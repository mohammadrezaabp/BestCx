namespace Cx.Domain.Entities;

/// <summary>Immutable raw fact. payload is never mutated after insert (rule R6).</summary>
public sealed class CxEvent
{
    public long Id { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string? CustomerId { get; set; }
    public string? AnonymousId { get; set; }
    public string? SessionId { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string EventCategory { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string? SourceType { get; set; }
    public string? SourceService { get; set; }
    public string? ProductId { get; set; }
    public string? ServiceId { get; set; }
    public string? TraceId { get; set; }
    public string? CorrelationId { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class OutboxEvent
{
    public Guid Id { get; set; }
    public string AggregateType { get; set; } = string.Empty;
    public string AggregateId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Null while Debezium is the publisher; set by the fallback relay.</summary>
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed class CustomerIdentity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public string? BusinessId { get; set; }
    public string? DisplayName { get; set; }
    public string? PhoneHash { get; set; }
    public string? EmailHash { get; set; }
    public string? Segment { get; set; }
    public string Attributes { get; set; } = "{}";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public Guid? MergedIntoId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Feedback
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string? EventId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string? CustomerId { get; set; }
    public string? ProductId { get; set; }
    public string FeedbackType { get; set; } = FeedbackTypes.Generic;
    public int? Rating { get; set; }
    public int? NpsScore { get; set; }
    public int? CsatScore { get; set; }
    public int? CesScore { get; set; }
    public string? SelectedTopic { get; set; }
    public string? Comment { get; set; }
    public string? Channel { get; set; }
    /// <summary>Set for a voice message: the voice_call that holds the audio and transcript.</summary>
    public string? CallId { get; set; }
    public string Status { get; set; } = InboxStatuses.New;
    public string? AssigneeId { get; set; }
    public bool Reviewed { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class FeedbackTypes
{
    public const string Generic = "generic";
    public const string Csat = "csat";
    public const string Nps = "nps";
    public const string Rating = "rating";
    public const string Voice = "voice";
    public const string Complaint = "complaint";
    public const string VoiceOfCustomer = "voc";
}

/// <summary>
/// A voice message's comment is its transcript. When the comment already holds context the
/// customer gave with the recording (the portal's request type and topic), the transcript is
/// written after this marker so the context survives transcription and every reprocess.
/// </summary>
public static class VoiceFeedbackComment
{
    public const string TranscriptMarker = "Voice message text:\n";
}

public static class InboxStatuses
{
    public const string New = "new";
    public const string InProgress = "in_progress";
    public const string Reviewed = "reviewed";
    public const string Closed = "closed";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { New, InProgress, Reviewed, Closed };
}

public sealed class SupportCase
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string? CustomerId { get; set; }
    public string? ProductId { get; set; }
    public string? Subject { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = "open";
    public string? Priority { get; set; }
    public string? Channel { get; set; }
    public string? AssigneeId { get; set; }
    public bool Escalated { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? FirstResponseAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string? Before { get; set; }
    public string? After { get; set; }
    public string? Reason { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public static class AuditActions
{
    public const string AiOverride = "ai.override";
    public const string AiApprove = "ai.approve";
    public const string AiReject = "ai.reject";
    public const string TranscriptEdit = "transcript.edit";
    public const string AudioAccess = "audio.access";
    public const string Export = "export";
    public const string Reprocess = "reprocess";
    public const string TaxonomyPublish = "taxonomy.publish";
    public const string TopicCreate = "topic.create";
    public const string TopicUpdate = "topic.update";
    public const string TopicDelete = "topic.delete";
    public const string RetentionChange = "retention.change";
    public const string RoleChange = "role.change";
    public const string IdentityMerge = "identity.merge";
    public const string DeleteAnonymize = "delete.anonymize";
    public const string FeedbackUpdate = "feedback.update";
    public const string FeedbackCreate = "feedback.create";
    public const string CustomerCreate = "customer.create";
    public const string CustomerUpdate = "customer.update";
}

public sealed class IntegrationCheckpoint
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string IntegrationName { get; set; } = string.Empty;
    public string IntegrationType { get; set; } = string.Empty;
    public string? Cursor { get; set; }
    public string Status { get; set; } = "unknown";
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public string? LastError { get; set; }
    public long RecordsProcessed { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
