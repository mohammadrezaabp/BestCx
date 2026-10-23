namespace Cx.Domain.Entities;

/// <summary>Versioned AI output. Never updated in place - a new row supersedes the old one (spec 22).</summary>
public sealed class AiAnalysis
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string? CustomerId { get; set; }
    public string AnalysisType { get; set; } = string.Empty;
    public string ModelProvider { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string? ModelVersion { get; set; }
    /// <summary>Model that made the judgements (topic, sentiment, severity, ...); ModelName wrote the text.</summary>
    public string? DecisionModel { get; set; }
    public string? PromptVersion { get; set; }
    public string? TaxonomyVersion { get; set; }
    public string? InputHash { get; set; }
    public string Result { get; set; } = "{}";
    public decimal? Confidence { get; set; }
    public string Status { get; set; } = AiAnalysisStatuses.Succeeded;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? SupersedesAnalysisId { get; set; }
}

public static class AiAnalysisStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Superseded = "superseded";
}

public sealed class HumanReview
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid AnalysisId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Status { get; set; } = HumanReviewStatuses.Pending;
    public string ReviewReason { get; set; } = string.Empty;
    public string? ReviewerId { get; set; }
    public string? PreviousResult { get; set; }
    public string? OverrideResult { get; set; }
    public string? Reason { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
}

public static class HumanReviewStatuses
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Overridden = "OVERRIDDEN";
    public const string Rejected = "REJECTED";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Pending, Approved, Overridden, Rejected };
}

/// <summary>Why an analysis entered the review queue (spec 23).</summary>
public static class ReviewReasons
{
    public const string LowConfidence = "low_confidence";
    public const string CriticalSeverity = "critical_severity";
    public const string UnknownCategory = "category_not_in_taxonomy";
    public const string PolicyRequired = "policy_required";
    public const string QaSample = "qa_sample";
}

public sealed class Taxonomy
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? ActiveVersionId { get; set; }
    /// <summary>Bumped by every topic edit; recorded on AI results as their taxonomy version.</summary>
    public int Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A node in the operator-maintained topic tree the AI classifies feedback into.</summary>
public sealed class Topic
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid TaxonomyId { get; set; }
    public Guid? ParentId { get; set; }
    /// <summary>Stable machine key stored on AI results. Generated, never shown for editing.</summary>
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// <summary>What belongs under this topic - the AI's main guidance for choosing it.</summary>
    public string? Description { get; set; }
    public string[] Keywords { get; set; } = Array.Empty<string>();
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TaxonomyVersion
{
    public Guid Id { get; set; }
    public Guid TaxonomyId { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Status { get; set; } = TaxonomyStatuses.Draft;
    public string Definition { get; set; } = "{}";
    public string? CreatedBy { get; set; }
    public string? PublishedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

public static class TaxonomyStatuses
{
    public const string Draft = "draft";
    public const string Review = "review";
    public const string Active = "active";
    public const string Retired = "retired";
}

/// <summary>Idempotency guard for every retryable pipeline job (spec 17, voice design 16).</summary>
public sealed class ProcessingJob
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string JobKey { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public string State { get; set; } = ProcessingJobStates.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public string? ErrorCategory { get; set; }
    public string? ResultRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class ProcessingJobStates
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string Succeeded = "SUCCEEDED";
    public const string FailedRetryable = "FAILED_RETRYABLE";
    public const string FailedFinal = "FAILED_FINAL";

    public static bool IsTerminal(string state) => state is Succeeded or FailedFinal;
}

public static class ProcessingJobTypes
{
    public const string Transcribe = "transcribe";
    public const string Analyze = "analyze";
    public const string RecordingIngest = "recording_ingest";
}
