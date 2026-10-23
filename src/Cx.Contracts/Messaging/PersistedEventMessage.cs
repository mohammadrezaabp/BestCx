using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cx.Contracts.Messaging;

/// <summary>
/// Outbox payload published to cx.persisted.events.v1 (spec 11).
/// AI only ever consumes this shape - never a raw event (rule R2).
/// </summary>
public sealed class PersistedEventMessage
{
    [JsonPropertyName("event_id")] public string EventId { get; set; } = string.Empty;
    [JsonPropertyName("tenant_id")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }
    [JsonPropertyName("event_name")] public string EventName { get; set; } = string.Empty;
    [JsonPropertyName("event_category")] public string EventCategory { get; set; } = string.Empty;
    [JsonPropertyName("occurred_at")] public DateTimeOffset OccurredAt { get; set; }
    [JsonPropertyName("persisted_at")] public DateTimeOffset PersistedAt { get; set; }
    [JsonPropertyName("source_record_id")] public long SourceRecordId { get; set; }
    [JsonPropertyName("correlation_id")] public string? CorrelationId { get; set; }
    [JsonPropertyName("trace_id")] public string? TraceId { get; set; }

    /// <summary>Full canonical envelope, so downstream consumers need no DB round-trip.</summary>
    [JsonPropertyName("envelope")] public JsonObject? Envelope { get; set; }
}
