using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Jobs;
using Cx.Application.Options;
using Cx.Contracts;
using Cx.Contracts.Ai;
using Cx.Contracts.Events;
using Cx.Contracts.Messaging;
using Cx.Contracts.Voice;
using Cx.Domain.Entities;
using Cx.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Ai.Orchestrator;

/// <summary>
/// Pipeline B entry point (spec 20).
/// Consumes ONLY cx.persisted.events.v1 - the architectural rule that AI never reads the
/// raw stream (rule R2) is enforced here by the topic this worker subscribes to.
/// It routes: voice work ensures a transcript exists first, everything else goes straight
/// to standard analysis.
/// </summary>
public sealed class AiOrchestratorWorker : CxConsumerService
{
    public const string ConsumerGroup = "cx-ai-orchestrator";

    private readonly KafkaOptions _kafka;
    private readonly AiOptions _ai;

    /// <summary>
    /// Not every event deserves an LLM call. Analysing a page view would burn budget and
    /// bury real signal, so only events that carry customer meaning are routed.
    /// </summary>
    private static readonly HashSet<string> AnalyzableEvents = new(StringComparer.Ordinal)
    {
        EventNames.FeedbackSubmitted,
        EventNames.FeedbackCsatSubmitted,
        EventNames.FeedbackNpsSubmitted,
        EventNames.FeedbackRatingSubmitted,
        EventNames.SupportTicketCreated,
        EventNames.SupportTicketEscalated,
        EventNames.SupportTicketReplied,
        EventNames.SupportTicketResolved,
        EventNames.ApiRequestFailed,
        EventNames.ApiTimeout,
        EventNames.FormFailed,
        EventNames.UserLoginFailed
    };

    public AiOrchestratorWorker(
        IServiceScopeFactory scopeFactory,
        IMessageSubscriber subscriber,
        IOptions<KafkaOptions> kafka,
        IOptions<AiOptions> ai,
        ILogger<AiOrchestratorWorker> logger)
        : base(scopeFactory, subscriber, logger)
    {
        _kafka = kafka.Value;
        _ai = ai.Value;
    }

    protected override SubscriptionOptions Subscription => new()
    {
        Topics = new[] { _kafka.Topics.PersistedEvents },
        GroupId = ConsumerGroup,
        DeadLetterTopic = _kafka.Topics.AiDlq,
        MaxAttempts = 4
    };

    protected override async Task HandleAsync(BusMessage message, IServiceProvider services, CancellationToken ct)
    {
        PersistedEventMessage? persisted;
        try
        {
            persisted = CxJson.Deserialize<PersistedEventMessage>(message.Value);
        }
        catch (JsonException ex)
        {
            throw new CxException(ErrorCategory.Schema, "malformed persisted event", ex);
        }

        if (persisted?.Envelope is null) return;

        var envelope = persisted.Envelope.Deserialize<CanonicalEvent>(CxJson.Options);
        if (envelope is null) return;

        if (envelope.EventCategory == EventCategories.Voice)
        {
            await RouteVoiceAsync(services, persisted, envelope, ct);
            return;
        }

        if (!AnalyzableEvents.Contains(envelope.EventName)) return;

        await EnqueueAnalysisAsync(services, new AiAnalysisRequest
        {
            JobKey = JobKeys.Analyze(AnalysisEntityTypes.CxEvent, envelope.EventId, _ai.PromptVersion),
            TenantId = envelope.TenantId,
            EntityType = AnalysisEntityTypes.CxEvent,
            EntityId = envelope.EventId,
            AnalysisType = AnalysisTypes.EventAnalysis,
            CustomerId = envelope.Customer?.CustomerId,
            PromptVersion = _ai.PromptVersion,
            TriggerEventId = envelope.EventId,
            CorrelationId = envelope.Context?.CorrelationId
        }, ct);
    }

    private async Task RouteVoiceAsync(IServiceProvider services, PersistedEventMessage persisted,
        CanonicalEvent envelope, CancellationToken ct)
    {
        var callId = envelope.Voice?.CallId;
        if (callId is null) return;

        switch (envelope.EventName)
        {
            case EventNames.VoiceRecordingReady:
                await EnsureTranscriptAsync(services, envelope, callId, ct);
                break;

            case EventNames.VoiceTranscriptReady:
                await EnqueueAnalysisAsync(services, new AiAnalysisRequest
                {
                    JobKey = JobKeys.Analyze(AnalysisEntityTypes.VoiceCall, callId, _ai.PromptVersion),
                    TenantId = envelope.TenantId,
                    EntityType = AnalysisEntityTypes.VoiceCall,
                    EntityId = callId,
                    AnalysisType = AnalysisTypes.VoiceAnalysis,
                    CustomerId = envelope.Customer?.CustomerId ?? persisted.CustomerId,
                    PromptVersion = _ai.PromptVersion,
                    TriggerEventId = envelope.EventId,
                    CorrelationId = envelope.Context?.CorrelationId
                }, ct);
                break;
        }
    }

