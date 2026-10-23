using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Jobs;
using Cx.Application.Observability;
using Cx.Application.Options;
using Cx.Application.Voice;
using Cx.Contracts;
using Cx.Contracts.Events;
using Cx.Contracts.Voice;
using Cx.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Voice.Worker;

public sealed record RecordingIngestRequest(
    string TenantId,
    string CallId,
    string ProviderRecordingId,
    string? SourceUri,
    string? ContentType,
    long? DurationMs,
    int? Channels,
    int? SampleRate,
    string? Codec,
    string? Language,
    long? ExpectedSizeBytes);

/// <summary>
/// Moves a provider recording into object storage and drives the storage state machine
/// (spec 15). There is no shared transaction between S3 and PostgreSQL, so correctness
/// comes from the state machine plus an idempotent, deterministic object key:
/// DISCOVERED -> STAGING -> STORED -> VERIFIED -> READY, and only READY is analysable.
/// </summary>
public sealed class RecordingIngestService
{
    private readonly IVoiceRepository _voice;
    private readonly IObjectStorage _storage;
    private readonly IVoipConnector _voip;
    private readonly IMessagePublisher _publisher;
    private readonly IProcessingJobRepository _jobs;
    private readonly VoiceOptions _options;
    private readonly KafkaOptions _kafka;
    private readonly ILogger<RecordingIngestService> _logger;

    public RecordingIngestService(
        IVoiceRepository voice,
        IObjectStorage storage,
        IVoipConnector voip,
        IMessagePublisher publisher,
        IProcessingJobRepository jobs,
        IOptions<VoiceOptions> options,
        IOptions<KafkaOptions> kafka,
        ILogger<RecordingIngestService> logger)
    {
        _voice = voice;
        _storage = storage;
        _voip = voip;
        _publisher = publisher;
        _jobs = jobs;
        _options = options.Value;
        _kafka = kafka.Value;
        _logger = logger;
    }

    /// <summary>Pulls the recording from the VoIP provider and stores it.</summary>
    public Task<VoiceRecording?> IngestAsync(RecordingIngestRequest request, CancellationToken ct = default) =>
        IngestAsync(request, null, force: false, ct);

    /// <summary>
    /// Direct-upload path (voice design 2): the caller already holds the audio stream,
    /// so no provider fetch happens. Verification and the state machine are identical -
    /// an uploaded file gets exactly the same integrity guarantees as a pulled one.
    /// </summary>
    public Task<VoiceRecording?> IngestFromStreamAsync(RecordingIngestRequest request, Stream content,
        CancellationToken ct = default) =>
        // force: the caller is holding the actual bytes, so this attempt supersedes any
        // earlier failed pull for the same recording.
        IngestAsync(request, content, force: true, ct);

