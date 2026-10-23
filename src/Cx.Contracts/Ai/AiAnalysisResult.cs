using System.Text.Json.Serialization;

namespace Cx.Contracts.Ai;

/// <summary>
/// Strict AI output contract (spec 21). Model output is validated against the JSON schema
/// in Schemas/ai-analysis-result.v1.schema.json before anything is persisted.
/// </summary>
public sealed class AiAnalysisResult
{
    [JsonPropertyName("analysis_version")] public string AnalysisVersion { get; set; } = "1.0";
    [JsonPropertyName("sentiment")] public string Sentiment { get; set; } = Sentiments.Unknown;
    [JsonPropertyName("intent")] public string Intent { get; set; } = string.Empty;
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
    [JsonPropertyName("subcategory")] public string? Subcategory { get; set; }
    [JsonPropertyName("severity")] public string Severity { get; set; } = Severities.Low;
    [JsonPropertyName("journey_stage")] public string? JourneyStage { get; set; }
    [JsonPropertyName("resolution_status")] public string ResolutionStatus { get; set; } = ResolutionStatuses.Unknown;
    [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("action_items")] public List<string> ActionItems { get; set; } = new();
    [JsonPropertyName("mentioned_products")] public List<string> MentionedProducts { get; set; } = new();
    [JsonPropertyName("confidence")] public decimal Confidence { get; set; }

    // voice extension (spec 21)
    [JsonPropertyName("call_reason")] public string? CallReason { get; set; }
    [JsonPropertyName("follow_up_required")] public bool? FollowUpRequired { get; set; }
    [JsonPropertyName("key_moments")] public List<AiKeyMoment>? KeyMoments { get; set; }
}

public sealed class AiKeyMoment
{
    [JsonPropertyName("start_ms")] public long StartMs { get; set; }
    [JsonPropertyName("end_ms")] public long EndMs { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = KeyMomentTypes.Other;
    [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
}

public static class Sentiments
{
    public const string Positive = "positive";
    public const string Neutral = "neutral";
    public const string Negative = "negative";
    public const string Mixed = "mixed";
    public const string Unknown = "unknown";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Positive, Neutral, Negative, Mixed, Unknown };
}

/// <summary>
/// System vocabulary for why a customer reached out. It is part of the analysis contract,
/// not something operators maintain: they curate topics, the platform owns intents.
/// </summary>
public static class Intents
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        "technical_support", "billing_inquiry", "complaint", "feature_request",
        "cancellation", "information_request", "praise", "unknown"
    };
}

public static class JourneyStages
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        "awareness", "onboarding", "activation", "usage", "support", "renewal", "churn_risk"
    };
}

/// <summary>Category recorded when no topic in the tree fits; it always routes to human review.</summary>
public static class TopicCodes
{
    public const string Unclassified = "unclassified";
}

public static class Severities
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Critical = "critical";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Low, Medium, High, Critical };
}

public static class ResolutionStatuses
{
    public const string Resolved = "resolved";
    public const string PartiallyResolved = "partially_resolved";
    public const string Unresolved = "unresolved";
    public const string Unknown = "unknown";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Resolved, PartiallyResolved, Unresolved, Unknown };
}

public static class KeyMomentTypes
{
    public const string ProblemStatement = "problem_statement";
    public const string Complaint = "complaint";
    public const string Resolution = "resolution";
    public const string Commitment = "commitment";
    public const string Escalation = "escalation";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { ProblemStatement, Complaint, Resolution, Commitment, Escalation, Other };
}
