using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Cx.Application.Ai;

public sealed class AnalysisContext
{
    [JsonPropertyName("tenant_id")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("analysis_type")] public string AnalysisType { get; set; } = string.Empty;
    [JsonPropertyName("entity_type")] public string EntityType { get; set; } = string.Empty;
    [JsonPropertyName("entity_id")] public string EntityId { get; set; } = string.Empty;
    [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }
    [JsonPropertyName("customer_segment")] public string? CustomerSegment { get; set; }

    [JsonPropertyName("current_event")] public ContextEvent? CurrentEvent { get; set; }
    [JsonPropertyName("previous_events")] public List<ContextEvent> PreviousEvents { get; set; } = new();
    [JsonPropertyName("open_support_cases")] public List<ContextSupportCase> OpenSupportCases { get; set; } = new();
    [JsonPropertyName("latest_feedback")] public List<ContextFeedback> LatestFeedback { get; set; } = new();

    [JsonPropertyName("call")] public ContextCall? Call { get; set; }
    /// <summary>Redacted transcript only - raw text never reaches a general LLM (spec 19).</summary>
    [JsonPropertyName("transcript")] public string? Transcript { get; set; }
    [JsonPropertyName("transcript_segments")] public List<ContextTranscriptSegment>? TranscriptSegments { get; set; }

    /// <summary>Judgements already made by the decision model; the text model writes around them.</summary>
    [JsonPropertyName("decision")] public ContextDecision? Decision { get; set; }
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }

    /// <summary>
    /// Stable hash of the model input. Lets a reprocess detect that nothing changed
    /// and supports reproducing a past decision (spec 22).
    /// </summary>
    public string ComputeInputHash(string promptVersion, string model)
    {
        var payload = new StringBuilder()
            .Append(promptVersion).Append('|').Append(model).Append('|')
            .Append(Cx.Contracts.CxJson.Serialize(this))
            .ToString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

public sealed class ContextDecision
{
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("subcategory")] public string? Subcategory { get; set; }
    /// <summary>Operator-facing topic path, e.g. «Payment › Port error».</summary>
    [JsonPropertyName("topic_title")] public string? TopicTitle { get; set; }
    [JsonPropertyName("sentiment")] public string? Sentiment { get; set; }
    [JsonPropertyName("intent")] public string? Intent { get; set; }
    [JsonPropertyName("severity")] public string? Severity { get; set; }
    [JsonPropertyName("resolution_status")] public string? ResolutionStatus { get; set; }
    [JsonPropertyName("journey_stage")] public string? JourneyStage { get; set; }
    [JsonPropertyName("follow_up_required")] public bool? FollowUpRequired { get; set; }
}

public sealed class ContextEvent
{
    [JsonPropertyName("event_id")] public string EventId { get; set; } = string.Empty;
    [JsonPropertyName("event_name")] public string EventName { get; set; } = string.Empty;
    [JsonPropertyName("occurred_at")] public DateTimeOffset OccurredAt { get; set; }
    [JsonPropertyName("product_id")] public string? ProductId { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    /// <summary>The customer's own words (redacted). Set on the event being analysed.</summary>
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("rating")] public int? Rating { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

public sealed class ContextSupportCase
{
    [JsonPropertyName("external_id")] public string ExternalId { get; set; } = string.Empty;
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("priority")] public string? Priority { get; set; }
    [JsonPropertyName("opened_at")] public DateTimeOffset OpenedAt { get; set; }
}

public sealed class ContextFeedback
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("rating")] public int? Rating { get; set; }
    [JsonPropertyName("comment")] public string? Comment { get; set; }
    [JsonPropertyName("submitted_at")] public DateTimeOffset SubmittedAt { get; set; }
}

public sealed class ContextCall
{
    [JsonPropertyName("call_id")] public string CallId { get; set; } = string.Empty;
    [JsonPropertyName("direction")] public string? Direction { get; set; }
    [JsonPropertyName("agent_id")] public string? AgentId { get; set; }
    [JsonPropertyName("queue_id")] public string? QueueId { get; set; }
    [JsonPropertyName("duration_ms")] public long? DurationMs { get; set; }
    [JsonPropertyName("end_reason")] public string? EndReason { get; set; }
    [JsonPropertyName("started_at")] public DateTimeOffset? StartedAt { get; set; }
}

public sealed class ContextTranscriptSegment
{
    [JsonPropertyName("speaker")] public string Speaker { get; set; } = string.Empty;
    [JsonPropertyName("start_ms")] public long StartMs { get; set; }
    [JsonPropertyName("end_ms")] public long EndMs { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
}