    private async Task<VoiceRecording?> IngestAsync(RecordingIngestRequest request, Stream? providedContent,
        bool force, CancellationToken ct)
    {
        var call = await _voice.GetCallAsync(request.TenantId, request.CallId, ct)
                   ?? throw new CxException(ErrorCategory.Validation,
                       $"cannot ingest a recording for unknown call '{request.CallId}'");

        var objectKey = VoiceObjectKey.Build(
            _options.ObjectKeyPrefix,
            request.TenantId,
            call.StartedAt ?? call.CreatedAt,
            request.CallId,
            request.ProviderRecordingId,
            request.ContentType,
            request.SourceUri);

        var jobKey = JobKeys.RecordingIngest(objectKey);
        if (!await _jobs.TryClaimAsync(request.TenantId, jobKey, ProcessingJobTypes.RecordingIngest, force, ct))
        {
            _logger.LogDebug("Recording ingest already handled for {ObjectKey}", objectKey);
            return await ExistingReadyRecordingAsync(call.Id, objectKey, ct);
        }

        try
        {
            var recording = await _voice.UpsertRecordingAsync(new VoiceRecording
            {
                CallId = call.Id,
                TenantId = request.TenantId,
                ObjectKey = objectKey,
                ContentType = request.ContentType,
                Codec = request.Codec,
                SampleRate = request.SampleRate,
                Channels = request.Channels,
                DurationMs = request.DurationMs,
                SourceUri = request.SourceUri,
                Language = request.Language ?? _options.DefaultLanguage,
                StorageState = RecordingStorageStates.Discovered
            }, ct);

            if (RecordingStorageStates.IsAnalyzable(recording.StorageState))
            {
                await _jobs.CompleteAsync(jobKey, recording.Id.ToString(), ct);
                return recording;
            }

            // STAGING -> STAGING is a legal no-op, so a re-attempt after a failed pull
            // resumes from the same point instead of tripping the state machine.
            if (recording.StorageState != RecordingStorageStates.Staging)
                await _voice.UpdateRecordingStateAsync(recording.Id, RecordingStorageStates.Staging, ct: ct);
            await _voice.AdvanceCallStateAsync(call.Id, CallLifecycleStates.RecordingStaging, ct);

            ObjectWriteResult stored;
            if (providedContent is not null)
            {
                stored = await _storage.PutStreamAsync(objectKey, providedContent, request.ContentType,
                    request.ExpectedSizeBytes, ct);
            }
            else
            {
                var descriptor = new VoipRecordingDescriptor(request.CallId, request.ProviderRecordingId,
                    request.SourceUri, request.ContentType, request.ExpectedSizeBytes, request.DurationMs,
                    request.Channels, request.SampleRate, request.Codec, request.Language);

                await using var source = await _voip.OpenRecordingStreamAsync(descriptor, ct);
                // Streamed straight through to storage; a long call is never buffered in memory.
                stored = await _storage.PutStreamAsync(objectKey, source, request.ContentType,
                    request.ExpectedSizeBytes, ct);
            }

            await _voice.UpdateRecordingStateAsync(recording.Id, RecordingStorageStates.Stored, stored, ct);
            await _voice.AdvanceCallStateAsync(call.Id, CallLifecycleStates.RecordingStored, ct);

            await VerifyAsync(recording.Id, objectKey, stored, request, ct);

            await _voice.UpdateRecordingStateAsync(recording.Id, RecordingStorageStates.Verified, ct: ct);
            await _voice.AdvanceCallStateAsync(call.Id, CallLifecycleStates.RecordingVerified, ct);

            await _voice.UpdateRecordingStateAsync(recording.Id, RecordingStorageStates.Ready, ct: ct);
            await _voice.AdvanceCallStateAsync(call.Id, CallLifecycleStates.ReadyForTranscription, ct);

            await _jobs.CompleteAsync(jobKey, recording.Id.ToString(), ct);
            CxMetrics.RecordingsStored.Add(1);

            var ready = await _voice.GetRecordingAsync(recording.Id, ct);
            if (ready is not null)
                await PublishReadyAsync(call, ready, ct);

            _logger.LogInformation("Recording {RecordingId} for call {CallId} is READY ({Bytes} bytes)",
                recording.Id, request.CallId, stored.SizeBytes);

            return ready;
        }
        catch (Exception ex)
        {
            CxMetrics.RecordingFailures.Add(1);
            var category = (ex as CxException)?.Category ?? ErrorCategory.Unknown;
            await _jobs.FailAsync(jobKey, ex.Message, category, ErrorCategories.IsRetryable(category), ct);
            throw;
        }
    }

    /// <summary>
    /// Integrity gate (voice design 5). A truncated or empty upload must never reach
    /// READY, because READY is the signal that says the audio can be analysed.
    /// </summary>
    private async Task VerifyAsync(Guid recordingId, string objectKey, ObjectWriteResult stored,
        RecordingIngestRequest request, CancellationToken ct)
    {
        if (stored.SizeBytes <= 0)
            throw await FailAsync(recordingId, "stored recording is empty", ct);

        if (stored.SizeBytes > _options.MaxRecordingBytes)
            throw await FailAsync(recordingId,
                $"stored recording is {stored.SizeBytes} bytes, above the {_options.MaxRecordingBytes} limit", ct);

        if (request.ExpectedSizeBytes is > 0 && request.ExpectedSizeBytes != stored.SizeBytes)
            throw await FailAsync(recordingId,
                $"size mismatch: provider reported {request.ExpectedSizeBytes}, stored {stored.SizeBytes}", ct);

        var stat = await _storage.StatAsync(objectKey, ct);
        if (stat is null)
            throw await FailAsync(recordingId, "object is not readable back from storage", ct);

        if (stat.SizeBytes != stored.SizeBytes)
            throw await FailAsync(recordingId,
                $"storage reports {stat.SizeBytes} bytes but {stored.SizeBytes} were written", ct);

        if (string.IsNullOrWhiteSpace(stored.ChecksumSha256))
            throw await FailAsync(recordingId, "checksum was not computed", ct);
    }

