namespace Cx.Contracts.Messaging;

/// <summary>
/// Default topic names (spec 8 / voice design 15).
/// These are defaults only: every service resolves topic names from configuration
/// so an existing Kafka naming convention can be adopted without a code change.
/// </summary>
public static class CxTopics
{
    public const string RawEvents = "cx.raw.events.v1";
    public const string PersistedEvents = "cx.persisted.events.v1";

    public const string VoiceCallEvents = "cx.voice.call.events.v1";
    public const string VoiceRecordingReady = "cx.voice.recording.ready.v1";
    public const string VoiceTranscriptionRequest = "cx.voice.transcription.request.v1";
    public const string VoiceTranscriptionResult = "cx.voice.transcription.result.v1";

    public const string AiAnalysisRequest = "cx.ai.analysis.request.v1";
    public const string AiAnalysisResult = "cx.ai.analysis.result.v1";

    public const string CustomerTimelineUpdated = "cx.customer.timeline.updated.v1";

    public const string InvalidEvents = "cx.invalid.events.v1";
    public const string PersistenceDlq = "cx.persistence.dlq.v1";
    public const string AiDlq = "cx.ai.dlq.v1";
    public const string VoiceDlq = "cx.voice.dlq.v1";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        RawEvents, PersistedEvents,
        VoiceCallEvents, VoiceRecordingReady, VoiceTranscriptionRequest, VoiceTranscriptionResult,
        AiAnalysisRequest, AiAnalysisResult,
        CustomerTimelineUpdated,
        InvalidEvents, PersistenceDlq, AiDlq, VoiceDlq
    };
}
