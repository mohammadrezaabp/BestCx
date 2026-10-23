using System.Diagnostics;
using System.Text.Json;
using Cx.Application.Abstractions;
using Cx.Application.Ai;
using Cx.Application.Common;
using Cx.Application.Jobs;
using Cx.Application.Observability;
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
/// Calls the model and publishes a schema-valid result (spec 21).
/// Context is materialized here rather than carried in the Kafka message: the orchestrator
/// owns routing, the worker owns retrieval, which keeps messages small and the context
/// fresh at the moment of the call.
/// </summary>
public sealed class AiAnalysisWorker : CxConsumerService
{
    public const string ConsumerGroup = "cx-ai-worker";

    private readonly KafkaOptions _kafka;
    private readonly AiOptions _ai;

    public AiAnalysisWorker(
        IServiceScopeFactory scopeFactory,
        IMessageSubscriber subscriber,
        IOptions<KafkaOptions> kafka,
        IOptions<AiOptions> ai,
        ILogger<AiAnalysisWorker> logger)
        : base(scopeFactory, subscriber, logger)
    {
        _kafka = kafka.Value;
        _ai = ai.Value;
    }

    protected override SubscriptionOptions Subscription => new()
    {
        Topics = new[] { _kafka.Topics.AiAnalysisRequest },
        GroupId = ConsumerGroup,
        DeadLetterTopic = _kafka.Topics.AiDlq,
        MaxAttempts = 3,
        InitialBackoff = TimeSpan.FromSeconds(3),
        MaxBackoff = TimeSpan.FromMinutes(2)
    };

