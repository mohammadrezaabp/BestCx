using Cx.Application.Abstractions;
using Cx.Domain.Entities;
using Dapper;

namespace Cx.Infrastructure.Database.Repositories;

public sealed class OutboxRepository : IOutboxRepository
{
    private readonly CxDataSource _dataSource;

    public OutboxRepository(CxDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<OutboxEvent>> FetchUnpublishedAsync(int batchSize, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        // SKIP LOCKED lets several relay instances share the table without double publishing.
        var rows = await connection.QueryAsync<OutboxEvent>(new CommandDefinition("""
            SELECT id, aggregate_type, aggregate_id, event_type, topic, partition_key, payload, created_at, published_at
            FROM cx_outbox
            WHERE published_at IS NULL
            ORDER BY created_at
            LIMIT @batchSize
            FOR UPDATE SKIP LOCKED
            """, new { batchSize }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task MarkPublishedAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return;
        await using var connection = await _dataSource.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE cx_outbox SET published_at = now() WHERE id = ANY(@ids)",
            new { ids = ids.ToArray() }, cancellationToken: ct));
    }

    public async Task<long> CountUnpublishedAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM cx_outbox WHERE published_at IS NULL", cancellationToken: ct));
    }

    public async Task<TimeSpan?> OldestUnpublishedAgeAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var oldest = await connection.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
            "SELECT min(created_at) FROM cx_outbox WHERE published_at IS NULL", cancellationToken: ct));
        return oldest is null ? null : DateTimeOffset.UtcNow - oldest.Value;
    }
}
