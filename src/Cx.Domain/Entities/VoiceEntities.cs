namespace Cx.Domain.Entities;

public sealed class VoiceCall
{
    public string Inbox { get; set; } = "feedback";
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string CallId { get; set; } = string.Empty;
    public string? CustomerId { get; set; }
    public string? AgentId { get; set; }
    public string? QueueId { get; set; }
    public string? Direction { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public long? DurationMs { get; set; }
    public string? EndReason { get; set; }
    public string? Provider { get; set; }
    public string? ProviderCallId { get; set; }
    public string? CorrelationId { get; set; }
    public string? ProductId { get; set; }
    public string LifecycleState { get; set; } = CallLifecycleStates.Discovered;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class VoiceRecording
{
    public Guid Id { get; set; }
    public Guid CallId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public string? Codec { get; set; }
    public int? SampleRate { get; set; }
    public int? Channels { get; set; }
    public long? SizeBytes { get; set; }
    public long? DurationMs { get; set; }
    public string? ChecksumSha256 { get; set; }
    public string? EncryptionKeyRef { get; set; }
    public string StorageState { get; set; } = RecordingStorageStates.Discovered;
    public string? SourceUri { get; set; }
    public string? Language { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

public sealed class VoiceTranscript
{
    public Guid Id { get; set; }
    public Guid RecordingId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string? Language { get; set; }
    public string Engine { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? ModelVersion { get; set; }
    public string Status { get; set; } = TranscriptStatuses.Pending;
    public decimal? AverageConfidence { get; set; }
    public string? TranscriptText { get; set; }
    public string? RedactedText { get; set; }
    public string? ArtifactObjectKey { get; set; }
    public string? RedactionMap { get; set; }
    public Guid? SupersedesTranscriptId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class VoiceTranscriptSegment
{
    public long Id { get; set; }
    public Guid TranscriptId { get; set; }
    public int SegmentIndex { get; set; }
    public string? Speaker { get; set; }
    public string? SpeakerRef { get; set; }
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? RedactedText { get; set; }
    public decimal? Confidence { get; set; }
}

/// <summary>Voice design 3 - every transition emits a canonical event.</summary>
public static class CallLifecycleStates
{
    public const string Discovered = "DISCOVERED";
    public const string Ringing = "RINGING";
    public const string Answered = "ANSWERED";
    public const string Ended = "ENDED";
    public const string RecordingDiscovered = "RECORDING_DISCOVERED";
    public const string RecordingStaging = "RECORDING_STAGING";
    public const string RecordingStored = "RECORDING_STORED";
    public const string RecordingVerified = "RECORDING_VERIFIED";
    public const string ReadyForTranscription = "READY_FOR_TRANSCRIPTION";
    public const string Transcribing = "TRANSCRIBING";
    public const string Transcribed = "TRANSCRIBED";
    public const string Analyzing = "ANALYZING";
    public const string Analyzed = "ANALYZED";
    public const string Reviewed = "REVIEWED";

    private static readonly string[] Order =
    {
        Discovered, Ringing, Answered, Ended,
        RecordingDiscovered, RecordingStaging, RecordingStored, RecordingVerified,
        ReadyForTranscription, Transcribing, Transcribed, Analyzing, Analyzed, Reviewed
    };

    public static int Rank(string state) => Array.IndexOf(Order, state);

    /// <summary>
    /// The workflow is idempotent and replay-safe: a redelivered message may re-assert the
    /// current state, but the lifecycle never moves backwards.
    /// </summary>
    public static bool CanTransition(string from, string to)
    {
        var f = Rank(from);
        var t = Rank(to);
        return f >= 0 && t >= 0 && t >= f;
    }

    public static string Max(string a, string b) => Rank(a) >= Rank(b) ? a : b;
}

/// <summary>Recording storage state machine. Only READY may be analysed (rule: technical design 11).</summary>
public static class RecordingStorageStates
{
    public const string Discovered = "DISCOVERED";
    public const string Staging = "STAGING";
    public const string Stored = "STORED";
    public const string Verified = "VERIFIED";
    public const string Ready = "READY";
    public const string Failed = "FAILED";

    private static readonly string[] Order = { Discovered, Staging, Stored, Verified, Ready };

    public static bool CanTransition(string from, string to)
    {
        if (to == Failed) return from != Ready;
        if (from == Failed) return to == Staging || to == Discovered;
        var f = Array.IndexOf(Order, from);
        var t = Array.IndexOf(Order, to);
        return f >= 0 && t >= 0 && t >= f;
    }

    public static bool IsAnalyzable(string state) => state == Ready;
}

public static class TranscriptStatuses
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
}