    protected override async Task HandleAsync(BusMessage message, IServiceProvider services, CancellationToken ct)
    {
        AiAnalysisRequest request;
        try
        {
            request = CxJson.Deserialize<AiAnalysisRequest>(message.Value)
                      ?? throw new CxException(ErrorCategory.Schema, "empty analysis request");
        }
        catch (JsonException ex)
        {
            throw new CxException(ErrorCategory.Schema, "malformed analysis request", ex);
        }

        var jobs = services.GetRequiredService<IProcessingJobRepository>();
        var contextBuilder = services.GetRequiredService<IContextBuilder>();
        var prompts = services.GetRequiredService<IPromptBuilder>();
        var validator = services.GetRequiredService<IAiResultValidator>();
        var client = services.GetRequiredService<IAiCompletionClient>();
        var taxonomies = services.GetRequiredService<ITaxonomyRepository>();
        var decisionMaker = services.GetRequiredService<IDecisionMaker>();
        var publisher = services.GetRequiredService<IMessagePublisher>();

        var jobKey = string.IsNullOrWhiteSpace(request.JobKey)
            ? JobKeys.Analyze(request.EntityType, request.EntityId, request.PromptVersion)
            : request.JobKey;

        if (!await jobs.TryClaimAsync(request.TenantId, jobKey, ProcessingJobTypes.Analyze, request.Force, ct))
        {
            Logger.LogDebug("Analysis job {JobKey} already handled", jobKey);
            return;
        }

        using var activity = CxMetrics.Activity.StartActivity("cx.ai.analyze", ActivityKind.Consumer);
        activity?.SetTag("cx.entity_type", request.EntityType);
        activity?.SetTag("cx.entity_id", request.EntityId);
        CxMetrics.AiJobsTotal.Add(1, new KeyValuePair<string, object?>("analysis_type", request.AnalysisType));

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var context = request.AnalysisType == AnalysisTypes.VoiceAnalysis
                ? await contextBuilder.BuildVoiceContextAsync(request, ct)
                : await contextBuilder.BuildEventContextAsync(request, ct);

            // The decision model judges first (topic from the operator's tree, sentiment,
            // severity, ...); the text model then writes the summary for those decisions.
            var topics = await TopicCatalog.LoadAsync(taxonomies, request.TenantId, _ai.TaxonomyName, ct);
            var decision = await decisionMaker.DecideAsync(context, topics, ct);
            context.Decision = DecisionMaker.ToContext(decision, topics);

            var completion = await CompleteWithRepairAsync(client, prompts, validator, context, ct);
            DecisionMaker.Apply(completion.Value, decision, topics);

            stopwatch.Stop();
            CxMetrics.AiLatency.Record(completion.Completion.LatencyMs,
                new KeyValuePair<string, object?>("model", completion.Completion.Model));

            var result = new AiAnalysisResultMessage
            {
                JobKey = jobKey,
                TenantId = request.TenantId,
                EntityType = request.EntityType,
                EntityId = request.EntityId,
                AnalysisType = request.AnalysisType,
                CustomerId = context.CustomerId ?? request.CustomerId,
                ModelProvider = completion.Completion.Provider,
                ModelName = completion.Completion.Model,
                ModelVersion = completion.Completion.ModelVersion,
                DecisionModel = decision.Model,
                PromptVersion = prompts.PromptVersion,
                TaxonomyVersion = topics.IsEmpty ? null : topics.Version,
                InputHash = context.ComputeInputHash(prompts.PromptVersion, completion.Completion.Model),
                Result = completion.Value,
                Status = AiAnalysisStatuses.Succeeded,
                SupersedesAnalysisId = request.SupersedesAnalysisId,
                LatencyMs = stopwatch.ElapsedMilliseconds
            };

            await publisher.PublishAsync(_kafka.Topics.AiAnalysisResult,
                PartitionKeys.ForEntity(request.EntityId), CxJson.Serialize(result), null, ct);

            Logger.LogInformation(
                "Analysed {EntityType}:{EntityId}: {DecisionModel} decided {Category}/{Subcategory}, " +
                "{Severity} (confidence {Confidence}); {TextModel} wrote the summary",
                request.EntityType, request.EntityId, decision.Model,
                completion.Value.Category, completion.Value.Subcategory, completion.Value.Severity,
                completion.Value.Confidence, completion.Completion.Model);
        }
        catch (Exception ex)
        {
            CxMetrics.AiFailuresTotal.Add(1,
                new KeyValuePair<string, object?>("analysis_type", request.AnalysisType),
                new KeyValuePair<string, object?>("category", (ex as CxException)?.Category.ToString() ?? "unknown"));

            var category = (ex as CxException)?.Category ?? ErrorCategory.Unknown;
            await jobs.FailAsync(jobKey, ex.Message, category, ErrorCategories.IsRetryable(category), ct);

            // The source event and transcript are untouched; a reprocess can try again later.
            Logger.LogError(ex, "Analysis failed for {EntityType}:{EntityId}",
                request.EntityType, request.EntityId);
            throw;
        }
    }

    private sealed record ValidatedCompletion(AiAnalysisResult Value, AiCompletion Completion);

    /// <summary>
    /// Bounded JSON repair (technical design 19): one corrective round trip, then the job
    /// fails to the AI DLQ rather than looping on a model that will not comply.
    /// </summary>
    private async Task<ValidatedCompletion> CompleteWithRepairAsync(IAiCompletionClient client,
        IPromptBuilder prompts, IAiResultValidator validator, AnalysisContext context, CancellationToken ct)
    {
        var system = prompts.BuildSystemPrompt(context);
        var user = prompts.BuildUserPrompt(context);

        var completion = await client.CompleteJsonAsync(new AiCompletionRequest
        {
            SystemPrompt = system,
            UserPrompt = user,
            JsonSchema = validator.SchemaJson,
            Temperature = _ai.Temperature,
            MaxTokens = _ai.MaxTokens
        }, ct);

        var validation = validator.Validate(completion.Content);
        if (validation.IsValid) return new ValidatedCompletion(validation.Value!, completion);

        CxMetrics.AiOutputInvalidTotal.Add(1);

        for (var attempt = 0; attempt < _ai.MaxRepairAttempts; attempt++)
        {
            Logger.LogWarning("Model output failed validation ({Errors}); requesting a repair",
                string.Join("; ", validation.Errors.Take(3)));

            var repaired = await client.CompleteJsonAsync(new AiCompletionRequest
            {
                SystemPrompt = system,
                UserPrompt = prompts.BuildRepairPrompt(completion.Content, validation.Errors),
                JsonSchema = validator.SchemaJson,
                Temperature = 0m,
                MaxTokens = _ai.MaxTokens
            }, ct);

            validation = validator.Validate(repaired.Content);
            if (validation.IsValid) return new ValidatedCompletion(validation.Value!, repaired);

            CxMetrics.AiOutputInvalidTotal.Add(1);
            completion = repaired;
        }

        throw new AiOutputInvalidException(validation.Errors, completion.Content);
    }
}
