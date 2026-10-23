using System.Text;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Domain.Entities;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Cx.Infrastructure.Database.Repositories;

public sealed class EventRepository : IEventRepository
{
    private const string SelectColumns = """
        id, event_id, tenant_id, customer_id, anonymous_id, session_id, event_name, event_category,
        schema_version, occurred_at, received_at, source_type, source_service, product_id, service_id,
        trace_id, correlation_id, payload, created_at
        """;

    private readonly CxDataSource _dataSource;

    public EventRepository(CxDataSource dataSource) => _dataSource = dataSource;

    public async Task<PersistResult> PersistWithOutboxAsync(CxEvent cxEvent, Func<long, OutboxEvent> outboxFactory,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        // ON CONFLICT DO NOTHING + RETURNING gives us the insert-or-duplicate answer in one round trip.
        var insert = new CommandDefinition("""
            INSERT INTO cx_event (
                event_id, tenant_id, customer_id, anonymous_id, session_id, event_name, event_category,
                schema_version, occurred_at, received_at, source_type, source_service, product_id, service_id,
                trace_id, correlation_id, payload)
            VALUES (
                @EventId, @TenantId, @CustomerId, @AnonymousId, @SessionId, @EventName, @EventCategory,
                @SchemaVersion, @OccurredAt, @ReceivedAt, @SourceType, @SourceService, @ProductId, @ServiceId,
                @TraceId, @CorrelationId, @Payload::jsonb)
            ON CONFLICT (event_id) DO NOTHING
            RETURNING id, created_at
            """, Normalize(cxEvent), tx, cancellationToken: ct);

        var inserted = await connection.QuerySingleOrDefaultAsync<(long Id, DateTimeOffset CreatedAt)?>(insert);

        if (inserted is null)
        {
            // Redelivery of an already-committed event. The original transaction wrote its outbox row,
            // so publication is already guaranteed - writing another one would duplicate the persisted
            // event downstream (spec 10).
            var existing = await connection.QuerySingleOrDefaultAsync<(long Id, DateTimeOffset CreatedAt)>(
                new CommandDefinition("SELECT id, created_at FROM cx_event WHERE event_id = @EventId",
                    new { cxEvent.EventId }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return new PersistResult(existing.Id, true, existing.CreatedAt);
        }

        var outbox = outboxFactory(inserted.Value.Id);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_outbox (id, aggregate_type, aggregate_id, event_type, topic, partition_key, payload)
            VALUES (@Id, @AggregateType, @AggregateId, @EventType, @Topic, @PartitionKey, @Payload::jsonb)
            ON CONFLICT (id) DO NOTHING
            """, outbox, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new PersistResult(inserted.Value.Id, false, inserted.Value.CreatedAt);
    }

    public async Task<CxEvent?> GetByEventIdAsync(string eventId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<CxEvent>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM cx_event WHERE event_id = @eventId",
            new { eventId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<CxEvent>> QueryAsync(EventQuery query, CancellationToken ct = default)
    {
        var sql = new StringBuilder($"SELECT {SelectColumns} FROM cx_event WHERE tenant_id = @TenantId");
        var parameters = new DynamicParameters();
        parameters.Add("TenantId", query.TenantId);

        void Filter(string? value, string clause, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            sql.Append(clause);
            parameters.Add(name, value);
        }

        Filter(query.CustomerId, " AND customer_id = @CustomerId", "CustomerId");
        Filter(query.SessionId, " AND session_id = @SessionId", "SessionId");
        Filter(query.EventName, " AND event_name = @EventName", "EventName");
        Filter(query.EventCategory, " AND event_category = @EventCategory", "EventCategory");
        Filter(query.ProductId, " AND product_id = @ProductId", "ProductId");
        Filter(query.CorrelationId, " AND correlation_id = @CorrelationId", "CorrelationId");

        if (query.From is not null)
        {
            sql.Append(" AND occurred_at >= @From");
            parameters.Add("From", query.From.Value.ToUniversalTime());
        }

        if (query.To is not null)
        {
            sql.Append(" AND occurred_at <= @To");
            parameters.Add("To", query.To.Value.ToUniversalTime());
        }

        if (query.Cursor is { } cursor)
        {
            // Keyset pagination: (occurred_at, event_id) is unique enough to be a stable tiebreaker.
            sql.Append(" AND (occurred_at, event_id) < (@CursorTime, @CursorId)");
            parameters.Add("CursorTime", cursor.Timestamp);
            parameters.Add("CursorId", cursor.Tiebreaker);
        }

        sql.Append(" ORDER BY occurred_at DESC, event_id DESC LIMIT @Limit");
        parameters.Add("Limit", Math.Clamp(query.Limit, 1, 500) + 1);

        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = await connection.QueryAsync<CxEvent>(
            new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<CxEvent>> GetRecentForCustomerAsync(string tenantId, string customerId, int limit,
        DateTimeOffset? since = null, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = await connection.QueryAsync<CxEvent>(new CommandDefinition($"""
            SELECT {SelectColumns} FROM cx_event
            WHERE tenant_id = @tenantId AND customer_id = @customerId
              AND (@since::timestamptz IS NULL OR occurred_at >= @since)
            ORDER BY occurred_at DESC
            LIMIT @limit
            """, new { tenantId, customerId, since = since?.ToUniversalTime(), limit = Math.Clamp(limit, 1, 500) },
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<long> CountAsync(string tenantId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT count(*) FROM cx_event
            WHERE tenant_id = @tenantId AND occurred_at >= @from AND occurred_at < @to
            """, new { tenantId, from = from.ToUniversalTime(), to = to.ToUniversalTime() }, cancellationToken: ct));
    }

    private static object Normalize(CxEvent e) => new
    {
        e.EventId,
        e.TenantId,
        e.CustomerId,
        e.AnonymousId,
        e.SessionId,
        e.EventName,
        e.EventCategory,
        e.SchemaVersion,
        OccurredAt = e.OccurredAt.ToUniversalTime(),
        ReceivedAt = e.ReceivedAt.ToUniversalTime(),
        e.SourceType,
        e.SourceService,
        e.ProductId,
        e.ServiceId,
        e.TraceId,
        e.CorrelationId,
        e.Payload
    };
}
