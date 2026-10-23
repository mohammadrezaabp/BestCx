using Cx.Contracts.Voice;

namespace Cx.Application.Abstractions;

public sealed class TranscriptionAudio
{
    public required Stream Content { get; init; }
    public required string ObjectKey { get; init; }
    public string? ContentType { get; init; }
    public string? Language { get; init; }
    public long? DurationMs { get; init; }
    /// <summary>Channel-separated audio gives reliable speaker mapping (voice design 8).</summary>
    public int? Channels { get; init; }
}

public interface ISpeechToTextEngine
{
    string Engine { get; }
    string Model { get; }
    string? ModelVersion { get; }
    Task<TranscriptionResult> TranscribeAsync(TranscriptionAudio audio, CancellationToken ct = default);
}
