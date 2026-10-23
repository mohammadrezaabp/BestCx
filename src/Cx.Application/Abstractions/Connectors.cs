using Cx.Contracts.Events;

namespace Cx.Application.Abstractions;

public sealed record ConnectorHealth(bool Healthy, string? Detail = null);

public sealed record PullResult(IReadOnlyList<CanonicalEvent> Events, string? NextCursor, bool HasMore);

/// <summary>
/// Podium integration boundary (spec 14).
/// podium.ir/docs was not reachable when this platform was specified, so no route,
/// auth scheme or payload shape is hard-coded here. A concrete adapter fills these in
/// once the official documentation is confirmed - CX Core does not change.
/// </summary>
public interface IPodiumConnector
{
    string Name { get; }
    Task<ConnectorHealth> HealthCheckAsync(CancellationToken ct = default);
    Task<PullResult> PullChangesAsync(string? cursor, CancellationToken ct = default);
    Task<IReadOnlyList<CanonicalEvent>> HandleWebhookAsync(string payload,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct = default);
    Task<string?> ResolveCustomerAsync(string sourceRef, CancellationToken ct = default);
    Task<string?> MapProductAsync(string sourceRef, CancellationToken ct = default);
}

/// <summary>Generic external source adapter (spec 13).</summary>
public interface IExternalCxAdapter
{
    string Provider { get; }
    string SourceType { get; }
    bool CanParse(string payload, IReadOnlyDictionary<string, string> headers);
    IReadOnlyList<string> Validate(string payload);
    IReadOnlyList<CanonicalEvent> MapToCanonicalEvents(string payload, IReadOnlyDictionary<string, string> headers);
}

public sealed record VoipCallDescriptor(
    string CallId,
    string? CustomerId,
    string? AgentId,
    string? QueueId,
    string? Direction,
    DateTimeOffset? StartedAt,
    DateTimeOffset? AnsweredAt,
    DateTimeOffset? EndedAt,
    long? DurationMs,
    string? EndReason,
    string? CorrelationId,
    string? ProviderCallId);

public sealed record VoipRecordingDescriptor(
    string CallId,
    string RecordingId,
    string? SourceUri,
    string? ContentType,
    long? SizeBytes,
    long? DurationMs,
    int? Channels,
    int? SampleRate,
    string? Codec,
    string? Language);

/// <summary>VoIP provider boundary (spec 15). Recordings are fetched as streams, never buffered.</summary>
public interface IVoipConnector
{
    string Provider { get; }
    Task<ConnectorHealth> HealthCheckAsync(CancellationToken ct = default);
    Task<VoipCallDescriptor?> FetchCallAsync(string callId, CancellationToken ct = default);
    Task<VoipRecordingDescriptor?> FetchRecordingMetadataAsync(string callId, string recordingId,
        CancellationToken ct = default);
    Task<Stream> OpenRecordingStreamAsync(VoipRecordingDescriptor recording, CancellationToken ct = default);
}
