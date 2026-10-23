using System.Text.Json.Serialization;
using Cx.Application.Abstractions;
using Cx.Application.Common;

namespace Cx.Application.Query;

/// <summary>
/// One row of the merged Customer 360 timeline (spec 24): CX events, feedback,
/// support cases, voice calls and AI insights in a single time-ordered stream.
/// </summary>
public sealed class TimelineEntry
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("occurred_at")] public DateTimeOffset OccurredAt { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("product_id")] public string? ProductId { get; set; }
    [JsonPropertyName("severity")] public string? Severity { get; set; }
    [JsonPropertyName("sentiment")] public string? Sentiment { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("correlation_id")] public string? CorrelationId { get; set; }
    [JsonPropertyName("reviewed")] public bool Reviewed { get; set; }
}

public static class TimelineKinds
{
    public const string Event = "event";
    public const string Feedback = "feedback";
    public const string Support = "support";
    public const string Voice = "voice";
    public const string Insight = "insight";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Event, Feedback, Support, Voice, Insight };
}

public sealed class TimelineQuery
{
    public string TenantId { get; init; } = "bestcx";
    public required string CustomerId { get; init; }
    public IReadOnlyList<string>? Kinds { get; init; }
    public string? ProductId { get; init; }
    public string? Severity { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public Cursor? Cursor { get; init; }
    public int Limit { get; init; } = 50;
}

public sealed class OverviewSnapshot
{
    [JsonPropertyName("from")] public DateTimeOffset From { get; set; }
    [JsonPropertyName("to")] public DateTimeOffset To { get; set; }
    [JsonPropertyName("total_interactions")] public long TotalInteractions { get; set; }
    [JsonPropertyName("active_customers")] public long ActiveCustomers { get; set; }
    [JsonPropertyName("feedback_count")] public long FeedbackCount { get; set; }
    [JsonPropertyName("nps")] public NpsSummary Nps { get; set; } = new(0, 0, 0);
    [JsonPropertyName("support_cases")] public long SupportCases { get; set; }
    [JsonPropertyName("call_count")] public long CallCount { get; set; }
    [JsonPropertyName("negative_sentiment_share")] public double NegativeSentimentShare { get; set; }
    [JsonPropertyName("unresolved_high_severity")] public long UnresolvedHighSeverity { get; set; }
    [JsonPropertyName("ai_review_backlog")] public long AiReviewBacklog { get; set; }
    [JsonPropertyName("data_quality_warnings")] public long DataQualityWarnings { get; set; }
    [JsonPropertyName("top_pain_points")] public List<NamedCount> TopPainPoints { get; set; } = new();
    [JsonPropertyName("top_affected_products")] public List<NamedCount> TopAffectedProducts { get; set; } = new();
    [JsonPropertyName("daily_trend")] public List<TrendPoint> DailyTrend { get; set; } = new();
    [JsonPropertyName("sentiment_breakdown")] public List<NamedCount> SentimentBreakdown { get; set; } = new();
}

// These are materialized by Dapper, so they are classes with settable properties:
// binding through a positional record's constructor requires an exact type match and
// breaks on provider mappings such as timestamptz to DateTimeOffset.
public sealed class NamedCount
{
    public NamedCount() { }

    public NamedCount(string name, long count)
    {
        Name = name;
        Count = count;
    }

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("count")] public long Count { get; set; }
}

public sealed class TrendPoint
{
    [JsonPropertyName("date")] public DateTimeOffset Date { get; set; }
    [JsonPropertyName("events")] public long Events { get; set; }
    [JsonPropertyName("negative")] public long Negative { get; set; }
    [JsonPropertyName("calls")] public long Calls { get; set; }
}

public sealed class CustomerInsights
{
    [JsonPropertyName("customer_id")] public string CustomerId { get; set; } = string.Empty;
    [JsonPropertyName("current_sentiment")] public string? CurrentSentiment { get; set; }
    [JsonPropertyName("dominant_intent")] public string? DominantIntent { get; set; }
    [JsonPropertyName("journey_stage")] public string? JourneyStage { get; set; }
    [JsonPropertyName("latest_summary")] public string? LatestSummary { get; set; }
    [JsonPropertyName("recurring_issues")] public List<NamedCount> RecurringIssues { get; set; } = new();
    [JsonPropertyName("unresolved_issues")] public long UnresolvedIssues { get; set; }
    [JsonPropertyName("open_support_cases")] public long OpenSupportCases { get; set; }
    [JsonPropertyName("last_interaction_at")] public DateTimeOffset? LastInteractionAt { get; set; }
    [JsonPropertyName("human_reviewed_labels")] public List<NamedCount> HumanReviewedLabels { get; set; } = new();
}

public sealed record DataQualityCheck(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("count")] long Count,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("sample")] IReadOnlyList<string> Sample);

public sealed class PipelineHealthSnapshot
{
    [JsonPropertyName("outbox_backlog")] public long OutboxBacklog { get; set; }
    [JsonPropertyName("outbox_lag_seconds")] public double? OutboxLagSeconds { get; set; }
    [JsonPropertyName("events_last_hour")] public long EventsLastHour { get; set; }
    [JsonPropertyName("events_last_24h")] public long EventsLast24H { get; set; }
    [JsonPropertyName("review_backlog")] public long ReviewBacklog { get; set; }
    [JsonPropertyName("jobs_by_state")] public Dictionary<string, long> JobsByState { get; set; } = new();
    [JsonPropertyName("recordings_by_state")] public Dictionary<string, long> RecordingsByState { get; set; } = new();
    [JsonPropertyName("failed_jobs")] public List<FailedJobSummary> FailedJobs { get; set; } = new();
    [JsonPropertyName("ai_analyses_last_24h")] public long AiAnalysesLast24H { get; set; }
    [JsonPropertyName("transcripts_last_24h")] public long TranscriptsLast24H { get; set; }
}

public sealed class FailedJobSummary
{
    [JsonPropertyName("job_key")] public string JobKey { get; set; } = string.Empty;
    [JsonPropertyName("job_type")] public string JobType { get; set; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;
    [JsonPropertyName("attempts")] public int Attempts { get; set; }
    [JsonPropertyName("error_category")] public string? ErrorCategory { get; set; }
    [JsonPropertyName("last_error")] public string? LastError { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
}

public interface IInsightsRepository
{
    Task<Page<TimelineEntry>> GetTimelineAsync(TimelineQuery query, CancellationToken ct = default);
    Task<OverviewSnapshot> GetOverviewAsync(string tenantId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default);
    Task<CustomerInsights> GetCustomerInsightsAsync(string tenantId, string customerId,
        CancellationToken ct = default);
    Task<IReadOnlyList<DataQualityCheck>> GetDataQualityAsync(string tenantId, CancellationToken ct = default);
    Task<PipelineHealthSnapshot> GetPipelineHealthAsync(string tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<GlobalSearchHit>> GlobalSearchAsync(string tenantId, string term, int limit,
        CancellationToken ct = default);
}

public sealed class GlobalSearchHit
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }
    [JsonPropertyName("occurred_at")] public DateTimeOffset? OccurredAt { get; set; }
}
