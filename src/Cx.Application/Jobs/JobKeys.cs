namespace Cx.Application.Jobs;

/// <summary>
/// Canonical job-key shapes (spec 17, voice design 16). Shared so the producer and the
/// consumer of a job always derive the same key - a mismatch would silently defeat
/// idempotency.
/// </summary>
public static class JobKeys
{
    public static string Transcribe(Guid recordingId, string modelVersion) =>
        $"{recordingId}:transcribe:{modelVersion}";

    public static string Analyze(string entityType, string entityId, string promptVersion) =>
        $"{entityType}:{entityId}:analyze:{promptVersion}";

    public static string RecordingIngest(string objectKey) => $"{objectKey}:ingest";
}
