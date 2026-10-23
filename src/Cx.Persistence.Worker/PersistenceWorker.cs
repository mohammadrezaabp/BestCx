using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Observability;
using Cx.Application.Options;
using Cx.Contracts;
using Cx.Contracts.Events;
using Cx.Contracts.Messaging;
using Cx.Domain.Entities;
using Cx.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Persistence.Worker;

/// <summary>
/// Pipeline A worker (spec 10): raw event -> validate -> normalize -> cx_event + cx_outbox
/// in one transaction -> commit offset. Duplicate delivery is an idempotent no-op.
/// </summary>
public sealed class PersistenceWorker : CxConsumerService
{
    public const string ConsumerGroup = "cx-persistence-worker";

    private readonly KafkaOptions _kafka;

    public PersistenceWorker(
        IServiceScopeFactory scopeFactory,
        IMessageSubscriber subscriber,
        IOptions<KafkaOptions> kafka,
        ILogger<PersistenceWorker> logger)
        : base(scopeFactory, subscriber, logger)
        => _kafka = kafka.Value;

    protected override SubscriptionOptions Subscription => new()
    {
        Topics = new[] { _kafka.Topics.RawEvents },
        GroupId = ConsumerGroup,
        DeadLetterTopic = _kafka.Topics.PersistenceDlq,
        MaxAttempts = 5
    };

    protected override async Task HandleAsync(BusMessage message, IServiceProvider services, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var activity = CxMetrics.Activity.StartActivity("cx.persist", ActivityKind.Consumer);

        var events = services.GetRequiredService<IEventRepository>();
        var customers = services.GetRequiredService<ICustomerRepository>();
        var validator = services.GetRequiredService<Cx.Application.Ingestion.IEventValidator>();

        CanonicalEvent envelope;
        JsonObject raw;

        try
        {
            raw = JsonNode.Parse(message.Value) as JsonObject
                  ?? throw new CxException(ErrorCategory.Schema, "raw message is not a JSON object");
            envelope = raw.Deserialize<CanonicalEvent>(CxJson.Options)
                       ?? throw new CxException(ErrorCategory.Schema, "raw message could not be bound");
        }
        catch (JsonException ex)
        {
            // Unparseable input can never succeed on a retry - straight to the DLQ.
            throw new CxException(ErrorCategory.Schema, "raw message is not valid JSON", ex);
        }

        // The ingestion API already validated, but the worker re-checks: the raw topic is also
        // reachable by replays and by other producers.
        var validation = validator.Validate(raw);
        if (!validation.IsValid)
            throw new CxException(ErrorCategory.Validation,
                $"invalid event {envelope.EventId}: {string.Join("; ", validation.Errors.Take(3))}");

        activity?.SetTag("cx.event_id", envelope.EventId);
        activity?.SetTag("cx.event_name", envelope.EventName);

        var cxEvent = Map(envelope);
        var result = await events.PersistWithOutboxAsync(cxEvent,
            recordId => BuildOutbox(envelope, recordId), ct);

        if (result.WasDuplicate)
        {
            CxMetrics.DuplicateTotal.Add(1);
            Logger.LogDebug("Event {EventId} already persisted; treated as idempotent success", envelope.EventId);
            return;
        }

        // Keeping the identity index current is what makes Customer 360 searchable;
        // a failure here must not roll back the fact we already committed.
        if (!string.IsNullOrWhiteSpace(envelope.Customer?.CustomerId))
        {
            try
            {
                await customers.UpsertFromEventAsync(envelope.TenantId, envelope.Customer.CustomerId,
                    envelope.Customer.BusinessId, envelope.OccurredAt, ct);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Customer identity upsert failed for {CustomerId}",
                    envelope.Customer.CustomerId);
            }
        }

        stopwatch.Stop();
        CxMetrics.PersistenceTotal.Add(1,
            new KeyValuePair<string, object?>("event_category", envelope.EventCategory));
        CxMetrics.PersistenceLatency.Record(stopwatch.Elapsed.TotalMilliseconds);
    }

    private static CxEvent Map(CanonicalEvent e) => new()
    {
        EventId = e.EventId,
        TenantId = e.TenantId,
        CustomerId = e.Customer?.CustomerId,
        AnonymousId = e.Customer?.AnonymousId,
        SessionId = e.Session?.SessionId,
        EventName = e.EventName,
        EventCategory = e.EventCategory,
        SchemaVersion = e.SchemaVersion,
        OccurredAt = e.OccurredAt,
        ReceivedAt = e.ReceivedAt ?? DateTimeOffset.UtcNow,
        SourceType = e.Source.Type,
        SourceService = e.Source.Service,
        ProductId = e.Product?.ProductId,
        ServiceId = e.Product?.ServiceId,
        TraceId = e.Context?.TraceId,
        CorrelationId = e.Context?.CorrelationId,
        Payload = CxJson.Serialize(e)
    };

    private OutboxEvent BuildOutbox(CanonicalEvent e, long recordId)
    {
        var payload = new PersistedEventMessage
        {
            EventId = e.EventId,
            TenantId = e.TenantId,
            CustomerId = e.Customer?.CustomerId,
            EventName = e.EventName,
            EventCategory = e.EventCategory,
            OccurredAt = e.OccurredAt,
            PersistedAt = DateTimeOffset.UtcNow,
            SourceRecordId = recordId,
            CorrelationId = e.Context?.CorrelationId,
            TraceId = e.Context?.TraceId,
            Envelope = JsonSerializer.SerializeToNode(e, CxJson.Options)?.AsObject()
        };

        return new OutboxEvent
        {
            // Deterministic id (spec 10): a retry that somehow reaches the insert again
            // collides on the primary key instead of publishing the event twice.
            Id = DeterministicGuid.From("persisted:" + e.EventId),
            AggregateType = "cx_event",
            AggregateId = e.EventId,
            EventType = e.EventName,
            Topic = _kafka.Topics.PersistedEvents,
            PartitionKey = e.PartitionKey(),
            Payload = CxJson.Serialize(payload)
        };
    }
}

/// <summary>UUIDv5-style deterministic id (name-based, SHA-1) as suggested in spec 10.</summary>
public static class DeterministicGuid
{
    private static readonly Guid Namespace = new("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    public static Guid From(string name)
    {
        var namespaceBytes = Namespace.ToByteArray();
        SwapByteOrder(namespaceBytes);

        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var buffer = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, buffer, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, buffer, namespaceBytes.Length, nameBytes.Length);

        var hash = System.Security.Cryptography.SHA1.HashData(buffer);

        var result = new byte[16];
        Array.Copy(hash, 0, result, 0, 16);
        result[6] = (byte)((result[6] & 0x0F) | 0x50); // version 5
        result[8] = (byte)((result[8] & 0x3F) | 0x80); // RFC 4122 variant

        SwapByteOrder(result);
        return new Guid(result);
    }

    private static void SwapByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
