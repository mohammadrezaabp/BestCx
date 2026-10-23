using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Contracts.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Integrations.Podium;

public sealed class PodiumOptions
{
    public const string Section = "Podium";

    /// <summary>
    /// Off until the official documentation is confirmed. Nothing in CX Core depends on
    /// this connector being enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Reports the integration as backed by local database fixtures and guarantees that
    /// no request is sent to podium.ir.
    /// </summary>
    public bool UseMockData { get; set; }

    public string BaseUrl { get; set; } = "https://connector.example.invalid";
    public string? ApiKey { get; set; }
    /// <summary>"apikey" (X-Api-Key), "bearer", or "none" - confirm against podium.ir/docs.</summary>
    public string AuthMode { get; set; } = "apikey";
    public string TenantId { get; set; } = "bestcx";
    public int TimeoutSeconds { get; set; } = 30;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Route mapping is intentionally configuration, not code. Fill these in once the
    /// official routes are known; no default is guessed (rule R7).
    /// </summary>
    public PodiumRoutes Routes { get; set; } = new();

    public sealed class PodiumRoutes
    {
        public string? Health { get; set; }
        public string? Changes { get; set; }
        public string? Customer { get; set; }
        public string? Product { get; set; }
    }
}

/// <summary>
/// Podium integration boundary (spec 14, rule R7).
///
/// podium.ir/docs returned HTTP 502 when this platform was specified, so NO endpoint,
/// auth scheme or payload shape is assumed. This connector is a working shell:
/// - transport, auth, checkpointing, error handling and observability are implemented;
/// - the actual routes come from configuration and are empty by default;
/// - with no routes configured it reports "not configured" instead of guessing a URL.
///
/// To complete the integration, confirm the routes from the official documentation, set
/// them in Podium:Routes, and adjust MapToCanonicalEvents to the real payload shape.
/// Nothing outside this project needs to change.
/// </summary>
public sealed class PodiumConnector : IPodiumConnector
{
    private readonly HttpClient _http;
    private readonly PodiumOptions _options;
    private readonly ILogger<PodiumConnector> _logger;

