using System.Diagnostics;
using System.Text.Json;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Jobs;
using Cx.Application.Observability;
using Cx.Application.Options;
using Cx.Application.Privacy;
using Cx.Application.Voice;
using Cx.Contracts;
using Cx.Contracts.Events;
using Cx.Contracts.Voice;
using Cx.Domain.Entities;
using Cx.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Voice.Worker;

/// <summary>
/// Transcription job runner (spec 17). Job key is
/// {recording_id}:transcribe:{model_version}, so a retry or a redelivery cannot produce
/// a second transcript for the same model, while a new model version legitimately can.
/// </summary>
public sealed class TranscriptionWorker : CxConsumerService
{
    public const string ConsumerGroup = "cx-voice-transcription";

    private readonly KafkaOptions _kafka;

    public TranscriptionWorker(
        IServiceScopeFactory scopeFactory,
        IMessageSubscriber subscriber,
        IOptions<KafkaOptions> kafka,
        ILogger<TranscriptionWorker> logger)
        : base(scopeFactory, subscriber, logger)
        => _kafka = kafka.Value;

    protected override SubscriptionOptions Subscription => new()
    {
        Topics = new[] { _kafka.Topics.VoiceTranscriptionRequest },
        GroupId = ConsumerGroup,
        DeadLetterTopic = _kafka.Topics.VoiceDlq,
        MaxAttempts = 3,
        InitialBackoff = TimeSpan.FromSeconds(2),
        MaxBackoff = TimeSpan.FromMinutes(2)
    };

    protected override async Task HandleAsync(BusMessage message, IServiceProvider services, CancellationToken ct)
    {
        TranscriptionRequest request;
        try
        {
            request = CxJson.Deserialize<TranscriptionRequest>(message.Value)
                      ?? throw new CxException(ErrorCategory.Schema, "empty transcription request");
        }
        catch (JsonException ex)
        {
            throw new CxException(ErrorCategory.Schema, "malformed transcription request", ex);
        }

        var voice = services.GetRequiredService<IVoiceRepository>();
        var jobs = services.GetRequiredService<IProcessingJobRepository>();
        var engine = services.GetRequiredService<ISpeechToTextEngine>();
        var storage = services.GetRequiredService<IObjectStorage>();
        var redaction = services.GetRequiredService<IRedactionService>();
        var publisher = services.GetRequiredService<IMessagePublisher>();
        var options = services.GetRequiredService<IOptions<VoiceOptions>>().Value;

        var recording = await voice.GetRecordingAsync(request.RecordingId, ct);
        if (recording is null)
            throw new CxException(ErrorCategory.Validation, $"recording {request.RecordingId} not found");

        // The hard gate from the technical design: unverified audio is never analysed.
        if (!RecordingStorageStates.IsAnalyzable(recording.StorageState))
            throw new CxException(ErrorCategory.Validation,
                $"recording {recording.Id} is in state {recording.StorageState}, not READY");

        var jobKey = string.IsNullOrWhiteSpace(request.JobKey)
            ? JobKeys.Transcribe(recording.Id, engine.ModelVersion ?? engine.Model)
            : request.JobKey;

        if (!await jobs.TryClaimAsync(request.TenantId, jobKey, ProcessingJobTypes.Transcribe, request.Force, ct))
        {
            Logger.LogDebug("Transcription job {JobKey} already handled", jobKey);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        using var activity = CxMetrics.Activity.StartActivity("cx.transcribe", ActivityKind.Consumer);
        activity?.SetTag("cx.call_id", request.CallId);
        activity?.SetTag("cx.job_key", jobKey);
        CxMetrics.SttJobsTotal.Add(1, new KeyValuePair<string, object?>("engine", engine.Engine));

        try
        {
            var call = await voice.GetCallAsync(request.TenantId, request.CallId, ct);
            if (call is not null)
                await voice.AdvanceCallStateAsync(call.Id, CallLifecycleStates.Transcribing, ct);

            TranscriptionResult result;
            await using (var audio = await storage.OpenReadAsync(recording.ObjectKey, ct))
            {
                result = await engine.TranscribeAsync(new TranscriptionAudio
                {
                    Content = audio,
                    ObjectKey = recording.ObjectKey,
                    ContentType = recording.ContentType,
                    Language = request.Language ?? recording.Language ?? options.DefaultLanguage,
                    DurationMs = recording.DurationMs,
                    Channels = recording.Channels
                }, ct);
            }

            var transcriptId = await PersistAsync(voice, storage, redaction, recording, result, request, ct);

            if (call is not null)
            {
                await voice.AdvanceCallStateAsync(call.Id, CallLifecycleStates.Transcribed, ct);

                // A voice message is also a feedback row: its text is the redacted transcript,
                // the same copy the model sees; the raw one stays behind the PII permission.
                var feedback = services.GetRequiredService<IFeedbackRepository>();
                await feedback.SetVoiceTranscriptAsync(request.TenantId, call.CallId,
                    redaction.Redact(result.Text).RedactedText, ct);
            }

            await jobs.CompleteAsync(jobKey, transcriptId.ToString(), ct);

            stopwatch.Stop();
            CxMetrics.SttLatency.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("engine", engine.Engine));

            await PublishTranscriptReadyAsync(publisher, call, recording, transcriptId, result, ct);

            Logger.LogInformation("Transcribed recording {RecordingId} with {Engine} in {Elapsed}ms ({Segments} segments)",
                recording.Id, engine.Engine, stopwatch.ElapsedMilliseconds, result.Segments.Count);
        }
        catch (Exception ex)
        {
            CxMetrics.SttFailuresTotal.Add(1, new KeyValuePair<string, object?>("engine", engine.Engine));
            var category = (ex as CxException)?.Category ?? ErrorCategory.Unknown;
            await jobs.FailAsync(jobKey, ex.Message, category, ErrorCategories.IsRetryable(category), ct);

            // The audio itself is untouched, so the job can be retried or reprocessed later.
            Logger.LogError(ex, "Transcription failed for recording {RecordingId}", recording.Id);
            throw;
        }
    }

