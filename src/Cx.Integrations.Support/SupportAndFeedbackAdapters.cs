using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Application.Ingestion;
using Cx.Contracts.Events;

namespace Cx.Integrations.Support;

/// <summary>
/// Generic support-desk adapter (spec 13). Transforms a provider payload into canonical
/// events and keeps the provider's own id so replays are idempotent.
/// Field names are configurable-by-convention: the adapter accepts the common spellings
/// rather than binding to one vendor's schema.
/// </summary>
public sealed class SupportTicketAdapter : IExternalCxAdapter
{
    public string Provider => "support";
    public string SourceType => "support";

    public bool CanParse(string payload, IReadOnlyDictionary<string, string> headers) =>
        payload.Contains("ticket", StringComparison.OrdinalIgnoreCase) ||
        headers.ContainsKey("X-Support-Event");

    public IReadOnlyList<string> Validate(string payload)
    {
        var errors = new List<string>();

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            return new[] { $"payload is not valid JSON: {ex.Message}" };
        }

        foreach (var item in Items(node))
        {
            if (AdapterJson.Text(item, "ticket_id", "id", "external_id") is null)
                errors.Add("ticket_id is required for idempotency");
        }

        return errors;
    }

    public IReadOnlyList<CanonicalEvent> MapToCanonicalEvents(string payload,
        IReadOnlyDictionary<string, string> headers)
    {
        var node = JsonNode.Parse(payload);
        var events = new List<CanonicalEvent>();

        foreach (var item in Items(node))
        {
            var ticketId = AdapterJson.Text(item, "ticket_id", "id", "external_id");
            if (ticketId is null) continue;

            var action = AdapterJson.Text(item, "action", "event", "status") ?? "created";
            var eventName = MapEventName(action);

            var evt = new CanonicalEvent
            {
                EventId = AdapterJson.Text(item, "event_id") ?? $"support-{ticketId}-{action}",
                SchemaVersion = EventSchema.CurrentVersion,
                EventName = eventName,
                EventCategory = EventCategories.Support,
                OccurredAt = AdapterJson.Time(item, "occurred_at", "updated_at", "created_at")
                             ?? DateTimeOffset.UtcNow,
                TenantId = AdapterJson.Text(item, "tenant_id") ?? "bestcx",
                Customer = new CustomerRef
                {
                    CustomerId = AdapterJson.Text(item, "customer_id", "requester_id"),
                    BusinessId = AdapterJson.Text(item, "business_id")
                },
                Source = new SourceRef
                {
                    Type = "support",
                    Service = AdapterJson.Text(item, "provider") ?? "support-desk",
                    Channel = AdapterJson.Text(item, "channel") ?? "support"
                },
                Product = new ProductRef { ProductId = AdapterJson.Text(item, "product_id") },
                Context = new ContextRef { CorrelationId = ticketId }
            };

            evt.Metadata["external_id"] = ticketId;
            evt.Metadata["subject"] = AdapterJson.Text(item, "subject", "title");
            evt.Metadata["description"] = AdapterJson.Text(item, "description", "body", "message");
            evt.Metadata["status"] = AdapterJson.Text(item, "status");
            evt.Metadata["priority"] = AdapterJson.Text(item, "priority", "severity");
            evt.Metadata["assignee_id"] = AdapterJson.Text(item, "assignee_id", "agent_id");

            events.Add(evt);
        }

        return events;
    }

    private static string MapEventName(string action) => action.ToLowerInvariant() switch
    {
        "created" or "new" or "open" => EventNames.SupportTicketCreated,
        "assigned" => EventNames.SupportTicketAssigned,
        "replied" or "comment" or "response" => EventNames.SupportTicketReplied,
        "escalated" => EventNames.SupportTicketEscalated,
        "resolved" or "closed" or "solved" => EventNames.SupportTicketResolved,
        "reopened" => EventNames.SupportTicketReopened,
        _ => EventNames.SupportTicketCreated
    };

    private static IEnumerable<JsonObject> Items(JsonNode? node) => AdapterJson.Items(node);
}

/// <summary>Feedback adapter: CSAT, NPS, star rating and free-text comments.</summary>
public sealed class FeedbackAdapter : IExternalCxAdapter
{
    public string Provider => "feedback";
    public string SourceType => "feedback";

