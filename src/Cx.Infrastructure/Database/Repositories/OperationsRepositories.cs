using System.Text;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Domain.Entities;
using Dapper;

namespace Cx.Infrastructure.Database.Repositories;

public sealed class TaxonomyRepository : ITaxonomyRepository
{
    private const string TopicColumns = """
        id, tenant_id, taxonomy_id, parent_id, code, title, description, keywords, sort_order, is_active,
        created_by, updated_by, created_at, updated_at
        """;

    private readonly CxDataSource _dataSource;

    public TaxonomyRepository(CxDataSource dataSource) => _dataSource = dataSource;

    public async Task<Taxonomy> EnsureAsync(string tenantId, string name, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleAsync<Taxonomy>(new CommandDefinition("""
            INSERT INTO taxonomy (id, tenant_id, name, description)
            VALUES (@id, @tenantId, @name, 'CX topic tree')
            ON CONFLICT (tenant_id, name) DO UPDATE SET name = EXCLUDED.name
            RETURNING id, tenant_id, name, description, active_version_id, revision, created_at
            """, new { id = Guid.NewGuid(), tenantId, name }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Topic>> ListTopicsAsync(Guid taxonomyId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = await connection.QueryAsync<Topic>(new CommandDefinition($"""
            SELECT {TopicColumns} FROM topic WHERE taxonomy_id = @taxonomyId ORDER BY sort_order, title
            """, new { taxonomyId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<Topic?> GetTopicAsync(Guid taxonomyId, Guid topicId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<Topic>(new CommandDefinition($"""
            SELECT {TopicColumns} FROM topic WHERE taxonomy_id = @taxonomyId AND id = @topicId
            """, new { taxonomyId, topicId }, cancellationToken: ct));
    }

    public async Task CreateTopicAsync(Topic topic, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO topic (id, tenant_id, taxonomy_id, parent_id, code, title, description, keywords,
                sort_order, is_active, created_by, updated_by)
            VALUES (@Id, @TenantId, @TaxonomyId, @ParentId, @Code, @Title, @Description, @Keywords,
                @SortOrder, @IsActive, @CreatedBy, @UpdatedBy)
            """, topic, tx, cancellationToken: ct));
        await BumpRevisionAsync(connection, tx, topic.TaxonomyId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task UpdateTopicAsync(Topic topic, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        // The code is deliberately not updatable: stored analyses reference it.
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE topic
            SET parent_id = @ParentId, title = @Title, description = @Description, keywords = @Keywords,
                sort_order = @SortOrder, is_active = @IsActive, updated_by = @UpdatedBy, updated_at = now()
            WHERE id = @Id AND taxonomy_id = @TaxonomyId
            """, topic, tx, cancellationToken: ct));
        await BumpRevisionAsync(connection, tx, topic.TaxonomyId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task DeleteTopicAsync(Guid taxonomyId, Guid topicId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM topic WHERE id = @topicId AND taxonomy_id = @taxonomyId",
            new { taxonomyId, topicId }, tx, cancellationToken: ct));
        await BumpRevisionAsync(connection, tx, taxonomyId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountUsageAsync(string tenantId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        // Current analyses only (superseded ones were replaced), counted under the category
        // and, separately, under the subcategory; a reviewer's override wins over the model.
        var rows = await connection.QueryAsync<(string Code, int Count)>(new CommandDefinition("""
            WITH effective AS (
                SELECT COALESCE(hr.override_result, a.result) AS result
                FROM ai_analysis a
                LEFT JOIN human_review hr ON hr.analysis_id = a.id AND hr.status = 'OVERRIDDEN'
                WHERE a.tenant_id = @tenantId AND a.status = 'succeeded'
            )
            SELECT code, count(*)::int
            FROM (
                SELECT result->>'category' AS code FROM effective
                UNION ALL
                SELECT result->>'subcategory' FROM effective
            ) codes
            WHERE code IS NOT NULL
            GROUP BY code
            """, new { tenantId }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Code, r => r.Count, StringComparer.OrdinalIgnoreCase);
    }

    private static Task BumpRevisionAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction tx, Guid taxonomyId, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "UPDATE taxonomy SET revision = revision + 1 WHERE id = @taxonomyId",
            new { taxonomyId }, tx, cancellationToken: ct));
}

public sealed class AuditRepository : IAuditRepository
{
    private readonly CxDataSource _dataSource;

    public AuditRepository(CxDataSource dataSource) => _dataSource = dataSource;

    public async Task WriteAsync(AuditLog entry, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_log (tenant_id, actor_id, action, resource_type, resource_id, before, after,
                reason, ip_address, occurred_at)
            VALUES (@TenantId, @ActorId, @Action, @ResourceType, @ResourceId, @Before::jsonb, @After::jsonb,
                @Reason, @IpAddress, @OccurredAt)
            """, new
        {
            entry.TenantId,
            entry.ActorId,
            entry.Action,
            entry.ResourceType,
            entry.ResourceId,
            entry.Before,
            entry.After,
            entry.Reason,
            entry.IpAddress,
            OccurredAt = entry.OccurredAt == default ? DateTimeOffset.UtcNow : entry.OccurredAt.ToUniversalTime()
        }, cancellationToken: ct));
    }

    public async Task<Page<AuditLog>> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        var sql = new StringBuilder("""
            SELECT id, tenant_id, actor_id, action, resource_type, resource_id, before, after, reason,
                   ip_address, occurred_at
            FROM audit_log WHERE tenant_id = @tenantId
            """);
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", query.TenantId);

        if (!string.IsNullOrWhiteSpace(query.ActorId)) { sql.Append(" AND actor_id = @actorId"); parameters.Add("actorId", query.ActorId); }
        if (!string.IsNullOrWhiteSpace(query.Action)) { sql.Append(" AND action = @action"); parameters.Add("action", query.Action); }
        if (!string.IsNullOrWhiteSpace(query.ResourceType)) { sql.Append(" AND resource_type = @resourceType"); parameters.Add("resourceType", query.ResourceType); }
        if (!string.IsNullOrWhiteSpace(query.ResourceId)) { sql.Append(" AND resource_id = @resourceId"); parameters.Add("resourceId", query.ResourceId); }
        if (query.From is not null) { sql.Append(" AND occurred_at >= @from"); parameters.Add("from", query.From.Value.ToUniversalTime()); }
        if (query.To is not null) { sql.Append(" AND occurred_at <= @to"); parameters.Add("to", query.To.Value.ToUniversalTime()); }

        if (query.Cursor is { } c)
        {
            sql.Append(" AND (occurred_at, id) < (@cursorTime, @cursorId::bigint)");
            parameters.Add("cursorTime", c.Timestamp);
            parameters.Add("cursorId", c.Tiebreaker);
        }

        var take = Math.Clamp(query.Limit, 1, 200);
        sql.Append(" ORDER BY occurred_at DESC, id DESC LIMIT @limit");
        parameters.Add("limit", take + 1);

        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = (await connection.QueryAsync<AuditLog>(
            new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct))).ToList();

        return Page<AuditLog>.From(rows, take, x => new Cursor(x.OccurredAt, x.Id.ToString()).Encode());
    }
}

public sealed class ProcessingJobRepository : IProcessingJobRepository
{
    private readonly CxDataSource _dataSource;

    public ProcessingJobRepository(CxDataSource dataSource) => _dataSource = dataSource;

    public async Task<bool> TryClaimAsync(string tenantId, string jobKey, string jobType, bool force = false,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);

        // The unique job_key is what makes a retry safe: a second delivery of the same work
        // finds the row already terminal and is refused (spec 17).
        var claimed = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition("""
            INSERT INTO processing_job (id, tenant_id, job_key, job_type, state, attempts, updated_at)
            VALUES (@id, @tenantId, @jobKey, @jobType, 'RUNNING', 1, now())
            ON CONFLICT (job_key) DO UPDATE
            SET state = 'RUNNING',
                attempts = processing_job.attempts + 1,
                updated_at = now()
            WHERE processing_job.state IN ('PENDING', 'FAILED_RETRYABLE')
               OR @force = true
               OR processing_job.updated_at < now() - interval '30 minutes'
            RETURNING true
            """, new { id = Guid.NewGuid(), tenantId, jobKey, jobType, force }, cancellationToken: ct));

        return claimed ?? false;
    }

    public async Task CompleteAsync(string jobKey, string? resultRef, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE processing_job
            SET state = 'SUCCEEDED', result_ref = @resultRef, last_error = NULL, error_category = NULL,
                updated_at = now()
            WHERE job_key = @jobKey
            """, new { jobKey, resultRef }, cancellationToken: ct));
    }

    public async Task FailAsync(string jobKey, string error, ErrorCategory category, bool retryable,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE processing_job
            SET state = @state, last_error = @error, error_category = @category, updated_at = now()
            WHERE job_key = @jobKey
            """, new
        {
            jobKey,
            state = retryable ? ProcessingJobStates.FailedRetryable : ProcessingJobStates.FailedFinal,
            error = error.Length > 4000 ? error[..4000] : error,
            category = category.ToString()
        }, cancellationToken: ct));
    }

    public async Task<ProcessingJob?> GetAsync(string jobKey, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<ProcessingJob>(new CommandDefinition("""
            SELECT id, tenant_id, job_key, job_type, state, attempts, last_error, error_category,
                   result_ref, created_at, updated_at
            FROM processing_job WHERE job_key = @jobKey
            """, new { jobKey }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ProcessingJob>> ListFailedAsync(string tenantId, int limit,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = await connection.QueryAsync<ProcessingJob>(new CommandDefinition("""
            SELECT id, tenant_id, job_key, job_type, state, attempts, last_error, error_category,
                   result_ref, created_at, updated_at
            FROM processing_job
            WHERE tenant_id = @tenantId AND state IN ('FAILED_RETRYABLE', 'FAILED_FINAL')
            ORDER BY updated_at DESC LIMIT @limit
            """, new { tenantId, limit = Math.Clamp(limit, 1, 500) }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyDictionary<string, long>> CountByStateAsync(string tenantId,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = await connection.QueryAsync<(string State, long Count)>(new CommandDefinition(
            "SELECT state, count(*) FROM processing_job WHERE tenant_id = @tenantId GROUP BY state",
            new { tenantId }, cancellationToken: ct));
        return rows.ToDictionary(r => r.State, r => r.Count, StringComparer.Ordinal);
    }
}

public sealed class IntegrationCheckpointRepository : IIntegrationCheckpointRepository
{
    private const string SelectColumns = """
        id, tenant_id, integration_name, integration_type, cursor, status, last_success_at,
        last_error_at, last_error, records_processed, updated_at
        """;

    private readonly CxDataSource _dataSource;

    public IntegrationCheckpointRepository(CxDataSource dataSource) => _dataSource = dataSource;

    public async Task<IntegrationCheckpoint?> GetAsync(string tenantId, string integrationName,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<IntegrationCheckpoint>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM integration_checkpoint WHERE tenant_id = @tenantId AND integration_name = @integrationName",
            new { tenantId, integrationName }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<IntegrationCheckpoint>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = await connection.QueryAsync<IntegrationCheckpoint>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM integration_checkpoint WHERE tenant_id = @tenantId ORDER BY integration_name",
            new { tenantId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task SaveCursorAsync(string tenantId, string integrationName, string integrationType, string? cursor,
        long processed, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO integration_checkpoint (id, tenant_id, integration_name, integration_type, cursor,
                status, last_success_at, records_processed, updated_at)
            VALUES (@id, @tenantId, @integrationName, @integrationType, @cursor, 'healthy', now(), @processed, now())
            ON CONFLICT (tenant_id, integration_name) DO UPDATE
            SET cursor = EXCLUDED.cursor,
                status = 'healthy',
                last_success_at = now(),
                records_processed = integration_checkpoint.records_processed + EXCLUDED.records_processed,
                updated_at = now()
            """, new { id = Guid.NewGuid(), tenantId, integrationName, integrationType, cursor, processed },
            cancellationToken: ct));
    }

    public async Task RecordErrorAsync(string tenantId, string integrationName, string integrationType, string error,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO integration_checkpoint (id, tenant_id, integration_name, integration_type, status,
                last_error_at, last_error, updated_at)
            VALUES (@id, @tenantId, @integrationName, @integrationType, 'error', now(), @error, now())
            ON CONFLICT (tenant_id, integration_name) DO UPDATE
            SET status = 'error', last_error_at = now(), last_error = EXCLUDED.last_error, updated_at = now()
            """, new
        {
            id = Guid.NewGuid(),
            tenantId,
            integrationName,
            integrationType,
            error = error.Length > 4000 ? error[..4000] : error
        }, cancellationToken: ct));
    }
}