    private static async Task<Guid> PersistAsync(IVoiceRepository voice, IObjectStorage storage,
        IRedactionService redaction, VoiceRecording recording, TranscriptionResult result,
        TranscriptionRequest request, CancellationToken ct)
    {
        // Two copies are kept: the raw transcript for authorized roles, and a redacted
        // copy that is the only version a general LLM ever sees (spec 19).
        var redactedFull = redaction.Redact(result.Text);

        var segments = result.Segments.Select(s =>
        {
            var redacted = redaction.Redact(s.Text);
            return new VoiceTranscriptSegment
            {
                SegmentIndex = s.Index,
                Speaker = s.Speaker,
                SpeakerRef = s.SpeakerRef,
                StartMs = s.StartMs,
                EndMs = s.EndMs,
                Text = s.Text,
                RedactedText = redacted.RedactedText,
                Confidence = s.Confidence
            };
        }).ToList();

        // The full structured artifact goes to object storage; the DB keeps what the
        // panel needs to query quickly (spec 18).
        var artifactKey = VoiceObjectKey.ArtifactKey(recording.ObjectKey, $"transcript-{result.Model}");
        using (var artifact = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(CxJson.Serialize(result))))
        {
            await storage.PutStreamAsync(artifactKey, artifact, "application/json", artifact.Length, ct);
        }

        var transcript = new VoiceTranscript
        {
            RecordingId = recording.Id,
            TenantId = request.TenantId,
            Language = result.Language,
            Engine = result.Engine,
            Model = result.Model,
            ModelVersion = result.ModelVersion,
            Status = TranscriptStatuses.Succeeded,
            AverageConfidence = result.AverageConfidence,
            TranscriptText = result.Text,
            RedactedText = redactedFull.RedactedText,
            ArtifactObjectKey = artifactKey,
            RedactionMap = CxJson.Serialize(new
            {
                rules = redactedFull.Hits.GroupBy(h => h.RuleName)
                    .ToDictionary(g => g.Key, g => g.Count()),
                total = redactedFull.Hits.Count
            })
        };

        return await voice.InsertTranscriptAsync(transcript, segments, ct);
    }

    private async Task PublishTranscriptReadyAsync(IMessagePublisher publisher, VoiceCall? call,
        VoiceRecording recording, Guid transcriptId, TranscriptionResult result, CancellationToken ct)
    {
        if (call is null) return;

        var canonical = new CanonicalEvent
        {
            EventId = Ulid.NewUlid().ToString(),
            SchemaVersion = EventSchema.CurrentVersion,
            EventName = EventNames.VoiceTranscriptReady,
            EventCategory = EventCategories.Voice,
            OccurredAt = DateTimeOffset.UtcNow,
            TenantId = call.TenantId,
            Customer = call.CustomerId is null ? null : new CustomerRef { CustomerId = call.CustomerId },
            Source = new SourceRef { Type = "worker", Service = "cx-voice-worker", Channel = "voice" },
            Voice = new VoiceRef { CallId = call.CallId, RecordingId = recording.Id.ToString() },
            Context = new ContextRef { CorrelationId = call.CorrelationId },
            Privacy = new PrivacyRef { ContainsPii = true, Classification = "restricted" }
        };

        canonical.Metadata["transcript_id"] = transcriptId.ToString();
        canonical.Metadata["engine"] = result.Engine;
        canonical.Metadata["model"] = result.Model;
        canonical.Metadata["segments"] = result.Segments.Count;
        canonical.Metadata["average_confidence"] = (double?)result.AverageConfidence;

        await publisher.PublishAsync(_kafka.Topics.RawEvents, canonical.PartitionKey(),
            CxJson.Serialize(canonical), null, ct);
    }
}
