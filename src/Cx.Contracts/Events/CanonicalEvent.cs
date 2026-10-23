using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cx.Contracts.Events;

/// <summary>
/// Canonical CX Event envelope (technical design §6.1 / implementation spec §6).
/// The envelope is an immutable raw fact: never mutate it after persistence (rule R6).
/// </summary>
public sealed class CanonicalEvent
{
    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = EventSchema.CurrentVersion;

    [JsonPropertyName("event_name")]
    public string EventName { get; set; } = string.Empty;

    [JsonPropertyName("event_category")]
    public string EventCategory { get; set; } = string.Empty;

    [JsonPropertyName("occurred_at")]
    public DateTimeOffset OccurredAt { get; set; }

    [JsonPropertyName("received_at")]
    public DateTimeOffset? ReceivedAt { get; set; }

    [JsonPropertyName("tenant_id")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("customer")]
    public CustomerRef? Customer { get; set; }

    [JsonPropertyName("session")]
    public SessionRef? Session { get; set; }

    [JsonPropertyName("source")]
    public SourceRef Source { get; set; } = new();

    [JsonPropertyName("product")]
    public ProductRef? Product { get; set; }

    [JsonPropertyName("interaction")]
    public InteractionRef? Interaction { get; set; }

    [JsonPropertyName("voice")]
    public VoiceRef? Voice { get; set; }

    [JsonPropertyName("context")]
    public ContextRef? Context { get; set; }

    [JsonPropertyName("privacy")]
    public PrivacyRef? Privacy { get; set; }

    /// <summary>Free-form, additive-only. Unknown keys are allowed (spec §6 validation behavior).</summary>
    [JsonPropertyName("metadata")]
    public JsonObject Metadata { get; set; } = new();

    /// <summary>Kafka partition key: tenant_id:customer_id (spec §8.2).</summary>
    public string PartitionKey() => PartitionKeys.ForCustomer(TenantId, Customer?.CustomerId ?? Customer?.AnonymousId);

    public CanonicalEvent Clone() =>
        JsonSerializer.Deserialize<CanonicalEvent>(JsonSerializer.Serialize(this, CxJson.Options), CxJson.Options)!;
}

public sealed class CustomerRef
{
    [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }
    [JsonPropertyName("anonymous_id")] public string? AnonymousId { get; set; }
    [JsonPropertyName("business_id")] public string? BusinessId { get; set; }
}

public sealed class SessionRef
{
    [JsonPropertyName("session_id")] public string? SessionId { get; set; }
}

public sealed class SourceRef
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("service")] public string Service { get; set; } = string.Empty;
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("environment")] public string? Environment { get; set; }
}

public sealed class ProductRef
{
    [JsonPropertyName("product_id")] public string? ProductId { get; set; }
    [JsonPropertyName("service_id")] public string? ServiceId { get; set; }
}

public sealed class InteractionRef
{
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("duration_ms")] public long? DurationMs { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
}

/// <summary>Voice block (voice design §4). Call metadata only, never audio bytes (rule R5).</summary>
public sealed class VoiceRef
{
    [JsonPropertyName("call_id")] public string? CallId { get; set; }
    [JsonPropertyName("recording_id")] public string? RecordingId { get; set; }
    [JsonPropertyName("direction")] public string? Direction { get; set; }
    [JsonPropertyName("agent_id")] public string? AgentId { get; set; }
    [JsonPropertyName("queue_id")] public string? QueueId { get; set; }
    [JsonPropertyName("duration_ms")] public long? DurationMs { get; set; }
    [JsonPropertyName("end_reason")] public string? EndReason { get; set; }
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("provider_call_id")] public string? ProviderCallId { get; set; }
}

public sealed class ContextRef
{
    [JsonPropertyName("trace_id")] public string? TraceId { get; set; }
    [JsonPropertyName("span_id")] public string? SpanId { get; set; }
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
    [JsonPropertyName("correlation_id")] public string? CorrelationId { get; set; }
}

public sealed class PrivacyRef
{
    [JsonPropertyName("contains_pii")] public bool? ContainsPii { get; set; }
    [JsonPropertyName("classification")] public string? Classification { get; set; }
}