    /// <summary>
    /// "ensure transcript exists" (spec 20): if one is already stored, go straight to
    /// analysis; otherwise request transcription and let its ready event bring us back here.
    /// </summary>
    private async Task EnsureTranscriptAsync(IServiceProvider services, CanonicalEvent envelope, string callId,
        CancellationToken ct)
    {
        var voice = services.GetRequiredService<IVoiceRepository>();
        var publisher = services.GetRequiredService<IMessagePublisher>();
        var stt = services.GetRequiredService<ISpeechToTextEngine>();
        var voiceOptions = services.GetRequiredService<IOptions<VoiceOptions>>().Value;

        var call = await voice.GetCallAsync(envelope.TenantId, callId, ct);
        if (call is null)
        {
            // The projection worker consumes the same topic; ordering between the two is not
            // guaranteed. Retry so the call row has time to appear.
            throw new CxException(ErrorCategory.TransientDb,
                $"call '{callId}' is not projected yet; will retry");
        }

        var recordings = await voice.GetRecordingsForCallAsync(call.Id, ct);
        var ready = recordings.FirstOrDefault(r => RecordingStorageStates.IsAnalyzable(r.StorageState));
        if (ready is null)
        {
            Logger.LogDebug("No READY recording for call {CallId} yet", callId);
            return;
        }

        var transcript = await voice.GetLatestTranscriptAsync(ready.Id, ct);
        if (transcript is not null)
        {
            await EnqueueAnalysisAsync(services, new AiAnalysisRequest
            {
                JobKey = JobKeys.Analyze(AnalysisEntityTypes.VoiceCall, callId, _ai.PromptVersion),
                TenantId = envelope.TenantId,
                EntityType = AnalysisEntityTypes.VoiceCall,
                EntityId = callId,
                AnalysisType = AnalysisTypes.VoiceAnalysis,
                CustomerId = call.CustomerId,
                PromptVersion = _ai.PromptVersion,
                TriggerEventId = envelope.EventId,
                CorrelationId = call.CorrelationId
            }, ct);
            return;
        }

        if (!voiceOptions.AutoTranscribeOnReady)
        {
            Logger.LogInformation("Auto transcription is disabled; call {CallId} awaits a manual request", callId);
            return;
        }

        var request = new TranscriptionRequest
        {
            JobKey = JobKeys.Transcribe(ready.Id, stt.ModelVersion ?? stt.Model),
            TenantId = envelope.TenantId,
            CallId = callId,
            RecordingId = ready.Id,
            ObjectKey = ready.ObjectKey,
            Language = ready.Language ?? voiceOptions.DefaultLanguage,
            Engine = stt.Engine,
            Model = stt.Model,
            ModelVersion = stt.ModelVersion
        };

        await publisher.PublishAsync(_kafka.Topics.VoiceTranscriptionRequest, PartitionKeys.ForCall(callId),
            CxJson.Serialize(request), null, ct);

        Logger.LogInformation("Requested transcription for call {CallId} (recording {RecordingId})",
            callId, ready.Id);
    }

    private async Task EnqueueAnalysisAsync(IServiceProvider services, AiAnalysisRequest request,
        CancellationToken ct)
    {
        var analyses = services.GetRequiredService<IAiAnalysisRepository>();
        var publisher = services.GetRequiredService<IMessagePublisher>();

        if (!request.Force)
        {
            // Redelivery of a persisted event must not create a second analysis version.
            var existing = await analyses.GetLatestForEntityAsync(request.TenantId, request.EntityType,
                request.EntityId, ct);

            if (existing is not null && existing.PromptVersion == request.PromptVersion)
            {
                Logger.LogDebug("Analysis for {EntityType}:{EntityId} already exists at prompt {Prompt}",
                    request.EntityType, request.EntityId, request.PromptVersion);
                return;
            }
        }

        await publisher.PublishAsync(_kafka.Topics.AiAnalysisRequest, PartitionKeys.ForEntity(request.EntityId),
            CxJson.Serialize(request), new Dictionary<string, string>
            {
                ["analysis_type"] = request.AnalysisType,
                ["entity_type"] = request.EntityType
            }, ct);

        Logger.LogInformation("Enqueued {AnalysisType} for {EntityType}:{EntityId}",
            request.AnalysisType, request.EntityType, request.EntityId);
    }

}
