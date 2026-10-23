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
    /// Hosts a recording may be fetched from. Empty means "any host the event points at",
    /// which is only safe in a trusted network - set this in production.
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
/// </summary>
public sealed class HttpVoipConnector : IVoipConnector
{
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

        switch (_options.AuthMode.ToLowerInvariant())
        {
            case "bearer" when !string.IsNullOrWhiteSpace(_options.ApiKey):
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                break;
            case "apikey" when !string.IsNullOrWhiteSpace(_options.ApiKey):
                _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
                break;
            case "basic" when !string.IsNullOrWhiteSpace(_options.BasicUsername):
                var raw = $"{_options.BasicUsername}:{_options.BasicPassword}";
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw)));
                break;
        }
    }

    public string Provider => _options.Provider;

    public async Task<ConnectorHealth> HealthCheckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return new ConnectorHealth(false, "no VoIP base URL configured");

        try
        {
            using var response = await _http.GetAsync(string.Empty, HttpCompletionOption.ResponseHeadersRead, ct);
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

        var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);

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
            // An event-supplied URL is attacker-influenced input in the worst case, so it is
            // checked against the allowlist before the worker fetches it.
            if (_options.AllowedRecordingHosts.Count > 0 &&
                !_options.AllowedRecordingHosts.Contains(absolute.Host, StringComparer.OrdinalIgnoreCase))
            {
                throw new CxException(ErrorCategory.Validation,
                    $"recording host '{absolute.Host}' is not in Voip:AllowedRecordingHosts");
            }

            if (absolute.Scheme is not ("http" or "https"))
                throw new CxException(ErrorCategory.Validation,
                    $"unsupported recording URI scheme '{absolute.Scheme}'");

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
}
