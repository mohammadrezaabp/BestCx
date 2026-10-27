using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Storage;

/// <summary>
/// S3-compatible storage (MinIO in the bundled stack).
/// A recording is never held in memory: a seekable source is hashed then uploaded in
/// place, and a non-seekable one is spooled to a temp file on disk first (spec 15).
/// </summary>
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly ObjectStorageOptions _options;
    private readonly ILogger<S3ObjectStorage> _logger;

    public S3ObjectStorage(IOptions<ObjectStorageOptions> options, ILogger<S3ObjectStorage> logger)
    {
        _options = options.Value;
        _logger = logger;

        var config = new AmazonS3Config
        {
            ServiceURL = _options.ServiceUrl,
            ForcePathStyle = _options.ForcePathStyle,
            AuthenticationRegion = _options.Region,
            UseHttp = _options.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        };

        _client = new AmazonS3Client(new BasicAWSCredentials(_options.AccessKey, _options.SecretKey), config);
    }

    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        if (!_options.CreateBucketOnStartup) return;

        try
        {
            await _client.PutBucketAsync(new PutBucketRequest
            {
                BucketName = _options.Bucket,
                // The bucket stays private; access is only ever granted by a short-lived signed URL.
                UseClientRegion = false
            }, ct);
            _logger.LogInformation("Created object storage bucket {Bucket}", _options.Bucket);
        }
        catch (AmazonS3Exception ex) when (
            ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            _logger.LogInformation("Object storage bucket {Bucket} already exists", _options.Bucket);
        }
    }

    public async Task<ObjectWriteResult> PutStreamAsync(string key, Stream content, string? contentType,
        long? contentLength = null, CancellationToken ct = default)
    {
        // SigV4 signs the payload hash, which means the body must be readable twice:
        // once to hash, once to send. A non-seekable source (an HTTP request body, a
        // provider response) is therefore spooled to a temp FILE first - to disk, never
        // to memory, which is what lets a two-hour call through (spec 15).
        if (!content.CanSeek)
            return await PutSpooledAsync(key, content, contentType, ct);

        var start = content.Position;
        var checksum = await ComputeSha256Async(content, ct);
        var length = content.Position - start;
        content.Position = start;

        if (length <= 0)
            throw new InvalidOperationException($"refusing to store an empty object at '{key}'");

        await _client.PutObjectAsync(BuildRequest(key, content, contentType, length), ct);

        return new ObjectWriteResult(key, length, checksum, contentType);
    }

    private static async Task<string> ComputeSha256Async(Stream content, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];

        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
            sha.AppendData(buffer, 0, read);

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<ObjectWriteResult> PutSpooledAsync(string key, Stream content, string? contentType,
        CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"cx-upload-{Guid.NewGuid():N}.tmp");

        try
        {
            long spooled;
            string checksum;

            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hashing = new HashingStream(content))
            {
                await hashing.CopyToAsync(file, 81_920, ct);
                spooled = hashing.BytesRead;
                checksum = hashing.FinishAndGetHash();
            }

            _logger.LogDebug("Spooled {Bytes} bytes to disk for {Key} (source length unknown)", spooled, key);

            if (spooled <= 0)
                throw new InvalidOperationException($"refusing to store an empty object at '{key}'");

            await using (var upload = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read,
                             81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _client.PutObjectAsync(BuildRequest(key, upload, contentType, spooled), ct);
            }

            return new ObjectWriteResult(key, spooled, checksum, contentType);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not delete upload spool file {Path}", temp);
            }
        }
    }

    private PutObjectRequest BuildRequest(string key, Stream body, string? contentType, long length)
    {
        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = body,
            ContentType = contentType ?? "application/octet-stream",
            AutoCloseStream = false,
            // The body is always seekable by this point, so the standard signed upload
            // works; chunked encoding is unnecessary and MinIO over plain HTTP rejects
            // the unsigned-payload alternative.
            UseChunkEncoding = false
        };

        // Set explicitly so the length is correct even when the body is a wrapper stream.
        request.Headers.ContentLength = length;
        return request;
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var response = await _client.GetObjectAsync(_options.Bucket, key, ct);
        return response.ResponseStream;
    }

    public async Task<ObjectStat?> StatAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetObjectMetadataAsync(_options.Bucket, key, ct);
            var lastModified = (DateTime?)response.LastModified
                ?? throw new InvalidDataException("Object metadata is missing its last-modified timestamp.");
            return new ObjectStat(key, response.ContentLength, response.Headers.ContentType,
                new DateTimeOffset(lastModified.ToUniversalTime(), TimeSpan.Zero));
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task<string> CreateSignedUrlAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        var url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Expires = DateTime.UtcNow.Add(ttl > TimeSpan.Zero ? ttl : _options.SignedUrlTtl),
            Verb = HttpVerb.GET
        });

        // Inside Docker the app reaches MinIO by service name, but the browser cannot resolve
        // that host. Rewrite the authority to the externally reachable one; the signature
        // covers the path and query, not the host, so the URL stays valid.
        if (!string.IsNullOrWhiteSpace(_options.PublicServiceUrl) &&
            Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            Uri.TryCreate(_options.PublicServiceUrl, UriKind.Absolute, out var publicBase))
        {
            var rewritten = new UriBuilder(parsed)
            {
                Scheme = publicBase.Scheme,
                Host = publicBase.Host,
                Port = publicBase.IsDefaultPort ? -1 : publicBase.Port
            };
            url = rewritten.Uri.ToString();
        }

        return Task.FromResult(url);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await _client.DeleteObjectAsync(_options.Bucket, key, ct);

    public void Dispose() => _client.Dispose();
}

/// <summary>Computes SHA-256 and counts bytes while the upload streams through.</summary>
internal sealed class HashingStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _result;

    public HashingStream(Stream inner) => _inner = inner;

    public long BytesRead { get; private set; }

    public string FinishAndGetHash() =>
        _result ??= Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _hash.AppendData(buffer, offset, read);
            BytesRead += read;
        }
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await _inner.ReadAsync(buffer, ct);
        if (read > 0)
        {
            _hash.AppendData(buffer.Span[..read]);
            BytesRead += read;
        }
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}
