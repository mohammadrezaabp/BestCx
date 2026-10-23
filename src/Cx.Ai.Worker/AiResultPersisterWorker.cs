using System.Text.Json;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Options;
using Cx.Contracts;
using Cx.Contracts.Ai;
using Cx.Contracts.Events;
using Cx.Domain.Entities;
using Cx.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Ai.Worker;

/// <summary>
/// Persists model output as a new versioned row and closes
/// the loop back onto the event pipeline (spec 22, 23).
/// Persisting is separate from calling the model so a storage failure never costs an
/// expensive completion twice.
/// </summary>
public sealed class AiResultPersisterWorker : CxConsumerService
{
    public const string ConsumerGroup = "cx-ai-result-persister";

    private readonly KafkaOptions _kafka;

    public AiResultPersisterWorker(
        IServiceScopeFactory scopeFactory,
        IMessageSubscriber subscriber,
        IOptions<KafkaOptions> kafka,
        ILogger<AiResultPersisterWorker> logger)
        : base(scopeFactory, subscriber, logger)
    {
        _kafka = kafka.Value;
    }

    protected override SubscriptionOptions Subscription => new()
    {
        Topics = new[] { _kafka.Topics.AiAnalysisResult },
        GroupId = ConsumerGroup,
        DeadLetterTopic = _kafka.Topics.AiDlq,
        MaxAttempts = 5
    };

    protected override async Task HandleAsync(BusMessage message, IServiceProvider services, CancellationToken ct)
    {
        AiAnalysisResultMessage result;
        try
        {
            result = CxJson.Deserialize<AiAnalysisResultMessage>(message.Value)
                     ?? throw new CxException(ErrorCategory.Schema, "empty analysis result");
        }
        catch (JsonException ex)
        {
            throw new CxException(ErrorCategory.Schema, "malformed analysis result", ex);
        }

        var analyses = services.GetRequiredService<IAiAnalysisRepository>();
        var jobs = services.GetRequiredService<IProcessingJobRepository>();
        var voice = services.GetRequiredService<IVoiceRepository>();
        var publisher = services.GetRequiredService<IMessagePublisher>();

        // A redelivered result must not create a second version of the same analysis.
        var existing = await analyses.GetLatestForEntityAsync(result.TenantId, result.EntityType,
            result.EntityId, ct);

        if (existing is not null && existing.InputHash == result.InputHash &&
            existing.PromptVersion == result.PromptVersion)
        {
            Logger.LogDebug("Analysis for {EntityType}:{EntityId} with identical input hash already stored",
                result.EntityType, result.EntityId);
            await jobs.CompleteAsync(result.JobKey, existing.Id.ToString(), ct);
            return;
        }

        var analysisId = await analyses.InsertAsync(new AiAnalysis
        {
            TenantId = result.TenantId,
            EntityType = result.EntityType,
            EntityId = result.EntityId,
            CustomerId = result.CustomerId,
            AnalysisType = result.AnalysisType,
            ModelProvider = result.ModelProvider,
            ModelName = result.ModelName,
            ModelVersion = result.ModelVersion,
            DecisionModel = result.DecisionModel,
            PromptVersion = result.PromptVersion,
            TaxonomyVersion = result.TaxonomyVersion,
            InputHash = result.InputHash,
            Result = CxJson.Serialize(result.Result),
            Confidence = result.Result.Confidence,
            Status = AiAnalysisStatuses.Succeeded,
            SupersedesAnalysisId = result.SupersedesAnalysisId
        }, ct);

        await jobs.CompleteAsync(result.JobKey, analysisId.ToString(), ct);

        if (result.EntityType == AnalysisEntityTypes.VoiceCall)
            await CompleteVoiceAsync(voice, publisher, result, analysisId, ct);

        await PublishTimelineUpdatedAsync(publisher, result, analysisId, ct);
    }

    private async Task CompleteVoiceAsync(IVoiceRepository voice, IMessagePublisher publisher,
        AiAnalysisResultMessage result, Guid analysisId, CancellationToken ct)
    {
        var call = await voice.GetCallAsync(result.TenantId, result.EntityId, ct);
        if (call is null) return;

        await voice.AdvanceCallStateAsync(call.Id, CallLifecycleStates.Analyzed, ct);

        var canonical = new CanonicalEvent
        {
            EventId = Ulid.NewUlid().ToString(),
            SchemaVersion = EventSchema.CurrentVersion,
            EventName = EventNames.VoiceAnalysisCompleted,
            EventCategory = EventCategories.Ai,
            OccurredAt = DateTimeOffset.UtcNow,
            TenantId = result.TenantId,
            Customer = call.CustomerId is null ? null : new CustomerRef { CustomerId = call.CustomerId },
            Source = new SourceRef { Type = "worker", Service = "cx-ai-worker", Channel = "ai" },
            Voice = new VoiceRef { CallId = call.CallId },
            Context = new ContextRef { CorrelationId = call.CorrelationId },
            Privacy = new PrivacyRef { ContainsPii = false, Classification = "internal" }
        };

        canonical.Metadata["analysis_id"] = analysisId.ToString();
        canonical.Metadata["category"] = result.Result.Category;
        canonical.Metadata["severity"] = result.Result.Severity;
        canonical.Metadata["sentiment"] = result.Result.Sentiment;
        canonical.Metadata["confidence"] = (double)result.Result.Confidence;

        await publisher.PublishAsync(_kafka.Topics.RawEvents, canonical.PartitionKey(),
            CxJson.Serialize(canonical), null, ct);
    }

    /// <summary>Lets the panel refresh a customer view without polling every timeline.</summary>
    private async Task PublishTimelineUpdatedAsync(IMessagePublisher publisher, AiAnalysisResultMessage result,
        Guid analysisId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(result.CustomerId)) return;

        var notification = new
        {
            tenant_id = result.TenantId,
            customer_id = result.CustomerId,
            analysis_id = analysisId,
            entity_type = result.EntityType,
            entity_id = result.EntityId,
            category = result.Result.Category,
            severity = result.Result.Severity,
            sentiment = result.Result.Sentiment,
            requires_review = false,
            updated_at = DateTimeOffset.UtcNow
        };

        await publisher.PublishAsync(_kafka.Topics.CustomerTimelineUpdated,
            PartitionKeys.ForCustomer(result.TenantId, result.CustomerId),
            CxJson.Serialize(notification), null, ct);
    }
}
