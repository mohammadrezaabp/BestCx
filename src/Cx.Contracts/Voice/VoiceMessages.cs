using System.Text.Json.Serialization;

namespace Cx.Contracts.Voice;

/// <summary>Published only after the stored object is verified (voice design 5).</summary>
public sealed class VoiceRecordingReadyMessage
{
    [JsonPropertyName("tenant_id")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("call_id")] public string CallId { get; set; } = string.Empty;
    [JsonPropertyName("recording_id")] public Guid RecordingId { get; set; }
    [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }
    [JsonPropertyName("object_key")] public string ObjectKey { get; set; } = string.Empty;
    [JsonPropertyName("content_type")] public string? ContentType { get; set; }
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("duration_ms")] public long? DurationMs { get; set; }
    [JsonPropertyName("checksum_sha256")] public string ChecksumSha256 { get; set; } = string.Empty;
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("ready_at")] public DateTimeOffset ReadyAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Job key: recording_id:transcribe:model_version (spec 17).</summary>
public sealed class TranscriptionRequest
{
    [JsonPropertyName("job_key")] public string JobKey { get; set; } = string.Empty;
    [JsonPropertyName("tenant_id")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("call_id")] public string CallId { get; set; } = string.Empty;
    [JsonPropertyName("recording_id")] public Guid RecordingId { get; set; }
    [JsonPropertyName("object_key")] public string ObjectKey { get; set; } = string.Empty;
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("engine")] public string? Engine { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("model_version")] public string? ModelVersion { get; set; }
    [JsonPropertyName("force")] public bool Force { get; set; }
    [JsonPropertyName("requested_at")] public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TranscriptSegmentDto
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("speaker")] public string Speaker { get; set; } = SpeakerLabels.Unknown;
    [JsonPropertyName("speaker_ref")] public string? SpeakerRef { get; set; }
    [JsonPropertyName("start_ms")] public long StartMs { get; set; }
    [JsonPropertyName("end_ms")] public long EndMs { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("confidence")] public decimal? Confidence { get; set; }
}

public sealed class TranscriptionResult
{
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("engine")] public string Engine { get; set; } = string.Empty;
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("model_version")] public string? ModelVersion { get; set; }
    [JsonPropertyName("average_confidence")] public decimal? AverageConfidence { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("segments")] public List<TranscriptSegmentDto> Segments { get; set; } = new();
}

public static class SpeakerLabels
{
    public const string Customer = "customer";
    public const string Agent = "agent";
    public const string Unknown = "unknown";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Customer, Agent, Unknown };
}
