using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Contracts.Events;

namespace Cx.Ingestion.Api;

/// <summary>
/// Maps a VoIP provider webhook onto canonical voice events (voice design 2, 4).
/// A recording is referenced, never embedded: the URL travels in metadata and the voice
/// worker fetches it (rule R5).
/// </summary>
public sealed class VoipWebhookAdapter : IExternalCxAdapter
{
    public string Provider => "voip";
    public string SourceType => "voip";

    public bool CanParse(string payload, IReadOnlyDictionary<string, string> headers) =>
        payload.Contains("call", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> Validate(string payload)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            return new[] { $"payload is not valid JSON: {ex.Message}" };
        }

        var errors = new List<string>();
        foreach (var item in Items(node))
        {
            if (Text(item, "call_id", "callId", "uniqueid", "id") is null)
                errors.Add("call_id is required");
        }

        if (!Items(node).Any()) errors.Add("payload contains no call events");
        return errors;
    }

    public IReadOnlyList<CanonicalEvent> MapToCanonicalEvents(string payload,
        IReadOnlyDictionary<string, string> headers)
    {
        var node = JsonNode.Parse(payload);
        var events = new List<CanonicalEvent>();

        foreach (var item in Items(node))
        {
            var callId = Text(item, "call_id", "callId", "uniqueid", "id");
            if (callId is null) continue;

            var action = Text(item, "event", "action", "status", "state") ?? "ended";
            var eventName = MapEventName(action);
            var occurredAt = Time(item, "occurred_at", "timestamp", "end_time", "start_time")
                             ?? DateTimeOffset.UtcNow;

            var evt = new CanonicalEvent
            {
                EventId = Text(item, "event_id") ?? $"voip-{callId}-{action}",
                SchemaVersion = EventSchema.CurrentVersion,
                EventName = eventName,
                EventCategory = EventCategories.Voice,
                OccurredAt = occurredAt,
                TenantId = Text(item, "tenant_id") ?? "bestcx",
                Customer = new CustomerRef { CustomerId = Text(item, "customer_id") },
                Source = new SourceRef
                {
                    Type = "voip",
                    Service = Text(item, "provider") ?? "contact-center",
                    Channel = "voice"
                },
                Voice = new VoiceRef
                {
                    CallId = callId,
                    RecordingId = Text(item, "recording_id"),
                    Direction = NormalizeDirection(Text(item, "direction")),
                    AgentId = Text(item, "agent_id", "agent"),
                    QueueId = Text(item, "queue_id", "queue"),
                    DurationMs = DurationMs(item),
                    EndReason = Text(item, "end_reason", "hangup_cause", "disposition"),
                    Provider = Text(item, "provider"),
                    ProviderCallId = Text(item, "provider_call_id", "uniqueid")
                },
                Context = new ContextRef { CorrelationId = Text(item, "correlation_id", "ticket_id") },
                Privacy = new PrivacyRef { ContainsPii = true, Classification = "restricted" }
            };

            var recordingUrl = Text(item, "recording_url", "record_url", "media_url");
            if (recordingUrl is not null)
            {
                evt.Metadata["recording_url"] = recordingUrl;
                evt.Metadata["content_type"] = Text(item, "content_type") ?? "audio/wav";
                evt.Metadata["channels"] = Number(item, "channels");
                evt.Metadata["sample_rate"] = Number(item, "sample_rate");
                evt.Metadata["codec"] = Text(item, "codec");
                evt.Metadata["size_bytes"] = Number(item, "size_bytes", "file_size");
                evt.Metadata["language"] = Text(item, "language");
                evt.Metadata["duration_ms"] = DurationMs(item);
            }

            events.Add(evt);
        }

        return events;
    }

    private static string MapEventName(string action) => action.ToLowerInvariant() switch
    {
        "started" or "start" or "ringing" or "initiated" => EventNames.VoiceCallStarted,
        "answered" or "answer" or "in_progress" => EventNames.VoiceCallAnswered,
        "ended" or "end" or "completed" or "hangup" => EventNames.VoiceCallEnded,
        "dropped" or "failed" or "no_answer" or "busy" or "canceled" => EventNames.VoiceCallDropped,
        "recording_ready" or "recording" or "record_ready" => EventNames.VoiceRecordingReady,
        _ => EventNames.VoiceCallEnded
    };

    private static string? NormalizeDirection(string? direction) => direction?.ToLowerInvariant() switch
    {
        "in" or "inbound" or "incoming" => "inbound",
        "out" or "outbound" or "outgoing" => "outbound",
        "internal" => "internal",
        _ => null
    };

    /// <summary>Providers report duration in seconds or milliseconds; normalize to ms.</summary>
    private static long? DurationMs(JsonObject item)
    {
        var ms = Number(item, "duration_ms");
        if (ms is not null) return ms;

        var seconds = Number(item, "duration", "duration_seconds", "billsec");
        return seconds is null ? null : seconds * 1000;
    }

    private static IEnumerable<JsonObject> Items(JsonNode? node) => node switch
    {
        JsonArray array => array.OfType<JsonObject>(),
        JsonObject obj when obj["events"] is JsonArray nested => nested.OfType<JsonObject>(),
        JsonObject obj when obj["data"] is JsonArray data => data.OfType<JsonObject>(),
        JsonObject obj => new[] { obj },
        _ => Array.Empty<JsonObject>()
    };

    private static string? Text(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue value)
            {
                if (value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)) return text;
                if (value.TryGetValue<long>(out var number)) return number.ToString();
            }
        }
        return null;
    }

    private static long? Number(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue value)
            {
                if (value.TryGetValue<long>(out var number)) return number;
                if (value.TryGetValue<double>(out var d)) return (long)Math.Round(d);
                if (value.TryGetValue<string>(out var text) && long.TryParse(text, out var parsed)) return parsed;
            }
        }
        return null;
    }

    private static DateTimeOffset? Time(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue value) continue;

            if (value.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, out var parsed))
                return parsed.ToUniversalTime();

            // Unix epoch seconds are common in telephony payloads.
            if (value.TryGetValue<long>(out var epoch) && epoch > 1_000_000_000)
                return DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        return null;
    }
}