    private async Task<ObjectCorruptException> FailAsync(Guid recordingId, string reason, CancellationToken ct)
    {
        await _voice.UpdateRecordingStateAsync(recordingId, RecordingStorageStates.Failed, ct: ct);
        return new ObjectCorruptException($"recording {recordingId} failed verification: {reason}");
    }

    private async Task<VoiceRecording?> ExistingReadyRecordingAsync(Guid callId, string objectKey,
        CancellationToken ct)
    {
        var recordings = await _voice.GetRecordingsForCallAsync(callId, ct);
        return recordings.FirstOrDefault(r => r.ObjectKey == objectKey);
    }

    /// <summary>
    /// Announces the verified recording twice, deliberately:
    /// as a canonical event through Pipeline A (so it lands in cx_event and the timeline),
    /// and on the voice ready topic that the orchestrator watches.
    /// </summary>
    private async Task PublishReadyAsync(VoiceCall call, VoiceRecording recording, CancellationToken ct)
    {
        var canonical = new CanonicalEvent
        {
            EventId = Ulid.NewUlid().ToString(),
            SchemaVersion = EventSchema.CurrentVersion,
            EventName = EventNames.VoiceRecordingReady,
            EventCategory = EventCategories.Voice,
            OccurredAt = DateTimeOffset.UtcNow,
            TenantId = call.TenantId,
            Customer = call.CustomerId is null ? null : new CustomerRef { CustomerId = call.CustomerId },
            Source = new SourceRef { Type = "voip", Service = "cx-voice-worker", Channel = "voice" },
            Voice = new VoiceRef
            {
                CallId = call.CallId,
                RecordingId = recording.Id.ToString(),
                Direction = call.Direction,
                AgentId = call.AgentId,
                QueueId = call.QueueId,
                DurationMs = recording.DurationMs ?? call.DurationMs,
                Provider = call.Provider,
                ProviderCallId = call.ProviderCallId
            },
            Context = new ContextRef { CorrelationId = call.CorrelationId },
            Privacy = new PrivacyRef { ContainsPii = true, Classification = "restricted" }
        };

        // Object key and checksum are operational metadata, not customer data.
        canonical.Metadata["object_key"] = recording.ObjectKey;
        canonical.Metadata["checksum_sha256"] = recording.ChecksumSha256;
        canonical.Metadata["size_bytes"] = recording.SizeBytes;

        await _publisher.PublishAsync(_kafka.Topics.RawEvents, canonical.PartitionKey(),
            CxJson.Serialize(canonical), new Dictionary<string, string>
            {
                ["event_id"] = canonical.EventId,
                ["event_name"] = canonical.EventName
            }, ct);

        var ready = new VoiceRecordingReadyMessage
        {
            TenantId = call.TenantId,
            CallId = call.CallId,
            RecordingId = recording.Id,
            CustomerId = call.CustomerId,
            ObjectKey = recording.ObjectKey,
            ContentType = recording.ContentType,
            SizeBytes = recording.SizeBytes ?? 0,
            DurationMs = recording.DurationMs,
            ChecksumSha256 = recording.ChecksumSha256 ?? string.Empty,
            Language = recording.Language
        };

        await _publisher.PublishAsync(_kafka.Topics.VoiceRecordingReady, PartitionKeys.ForCall(call.CallId),
            CxJson.Serialize(ready), null, ct);
    }
}
