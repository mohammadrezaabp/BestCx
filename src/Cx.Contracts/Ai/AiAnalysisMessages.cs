using System.Text.Json.Serialization;

namespace Cx.Contracts.Ai;

public static class AnalysisTypes
{
    public const string EventAnalysis = "event_analysis";
    public const string VoiceAnalysis = "voice_analysis";
}

public static class AnalysisEntityTypes
{
    public const string CxEvent = "cx_event";
    public const string VoiceCall = "voice_call";
    public const string Feedback = "feedback";
    public const string SupportCase = "support_case";
}

/// <summary>Job enqueued on cx.ai.analysis.request.v1 by the orchestrator.</summary>
public sealed class AiAnalysisRequest
{
    [JsonPropertyName("job_key")] public string JobKey { get; set; } = string.Empty;
    [JsonPropertyName("tenant_id")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("entity_type")] public string EntityType { get; set; } = AnalysisEntityTypes.CxEvent;
    [JsonPropertyName("entity_id")] public string EntityId { get; set; } = string.Empty;
    [JsonPropertyName("analysis_type")] public string AnalysisType { get; set; } = AnalysisTypes.EventAnalysis;
    [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }
    [JsonPropertyName("prompt_version")] public string PromptVersion { get; set; } = "1.0";
    [JsonPropertyName("taxonomy_version")] public string? TaxonomyVersion { get; set; }
    [JsonPropertyName("trigger_event_id")] public string? TriggerEventId { get; set; }
    [JsonPropertyName("correlation_id")] public string? CorrelationId { get; set; }
    [JsonPropertyName("requested_at")] public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Set by an operator-triggered reprocess; forces a new version even when one exists.</summary>
    [JsonPropertyName("force")] public bool Force { get; set; }
    [JsonPropertyName("supersedes_analysis_id")] public Guid? SupersedesAnalysisId { get; set; }
}

/// <summary>Model output published on cx.ai.analysis.result.v1, consumed by the result persister.</summary>
public sealed class AiAnalysisResultMessage
{
    [JsonPropertyName("job_key")] public string JobKey { get; set; } = string.Empty;
    [JsonPropertyName("tenant_id")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("entity_type")] public string EntityType { get; set; } = string.Empty;
    [JsonPropertyName("entity_id")] public string EntityId { get; set; } = string.Empty;
    [JsonPropertyName("analysis_type")] public string AnalysisType { get; set; } = string.Empty;
    [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }

    [JsonPropertyName("model_provider")] public string ModelProvider { get; set; } = string.Empty;
    [JsonPropertyName("model_name")] public string ModelName { get; set; } = string.Empty;
    [JsonPropertyName("model_version")] public string? ModelVersion { get; set; }
    [JsonPropertyName("decision_model")] public string? DecisionModel { get; set; }
    [JsonPropertyName("prompt_version")] public string PromptVersion { get; set; } = "1.0";
    [JsonPropertyName("taxonomy_version")] public string? TaxonomyVersion { get; set; }
    [JsonPropertyName("input_hash")] public string InputHash { get; set; } = string.Empty;

    [JsonPropertyName("result")] public AiAnalysisResult Result { get; set; } = new();
    [JsonPropertyName("status")] public string Status { get; set; } = "succeeded";
    [JsonPropertyName("supersedes_analysis_id")] public Guid? SupersedesAnalysisId { get; set; }
    [JsonPropertyName("latency_ms")] public long LatencyMs { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
