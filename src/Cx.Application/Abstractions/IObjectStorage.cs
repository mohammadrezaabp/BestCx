namespace Cx.Application.Abstractions;

public sealed record ObjectWriteResult(string Key, long SizeBytes, string ChecksumSha256, string? ContentType);

public sealed record ObjectStat(string Key, long SizeBytes, string? ContentType, DateTimeOffset LastModified);

/// <summary>
/// Audio and other large immutable payloads live here, never in Kafka or PostgreSQL (rule R5).
/// Implementations stream: a recording is never buffered whole in memory (spec 15).
/// </summary>
public interface IObjectStorage
{
    /// <param name="contentLength">
    /// Known size when the caller has one (an HTTP Content-Length, a provider-reported
    /// size). Implementations that require a length spool an unknown-length stream to
    /// disk rather than into memory.
    /// </param>
    Task<ObjectWriteResult> PutStreamAsync(string key, Stream content, string? contentType,
        long? contentLength = null, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);

    Task<ObjectStat?> StatAsync(string key, CancellationToken ct = default);

    /// <summary>Short-lived signed URL; the Query API never proxies raw audio bytes by default (spec 24).</summary>
    Task<string> CreateSignedUrlAsync(string key, TimeSpan ttl, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);

    Task EnsureBucketAsync(CancellationToken ct = default);
}