    public bool CanParse(string payload, IReadOnlyDictionary<string, string> headers) =>
        payload.Contains("rating", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("nps", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("csat", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> Validate(string payload)
    {
        try
        {
            var node = JsonNode.Parse(payload);
            var errors = new List<string>();

            foreach (var item in AdapterJson.Items(node))
            {
                foreach (var key in new[] { "nps", "nps_score" })
                    if (item.ContainsKey(key) && !NpsScoreValidation.IsValid(item[key]))
                        errors.Add(NpsScoreValidation.Error);
                if (item["nps"] is not null && item["nps_score"] is not null
                    && AdapterJson.Number(item, "nps") != AdapterJson.Number(item, "nps_score"))
                    errors.Add("nps and nps_score must agree when both are supplied");

                var hasScore = AdapterJson.Number(item, "rating", "stars", "score", "nps", "nps_score", "csat", "csat_score") is not null;
                var hasComment = !string.IsNullOrWhiteSpace(AdapterJson.Text(item, "comment", "text", "feedback"));
                if (!hasScore && !hasComment)
                    errors.Add("feedback must carry at least a score or a comment");
            }

            return errors;
        }
        catch (JsonException ex)
        {
            return new[] { $"payload is not valid JSON: {ex.Message}" };
        }
    }

    public IReadOnlyList<CanonicalEvent> MapToCanonicalEvents(string payload,
        IReadOnlyDictionary<string, string> headers)
    {
        var node = JsonNode.Parse(payload);
        var events = new List<CanonicalEvent>();

        foreach (var item in AdapterJson.Items(node))
        {
            var externalId = AdapterJson.Text(item, "feedback_id", "id") ?? Ulid.NewUlid().ToString();
            var nps = AdapterJson.Number(item, "nps", "nps_score");
            var csat = AdapterJson.Number(item, "csat", "csat_score");
            var rating = AdapterJson.Number(item, "rating", "stars", "score");

            var eventName = nps is not null ? EventNames.FeedbackNpsSubmitted
                : csat is not null ? EventNames.FeedbackCsatSubmitted
                : rating is not null ? EventNames.FeedbackRatingSubmitted
                : EventNames.FeedbackSubmitted;

            var evt = new CanonicalEvent
            {
                EventId = AdapterJson.Text(item, "event_id") ?? $"feedback-{externalId}",
                SchemaVersion = EventSchema.CurrentVersion,
                EventName = eventName,
                EventCategory = EventCategories.Feedback,
                OccurredAt = AdapterJson.Time(item, "submitted_at", "created_at") ?? DateTimeOffset.UtcNow,
                TenantId = AdapterJson.Text(item, "tenant_id") ?? "bestcx",
                Customer = new CustomerRef { CustomerId = AdapterJson.Text(item, "customer_id", "user_id") },
                Source = new SourceRef
                {
                    Type = "feedback",
                    Service = AdapterJson.Text(item, "provider") ?? "feedback-form",
                    Channel = AdapterJson.Text(item, "channel") ?? "web"
                },
                Product = new ProductRef { ProductId = AdapterJson.Text(item, "product_id") }
            };

            evt.Metadata["external_id"] = externalId;
            evt.Metadata["rating"] = rating;
            evt.Metadata["nps_score"] = nps;
            evt.Metadata["csat_score"] = csat;
            evt.Metadata["comment"] = AdapterJson.Text(item, "comment", "text", "feedback");

            events.Add(evt);
        }

        return events;
    }
}

internal static class AdapterJson
{
    public static IEnumerable<JsonObject> Items(JsonNode? node) => node switch
    {
        JsonArray array => array.OfType<JsonObject>(),
        JsonObject obj when obj["data"] is JsonArray data => data.OfType<JsonObject>(),
        JsonObject obj when obj["items"] is JsonArray list => list.OfType<JsonObject>(),
        JsonObject obj => new[] { obj },
        _ => Array.Empty<JsonObject>()
    };

    public static string? Text(JsonObject obj, params string[] keys)
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

    public static int? Number(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue value)
            {
                if (value.TryGetValue<int>(out var number)) return number;
                if (value.TryGetValue<double>(out var d)) return (int)Math.Round(d);
                if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed)) return parsed;
            }
        }
        return null;
    }

    public static DateTimeOffset? Time(JsonObject obj, params string[] keys)
    {
        var raw = Text(obj, keys);
        return raw is not null && DateTimeOffset.TryParse(raw, out var parsed) ? parsed.ToUniversalTime() : null;
    }
}
