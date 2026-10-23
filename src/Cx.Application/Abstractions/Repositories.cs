using Cx.Application.Common;
using Cx.Application.Query;
using Cx.Domain.Entities;

namespace Cx.Application.Abstractions;

public sealed record PersistResult(long RecordId, bool WasDuplicate, DateTimeOffset PersistedAt);

public interface IEventRepository
{
    /// <summary>
    /// Inserts cx_event and cx_outbox in one transaction (technical design 5.3).
    /// A duplicate event_id is an idempotent success and never writes a second outbox row.
    /// </summary>
    Task<PersistResult> PersistWithOutboxAsync(CxEvent cxEvent, Func<long, OutboxEvent> outboxFactory,
        CancellationToken ct = default);

    Task<CxEvent?> GetByEventIdAsync(string eventId, CancellationToken ct = default);

    Task<IReadOnlyList<CxEvent>> QueryAsync(EventQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<CxEvent>> GetRecentForCustomerAsync(string tenantId, string customerId, int limit,
        DateTimeOffset? since = null, CancellationToken ct = default);

    Task<long> CountAsync(string tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

public sealed class EventQuery
{
    public string TenantId { get; init; } = "bestcx";
    public string? CustomerId { get; init; }
    public string? SessionId { get; init; }
    public string? EventName { get; init; }
    public string? EventCategory { get; init; }
    public string? ProductId { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public Cursor? Cursor { get; init; }
    public int Limit { get; init; } = 50;
}

public interface IOutboxRepository
{
    Task<IReadOnlyList<OutboxEvent>> FetchUnpublishedAsync(int batchSize, CancellationToken ct = default);
    Task MarkPublishedAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default);
    Task<long> CountUnpublishedAsync(CancellationToken ct = default);
    Task<TimeSpan?> OldestUnpublishedAgeAsync(CancellationToken ct = default);
}

public interface ICustomerRepository
{
    Task UpsertFromEventAsync(string tenantId, string customerId, string? businessId, DateTimeOffset seenAt,
        CancellationToken ct = default);

    Task<CustomerIdentity?> GetAsync(string tenantId, string customerId, CancellationToken ct = default);

    Task<Page<CustomerSummary>> SearchAsync(string tenantId, string? term, string? segment, Cursor? cursor, int limit,
        CancellationToken ct = default);

    Task<CustomerSummary?> GetSummaryAsync(string tenantId, string customerId, CancellationToken ct = default);

    /// <summary>Defines a customer by hand. False when the id is already taken.</summary>
    Task<bool> CreateAsync(string tenantId, string customerId, CustomerProfileInput profile,
        CancellationToken ct = default);

    /// <summary>Edits the operator-maintained profile. False when the customer does not exist.</summary>
    Task<bool> UpdateProfileAsync(string tenantId, string customerId, CustomerProfileInput profile,
        CancellationToken ct = default);

    Task MergeAsync(string tenantId, string sourceCustomerId, string targetCustomerId, CancellationToken ct = default);
}

public interface IFeedbackRepository
{
    Task<Guid> UpsertAsync(Feedback feedback, CancellationToken ct = default);
    Task<Feedback?> GetAsync(string tenantId, Guid id, CancellationToken ct = default);
    Task<Page<Feedback>> QueryAsync(InboxQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<Feedback>> GetRecentForCustomerAsync(string tenantId, string customerId, int limit,
        CancellationToken ct = default);
    Task UpdateReviewStateAsync(string tenantId, Guid id, string? status, string? assigneeId, bool? reviewed,
        CancellationToken ct = default);
    Task<NumberedPage<FeedbackGridRow>> QueryGridAsync(FeedbackGridQuery query, CancellationToken ct = default);
    /// <summary>Stores the (redacted) transcript of a voice message as its feedback text.</summary>
    Task SetVoiceTranscriptAsync(string tenantId, string callId, string transcript, CancellationToken ct = default);
}

public interface ISupportCaseRepository
{
    Task<Guid> UpsertAsync(SupportCase supportCase, CancellationToken ct = default);
    Task<SupportCase?> GetAsync(string tenantId, Guid id, CancellationToken ct = default);
    Task<Page<SupportCase>> QueryAsync(InboxQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<SupportCase>> GetOpenForCustomerAsync(string tenantId, string customerId, int limit,
        CancellationToken ct = default);
    Task<IReadOnlyList<SupportCase>> GetRecentForCustomerAsync(string tenantId, string customerId, int limit,
        CancellationToken ct = default);
}

public sealed class InboxQuery
{
    public string TenantId { get; init; } = "bestcx";
    public string? CustomerId { get; init; }
    public string? ProductId { get; init; }
    public string? Status { get; init; }
    public string? AssigneeId { get; init; }
    public bool? Reviewed { get; init; }
    public string? Search { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public Cursor? Cursor { get; init; }
    public int Limit { get; init; } = 50;
}

public interface IVoiceRepository
{
    Task<VoiceCall> UpsertCallAsync(VoiceCall call, CancellationToken ct = default);
    Task<VoiceCall?> GetCallAsync(string tenantId, string callId, CancellationToken ct = default);
    Task<VoiceCall?> GetCallByIdAsync(string tenantId, Guid id, CancellationToken ct = default);
    Task<Page<VoiceCall>> QueryCallsAsync(VoiceCallQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<VoiceCall>> GetRecentForCustomerAsync(string tenantId, string customerId, int limit,
        CancellationToken ct = default);
    Task AdvanceCallStateAsync(Guid callId, string state, CancellationToken ct = default);

    Task<VoiceRecording> UpsertRecordingAsync(VoiceRecording recording, CancellationToken ct = default);
    Task<VoiceRecording?> GetRecordingAsync(Guid recordingId, CancellationToken ct = default);
    Task<IReadOnlyList<VoiceRecording>> GetRecordingsForCallAsync(Guid callId, CancellationToken ct = default);
    Task UpdateRecordingStateAsync(Guid recordingId, string state, ObjectWriteResult? stored = null,
        CancellationToken ct = default);

    Task<Guid> InsertTranscriptAsync(VoiceTranscript transcript, IReadOnlyList<VoiceTranscriptSegment> segments,
        CancellationToken ct = default);
    Task<VoiceTranscript?> GetLatestTranscriptAsync(Guid recordingId, CancellationToken ct = default);
    Task<VoiceTranscript?> GetTranscriptAsync(Guid transcriptId, CancellationToken ct = default);
    Task<IReadOnlyList<VoiceTranscriptSegment>> GetSegmentsAsync(Guid transcriptId, CancellationToken ct = default);
    Task UpdateSegmentTextAsync(Guid transcriptId, int segmentIndex, string text, CancellationToken ct = default);
}

public sealed class VoiceCallQuery
{
    public string Inbox { get; init; } = "feedback";
    public string TenantId { get; init; } = "bestcx";
    public string? CustomerId { get; init; }
    public string? AgentId { get; init; }
    public string? Direction { get; init; }
    public string? ProductId { get; init; }
    public string? LifecycleState { get; init; }
    public string? Search { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public Cursor? Cursor { get; init; }
    public int Limit { get; init; } = 50;
}

public interface IAiAnalysisRepository
{
    Task<Guid> InsertAsync(AiAnalysis analysis, CancellationToken ct = default);
    Task<AiAnalysis?> GetAsync(string tenantId, Guid id, CancellationToken ct = default);
    Task<AiAnalysis?> GetLatestForEntityAsync(string tenantId, string entityType, string entityId,
        CancellationToken ct = default);
    Task<IReadOnlyList<AiAnalysis>> GetVersionsForEntityAsync(string tenantId, string entityType, string entityId,
        CancellationToken ct = default);
    Task<IReadOnlyList<AiAnalysis>> GetRecentForCustomerAsync(string tenantId, string customerId, int limit,
        CancellationToken ct = default);
    Task MarkSupersededAsync(Guid analysisId, CancellationToken ct = default);
}

public interface IHumanReviewRepository
{
    Task<Guid> CreateAsync(HumanReview review, CancellationToken ct = default);
    Task<HumanReview?> GetAsync(string tenantId, Guid id, CancellationToken ct = default);
    Task<HumanReview?> GetByAnalysisAsync(string tenantId, Guid analysisId, CancellationToken ct = default);
    Task<Page<HumanReviewListItem>> QueryAsync(ReviewQuery query, CancellationToken ct = default);
    Task ResolveAsync(Guid id, string status, string reviewerId, string? overrideResult, string? reason, string? notes,
        CancellationToken ct = default);
    Task<long> BacklogCountAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>
/// Read model for the review queue. A settable-property class rather than a positional
/// record: Dapper binds constructors by exact parameter type, which makes records brittle
/// against provider type mapping (timestamptz to DateTimeOffset, nullable numerics).
/// </summary>
public sealed class HumanReviewListItem
{
    public Guid Id { get; set; }
    public Guid AnalysisId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string? CustomerId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ReviewReason { get; set; } = string.Empty;
    public decimal? Confidence { get; set; }
    public string? Sentiment { get; set; }
    public string? Category { get; set; }
    public string? Severity { get; set; }
    public string? Summary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewerId { get; set; }
}

public sealed class ReviewQuery
{
    public string TenantId { get; init; } = "bestcx";
    public string? Status { get; init; }
    public string? EntityType { get; init; }
    public string? ReviewReason { get; init; }
    public Cursor? Cursor { get; init; }
    public int Limit { get; init; } = 50;
}

/// <summary>
/// The operator-maintained topic tree. Edits apply immediately: every change bumps the
/// taxonomy revision, which AI results record so a classification stays traceable.
/// </summary>
public interface ITaxonomyRepository
{
    /// <summary>The taxonomy row, created on first use so a fresh tenant can start adding topics.</summary>
    Task<Taxonomy> EnsureAsync(string tenantId, string name, CancellationToken ct = default);
    Task<IReadOnlyList<Topic>> ListTopicsAsync(Guid taxonomyId, CancellationToken ct = default);
    Task<Topic?> GetTopicAsync(Guid taxonomyId, Guid topicId, CancellationToken ct = default);
    Task CreateTopicAsync(Topic topic, CancellationToken ct = default);
    Task UpdateTopicAsync(Topic topic, CancellationToken ct = default);
    Task DeleteTopicAsync(Guid taxonomyId, Guid topicId, CancellationToken ct = default);
    /// <summary>How many AI analyses were classified under each topic code.</summary>
    Task<IReadOnlyDictionary<string, int>> CountUsageAsync(string tenantId, CancellationToken ct = default);
}

public interface IAuditRepository
{
    Task WriteAsync(AuditLog entry, CancellationToken ct = default);
    Task<Page<AuditLog>> QueryAsync(AuditQuery query, CancellationToken ct = default);
}

public sealed class AuditQuery
{
    public string TenantId { get; init; } = "bestcx";
    public string? ActorId { get; init; }
    public string? Action { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public Cursor? Cursor { get; init; }
    public int Limit { get; init; } = 50;
}

public interface IProcessingJobRepository
{
    /// <summary>
    /// Idempotency gate. Returns false when the job key already reached a terminal state
    /// or is currently running (spec 17, voice design 16).
    /// </summary>
    Task<bool> TryClaimAsync(string tenantId, string jobKey, string jobType, bool force = false,
        CancellationToken ct = default);

    Task CompleteAsync(string jobKey, string? resultRef, CancellationToken ct = default);
    Task FailAsync(string jobKey, string error, ErrorCategory category, bool retryable, CancellationToken ct = default);
    Task<ProcessingJob?> GetAsync(string jobKey, CancellationToken ct = default);
    Task<IReadOnlyList<ProcessingJob>> ListFailedAsync(string tenantId, int limit, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, long>> CountByStateAsync(string tenantId, CancellationToken ct = default);
}

public interface IIntegrationCheckpointRepository
{
    Task<IntegrationCheckpoint?> GetAsync(string tenantId, string integrationName, CancellationToken ct = default);
    Task<IReadOnlyList<IntegrationCheckpoint>> ListAsync(string tenantId, CancellationToken ct = default);
    Task SaveCursorAsync(string tenantId, string integrationName, string integrationType, string? cursor,
        long processed, CancellationToken ct = default);
    Task RecordErrorAsync(string tenantId, string integrationName, string integrationType, string error,
        CancellationToken ct = default);
}
