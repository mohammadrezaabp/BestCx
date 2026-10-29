using System.Net.Http.Headers;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Integrations.Voip;

public sealed class VoipOptions
{
    public const string Section = "Voip";

    public string Provider { get; set; } = "http";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>"bearer", "apikey", "basic" or "none".</summary>
    public string AuthMode { get; set; } = "none";
    public string? BasicUsername { get; set; }
    public string? BasicPassword { get; set; }
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Hosts an event-supplied recording URL may point at, besides the host of BaseUrl.
    /// A URL on any other host is refused, so an empty list with no BaseUrl fetches nothing.
    /// </summary>
    public List<string> AllowedRecordingHosts { get; set; } = new();

    /// <summary>Route templates, filled in per provider. {callId} and {recordingId} are substituted.</summary>
    public string? CallRoute { get; set; }
    public string? RecordingRoute { get; set; }
}

/// <summary>
/// VoIP boundary (spec 15) for providers that expose recordings over HTTP.
///
/// The recording is returned as a live response stream and handed straight to object
/// storage: a two-hour call is never materialized in memory.
/// Concrete providers differ in routes and auth, so both are configuration. A provider
/// with a proprietary SDK gets its own IVoipConnector; nothing else changes.
///
/// A recording URL comes from an event, so it is attacker-influenced input in the worst
/// case: every hop must be on a trusted host, redirects are followed here rather than by
/// the handler (the client is registered with AllowAutoRedirect off), and the provider
/// credentials go on the first hop only, never in the client's default headers.
/// </summary>
public sealed class HttpVoipConnector : IVoipConnector
{
    private const int MaxRedirects = 5;

    private readonly HttpClient _http;
    private readonly VoipOptions _options;
    private readonly ILogger<HttpVoipConnector> _logger;

    public HttpVoipConnector(HttpClient http, IOptions<VoipOptions> options, ILogger<HttpVoipConnector> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
            _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
    }

    public string Provider => _options.Provider;

    public async Task<ConnectorHealth> HealthCheckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return new ConnectorHealth(false, "no VoIP base URL configured");

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, _http.BaseAddress);
            Authorize(request);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return new ConnectorHealth(true, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new ConnectorHealth(false, ex.Message);
        }
    }

    public Task<VoipCallDescriptor?> FetchCallAsync(string callId, CancellationToken ct = default)
    {
        // Call metadata arrives as events; a pull route is only needed for backfill and is
        // left unconfigured until a provider is chosen.
        _logger.LogDebug("FetchCall is not configured for provider {Provider}", Provider);
        return Task.FromResult<VoipCallDescriptor?>(null);
    }

    public Task<VoipRecordingDescriptor?> FetchRecordingMetadataAsync(string callId, string recordingId,
        CancellationToken ct = default) =>
        Task.FromResult<VoipRecordingDescriptor?>(null);

    public async Task<Stream> OpenRecordingStreamAsync(VoipRecordingDescriptor recording,
        CancellationToken ct = default)
    {
        var uri = ResolveUri(recording);
        HttpResponseMessage response;

        for (var hop = 0; ; hop++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // Credentials belong to the first hop only, as the platform handler would do.
            if (hop == 0) Authorize(request);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) ||
                response.Headers.Location is not { } location)
                break;

            response.Dispose();
            if (hop >= MaxRedirects)
                throw new CxException(ErrorCategory.PermanentProvider,
                    $"recording {recording.RecordingId} redirected more than {MaxRedirects} times");

            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            EnsureFetchable(uri);
        }

        if (!response.IsSuccessStatusCode)
        {
            var category = (int)response.StatusCode switch
            {
                404 => ErrorCategory.PermanentProvider,
                401 or 403 => ErrorCategory.Auth,
                429 => ErrorCategory.RateLimit,
                >= 500 => ErrorCategory.Provider5xx,
                _ => ErrorCategory.PermanentProvider
            };
            response.Dispose();
            throw new CxException(category,
                $"VoIP provider returned {(int)response.StatusCode} for recording {recording.RecordingId}");
        }

        // ResponseHeadersRead means the body is still on the wire; the caller streams it
        // into object storage without buffering.
        return await response.Content.ReadAsStreamAsync(ct);
    }

    private Uri ResolveUri(VoipRecordingDescriptor recording)
    {
        if (!string.IsNullOrWhiteSpace(recording.SourceUri) &&
            Uri.TryCreate(recording.SourceUri, UriKind.Absolute, out var absolute))
        {
            EnsureFetchable(absolute);
            return absolute;
        }

        if (string.IsNullOrWhiteSpace(_options.RecordingRoute) || _http.BaseAddress is null)
            throw new CxException(ErrorCategory.Validation,
                $"recording {recording.RecordingId} has no source URI and Voip:RecordingRoute is not configured");

        var route = _options.RecordingRoute
            .Replace("{callId}", Uri.EscapeDataString(recording.CallId), StringComparison.Ordinal)
            .Replace("{recordingId}", Uri.EscapeDataString(recording.RecordingId), StringComparison.Ordinal);

        return new Uri(_http.BaseAddress, route);
    }

    /// <summary>Refuses a scheme other than HTTP(S) and any host that is not the provider's or allowlisted.</summary>
    private void EnsureFetchable(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
            throw new CxException(ErrorCategory.Validation,
                $"unsupported recording URI scheme '{uri.Scheme}'");

        var trusted = (_http.BaseAddress is not null &&
                       uri.Host.Equals(_http.BaseAddress.Host, StringComparison.OrdinalIgnoreCase)) ||
                      _options.AllowedRecordingHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
        if (!trusted)
            throw new CxException(ErrorCategory.Validation,
                $"recording host '{uri.Host}' is neither the Voip:BaseUrl host nor in Voip:AllowedRecordingHosts");
    }

    private void Authorize(HttpRequestMessage request)
    {
        switch (_options.AuthMode.ToLowerInvariant())
        {
            case "bearer" when !string.IsNullOrWhiteSpace(_options.ApiKey):
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                break;
            case "apikey" when !string.IsNullOrWhiteSpace(_options.ApiKey):
                request.Headers.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
                break;
            case "basic" when !string.IsNullOrWhiteSpace(_options.BasicUsername):
                var raw = $"{_options.BasicUsername}:{_options.BasicPassword}";
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw)));
                break;
        }
    }
}