    public PodiumConnector(HttpClient http, IOptions<PodiumOptions> options, ILogger<PodiumConnector> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = http;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            switch (_options.AuthMode.ToLowerInvariant())
            {
                case "bearer":
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                    break;
                case "apikey":
                    _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
                    break;
            }
        }
    }

    public string Name => "bestcx";

    public async Task<ConnectorHealth> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_options.UseMockData)
            return new ConnectorHealth(true, "database mock data (no Podium API key or network call)");

        if (!_options.Enabled)
            return new ConnectorHealth(false, "disabled");

        if (string.IsNullOrWhiteSpace(_options.Routes.Health))
            return new ConnectorHealth(false,
                "no health route configured - confirm the endpoint from podium.ir/docs and set Podium:Routes:Health");

        try
        {
            using var response = await _http.GetAsync(_options.Routes.Health, ct);
            return new ConnectorHealth(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new ConnectorHealth(false, ex.Message);
        }
    }

    public async Task<PullResult> PullChangesAsync(string? cursor, CancellationToken ct = default)
    {
        if (_options.UseMockData)
            return new PullResult(Array.Empty<CanonicalEvent>(), cursor, false);

        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Routes.Changes))
            return new PullResult(Array.Empty<CanonicalEvent>(), cursor, false);

        var url = _options.Routes.Changes.Contains("{cursor}", StringComparison.Ordinal)
            ? _options.Routes.Changes.Replace("{cursor}", Uri.EscapeDataString(cursor ?? string.Empty))
            : _options.Routes.Changes + (cursor is null ? string.Empty : $"?cursor={Uri.EscapeDataString(cursor)}");

        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(ct);
        var events = MapToCanonicalEvents(payload, new Dictionary<string, string>());

        var next = JsonNode.Parse(payload)?["next_cursor"]?.GetValue<string>();
        return new PullResult(events, next ?? cursor, next is not null);
    }

    public Task<IReadOnlyList<CanonicalEvent>> HandleWebhookAsync(string payload,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct = default) =>
        Task.FromResult(MapToCanonicalEvents(payload, headers));

    public Task<string?> ResolveCustomerAsync(string sourceRef, CancellationToken ct = default)
    {
        // Until the identity endpoint is confirmed, the source reference is passed through
        // unchanged rather than mapped through a guessed route.
        _logger.LogDebug("Podium customer resolution is not configured; passing through '{SourceRef}'", sourceRef);
        return Task.FromResult<string?>(sourceRef);
    }

    public Task<string?> MapProductAsync(string sourceRef, CancellationToken ct = default) =>
        Task.FromResult<string?>(sourceRef);

    /// <summary>
    /// Best-effort mapping of an unknown payload onto the canonical envelope.
    /// It reads only fields that are self-describing, and anything it cannot interpret is
    /// preserved under metadata.source_payload so no information is lost before the real
    /// mapping is written.
    /// </summary>
    public IReadOnlyList<CanonicalEvent> MapToCanonicalEvents(string payload,
        IReadOnlyDictionary<string, string> headers)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Podium payload is not valid JSON");
            return Array.Empty<CanonicalEvent>();
        }

        var items = node switch
        {
            JsonArray array => array.OfType<JsonObject>().ToList(),
            JsonObject obj when obj["data"] is JsonArray data => data.OfType<JsonObject>().ToList(),
            JsonObject obj when obj["items"] is JsonArray list => list.OfType<JsonObject>().ToList(),
            JsonObject obj => new List<JsonObject> { obj },
            _ => new List<JsonObject>()
        };

        var events = new List<CanonicalEvent>(items.Count);

        foreach (var item in items)
        {
            var evt = new CanonicalEvent
            {
                EventId = Text(item, "event_id", "id") ?? Ulid.NewUlid().ToString(),
                SchemaVersion = EventSchema.CurrentVersion,
                EventName = NormalizeEventName(Text(item, "event", "event_name", "type")),
                OccurredAt = Time(item, "occurred_at", "timestamp", "created_at") ?? DateTimeOffset.UtcNow,
                TenantId = _options.TenantId,
                Source = new SourceRef { Type = "bestcx", Service = "podium-api", Channel = "integration" },
                Customer = new CustomerRef
                {
                    CustomerId = Text(item, "customer_id", "user_id"),
                    BusinessId = Text(item, "business_id", "merchant_id")
                },
                Product = new ProductRef
                {
                    ProductId = Text(item, "product_id", "service"),
                    ServiceId = Text(item, "service_id")
                },
                Context = new ContextRef { CorrelationId = Text(item, "correlation_id", "reference") }
            };

            evt.EventCategory = EventNames.CategoryOf(evt.EventName) ?? EventCategories.System;
            evt.Metadata["source_payload"] = item.DeepClone();
            if (headers.TryGetValue("X-Podium-Event", out var headerEvent))
                evt.Metadata["source_event_header"] = headerEvent;

            events.Add(evt);
        }

        return events;
    }

    /// <summary>
    /// Maps an unknown source event name onto the catalog. Anything unrecognised becomes
    /// a well-formed generic name - an invented event name would poison the taxonomy.
    /// </summary>
    private static string NormalizeEventName(string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) return "api.request.succeeded";

        var normalized = sourceName.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (EventNames.IsKnown(normalized)) return normalized;

        return normalized switch
        {
            var s when s.Contains("ticket") && s.Contains("creat") => EventNames.SupportTicketCreated,
            var s when s.Contains("ticket") && s.Contains("resolv") => EventNames.SupportTicketResolved,
            var s when s.Contains("feedback") => EventNames.FeedbackSubmitted,
            var s when s.Contains("fail") || s.Contains("error") => EventNames.ApiRequestFailed,
            var s when s.Contains("activat") => EventNames.ProductServiceActivated,
            _ => "api.request.succeeded"
        };
    }

    private static string? Text(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue value &&
                value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                return text;
        }
        return null;
    }

    private static DateTimeOffset? Time(JsonObject obj, params string[] keys)
    {
        var raw = Text(obj, keys);
        return raw is not null && DateTimeOffset.TryParse(raw, out var parsed) ? parsed.ToUniversalTime() : null;
    }
}
