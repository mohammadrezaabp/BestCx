using Cx.Application.Options;
using Cx.Contracts.Events;
using Microsoft.Extensions.Options;

namespace Cx.Application.Ingestion;

public interface IEventNormalizer
{
    /// <summary>Fills gateway-owned fields. Never rewrites producer-supplied facts.</summary>
    CanonicalEvent Normalize(CanonicalEvent evt, DateTimeOffset receivedAt);
}

public sealed class EventNormalizer : IEventNormalizer
{
    private readonly IngestionOptions _options;

    public EventNormalizer(IOptions<IngestionOptions> options) => _options = options.Value;

    public CanonicalEvent Normalize(CanonicalEvent evt, DateTimeOffset receivedAt)
    {
        // The gateway mints event_id only when the producer did not - a producer-supplied id
        // is what makes retries idempotent (technical design 5.4).
        if (string.IsNullOrWhiteSpace(evt.EventId))
            evt.EventId = Ulid.NewUlid().ToString();

        if (string.IsNullOrWhiteSpace(evt.SchemaVersion))
            evt.SchemaVersion = EventSchema.CurrentVersion;

        if (string.IsNullOrWhiteSpace(evt.TenantId))
            evt.TenantId = _options.DefaultTenantId;

        // Timestamps are always stored in UTC (spec 37.13).
        evt.OccurredAt = evt.OccurredAt.ToUniversalTime();
        evt.ReceivedAt = receivedAt.ToUniversalTime();

        // A producer clock far ahead of ours would corrupt time-ordered queries; clamp forward skew
        // but keep the producer value visible in metadata for data-quality analysis.
        var skew = evt.OccurredAt - evt.ReceivedAt.Value;
        if (skew > _options.MaxClockSkew)
        {
            evt.Metadata["_original_occurred_at"] = evt.OccurredAt.ToString("O");
            evt.Metadata["_clock_skew_ms"] = (long)skew.TotalMilliseconds;
            evt.OccurredAt = evt.ReceivedAt.Value;
        }

        if (string.IsNullOrWhiteSpace(evt.EventCategory))
            evt.EventCategory = EventNames.CategoryOf(evt.EventName) ?? EventCategories.System;

        evt.Source.Environment ??= _options.Environment;

        if (evt.Customer is not null)
        {
            evt.Customer.CustomerId = Trim(evt.Customer.CustomerId);
            evt.Customer.AnonymousId = Trim(evt.Customer.AnonymousId);
            evt.Customer.BusinessId = Trim(evt.Customer.BusinessId);
        }

        evt.Privacy ??= new PrivacyRef();
        evt.Privacy.ContainsPii ??= false;
        evt.Privacy.Classification ??= "internal";

        return evt;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
