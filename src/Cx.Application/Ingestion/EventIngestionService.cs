using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Application.Observability;
using Cx.Application.Options;
using Cx.Contracts;
using Cx.Contracts.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Application.Ingestion;

public sealed record IngestOutcome(string EventId, string Status, string? Reason, IReadOnlyList<string>? Errors)
{
    public bool Accepted => Status == IngestStatuses.Accepted;

    public static IngestOutcome Ok(string eventId) => new(eventId, IngestStatuses.Accepted, null, null);

    public static IngestOutcome Rejected(string eventId, string reason, IReadOnlyList<string> errors) =>
        new(eventId, IngestStatuses.Rejected, reason, errors);
}

public static class IngestStatuses
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

public interface IEventIngestionService
{
    Task<IngestOutcome> IngestAsync(JsonNode? raw, string tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<IngestOutcome>> IngestBatchAsync(IReadOnlyList<JsonNode?> raw, string tenantId,
        CancellationToken ct = default);
}

/// <summary>
/// Validates, normalizes and publishes to cx.raw.events.v1. Returns as soon as Kafka acks -
/// the API never waits for database persistence (spec 9).
/// </summary>
public sealed class EventIngestionService : IEventIngestionService
{
    private readonly IEventValidator _validator;
    private readonly IEventNormalizer _normalizer;
    private readonly IMessagePublisher _publisher;
    private readonly KafkaOptions _kafka;
    private readonly ILogger<EventIngestionService> _logger;
    private readonly TimeProvider _time;

    public EventIngestionService(
        IEventValidator validator,
        IEventNormalizer normalizer,
        IMessagePublisher publisher,
        IOptions<KafkaOptions> kafka,
        ILogger<EventIngestionService> logger,
        TimeProvider? time = null)
    {
        _validator = validator;
        _normalizer = normalizer;
        _publisher = publisher;
        _kafka = kafka.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IngestOutcome> IngestAsync(JsonNode? raw, string tenantId, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        using var activity = CxMetrics.Activity.StartActivity("cx.ingest", ActivityKind.Producer);

        var receivedAt = _time.GetUtcNow();
        var envelope = raw as JsonObject;

        // The tenant comes from the authenticated principal, not from the producer's body:
        // a producer must not be able to write into another tenant's stream.
        if (envelope is not null)
            envelope["tenant_id"] = tenantId;

        var validation = _validator.Validate(envelope);
        var eventId = envelope?["event_id"]?.GetValue<string>() ?? Ulid.NewUlid().ToString();

        if (!validation.IsValid)
        {
            await PublishInvalidAsync(eventId, tenantId, envelope, validation, ct);
            CxMetrics.IngestInvalidTotal.Add(1,
                new KeyValuePair<string, object?>("reason", validation.RejectReason ?? "unknown"));
            activity?.SetStatus(ActivityStatusCode.Error, validation.RejectReason);
            return IngestOutcome.Rejected(eventId, validation.RejectReason ?? RejectReasons.SchemaInvalid,
                validation.Errors);
        }

        var evt = envelope.Deserialize<CanonicalEvent>(CxJson.Options)!;
        evt = _normalizer.Normalize(evt, receivedAt);

        // Carry the current trace into the message so a browser click and the AI result
        // it eventually produces share one trace (spec 30).
        evt.Context ??= new ContextRef();
        evt.Context.TraceId ??= activity?.TraceId.ToString();
        evt.Context.SpanId ??= activity?.SpanId.ToString();

        var payload = CxJson.Serialize(evt);
        var headers = new Dictionary<string, string>
        {
            ["event_id"] = evt.EventId,
            ["event_name"] = evt.EventName,
            ["schema_version"] = evt.SchemaVersion,
            ["tenant_id"] = evt.TenantId
        };
        if (evt.Context.TraceId is not null) headers["trace_id"] = evt.Context.TraceId;

        await _publisher.PublishAsync(_kafka.Topics.RawEvents, evt.PartitionKey(), payload, headers, ct);

        stopwatch.Stop();
        CxMetrics.IngestTotal.Add(1,
            new KeyValuePair<string, object?>("event_category", evt.EventCategory),
            new KeyValuePair<string, object?>("source_type", evt.Source.Type));
        CxMetrics.IngestLatency.Record(stopwatch.Elapsed.TotalMilliseconds);

        activity?.SetTag("cx.event_id", evt.EventId);
        activity?.SetTag("cx.event_name", evt.EventName);

        return IngestOutcome.Ok(evt.EventId);
    }

    public async Task<IReadOnlyList<IngestOutcome>> IngestBatchAsync(IReadOnlyList<JsonNode?> raw, string tenantId,
        CancellationToken ct = default)
    {
        var results = new List<IngestOutcome>(raw.Count);
        // Per-item results: one bad event in a batch must not reject the good ones (spec 9).
        foreach (var item in raw)
            results.Add(await IngestAsync(item, tenantId, ct));
        return results;
    }

    private async Task PublishInvalidAsync(string eventId, string tenantId, JsonObject? envelope,
        EventValidationResult validation, CancellationToken ct)
    {
        var record = new JsonObject
        {
            ["event_id"] = eventId,
            ["tenant_id"] = tenantId,
            ["rejected_at"] = _time.GetUtcNow().ToString("O"),
            ["reason"] = validation.RejectReason,
            ["errors"] = new JsonArray(validation.Errors.Select(e => (JsonNode)e!).ToArray()),
            ["payload"] = envelope?.DeepClone()
        };

        try
        {
            await _publisher.PublishAsync(_kafka.Topics.InvalidEvents, eventId, record.ToJsonString(), null, ct);
        }
        catch (Exception ex)
        {
            // Losing the invalid-stream copy must not turn a 400 into a 500 for the producer.
            _logger.LogError(ex, "Failed to publish invalid event {EventId} to {Topic}",
                eventId, _kafka.Topics.InvalidEvents);
        }

        _logger.LogWarning("Rejected event {EventId}: {Reason} ({Errors})",
            eventId, validation.RejectReason, string.Join("; ", validation.Errors.Take(3)));
    }
}
