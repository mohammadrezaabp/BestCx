using System.Text;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Domain.Entities;
using Dapper;

namespace Cx.Infrastructure.Database.Repositories;

public sealed class AiAnalysisRepository : IAiAnalysisRepository
{
    private const string SelectColumns = """
        id, tenant_id, entity_type, entity_id, customer_id, analysis_type, model_provider, model_name,
        model_version, decision_model, prompt_version, taxonomy_version, input_hash, result, confidence,
        status, created_at, supersedes_analysis_id
        """;

    private readonly CxDataSource _dataSource;

    public AiAnalysisRepository(CxDataSource dataSource) => _dataSource = dataSource;

    public async Task<Guid> InsertAsync(AiAnalysis analysis, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var id = analysis.Id == Guid.Empty ? Guid.NewGuid() : analysis.Id;

        // Find the row this one replaces, then mark it superseded. The old result is kept
        // forever - reprocessing creates a version, it never edits one (spec 22).
        var previous = analysis.SupersedesAnalysisId ?? await connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition("""
                SELECT id FROM ai_analysis
                WHERE tenant_id = @tenantId AND entity_type = @entityType AND entity_id = @entityId
                  AND analysis_type = @analysisType AND status = 'succeeded'
                ORDER BY created_at DESC LIMIT 1
                """, new
            {
                tenantId = analysis.TenantId,
                entityType = analysis.EntityType,
                entityId = analysis.EntityId,
                analysisType = analysis.AnalysisType
            }, tx, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ai_analysis (id, tenant_id, entity_type, entity_id, customer_id, analysis_type,
                model_provider, model_name, model_version, decision_model, prompt_version, taxonomy_version,
                input_hash, result, confidence, status, supersedes_analysis_id)
            VALUES (@Id, @TenantId, @EntityType, @EntityId, @CustomerId, @AnalysisType,
                @ModelProvider, @ModelName, @ModelVersion, @DecisionModel, @PromptVersion, @TaxonomyVersion,
                @InputHash,
                @Result::jsonb, @Confidence, @Status, @SupersedesAnalysisId)
            """, new
        {
            Id = id,
            analysis.TenantId,
            analysis.EntityType,
            analysis.EntityId,
            analysis.CustomerId,
            analysis.AnalysisType,
            analysis.ModelProvider,
            analysis.ModelName,
            analysis.ModelVersion,
            analysis.DecisionModel,
            analysis.PromptVersion,
            analysis.TaxonomyVersion,
            analysis.InputHash,
            analysis.Result,
            analysis.Confidence,
            analysis.Status,
            SupersedesAnalysisId = previous
        }, tx, cancellationToken: ct));

        if (previous is not null && analysis.Status == AiAnalysisStatuses.Succeeded)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE ai_analysis SET status = 'superseded' WHERE id = @previous",
                new { previous }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<AiAnalysis?> GetAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<AiAnalysis>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM ai_analysis WHERE tenant_id = @tenantId AND id = @id",
            new { tenantId, id }, cancellationToken: ct));
    }

    public async Task<AiAnalysis?> GetLatestForEntityAsync(string tenantId, string entityType, string entityId,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<AiAnalysis>(new CommandDefinition($"""
            SELECT {SelectColumns} FROM ai_analysis
            WHERE tenant_id = @tenantId AND entity_type = @entityType AND entity_id = @entityId
              AND status <> 'failed'
            ORDER BY created_at DESC LIMIT 1
            """, new { tenantId, entityType, entityId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AiAnalysis>> GetVersionsForEntityAsync(string tenantId, string entityType,
        string entityId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = await connection.QueryAsync<AiAnalysis>(new CommandDefinition($"""
            SELECT {SelectColumns} FROM ai_analysis
            WHERE tenant_id = @tenantId AND entity_type = @entityType AND entity_id = @entityId
            ORDER BY created_at DESC
            """, new { tenantId, entityType, entityId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<AiAnalysis>> GetRecentForCustomerAsync(string tenantId, string customerId,
        int limit, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = await connection.QueryAsync<AiAnalysis>(new CommandDefinition($"""
            SELECT {SelectColumns} FROM ai_analysis
            WHERE tenant_id = @tenantId AND customer_id = @customerId AND status <> 'failed'
            ORDER BY created_at DESC LIMIT @limit
            """, new { tenantId, customerId, limit = Math.Clamp(limit, 1, 200) }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task MarkSupersededAsync(Guid analysisId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE ai_analysis SET status = 'superseded' WHERE id = @analysisId",
            new { analysisId }, cancellationToken: ct));
    }
}

public sealed class HumanReviewRepository : IHumanReviewRepository
{
    private const string SelectColumns = """
        id, tenant_id, analysis_id, entity_type, entity_id, status, review_reason, reviewer_id,
        previous_result, override_result, reason, notes, created_at, reviewed_at
        """;

    private readonly CxDataSource _dataSource;

    public HumanReviewRepository(CxDataSource dataSource) => _dataSource = dataSource;

    public async Task<Guid> CreateAsync(HumanReview review, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var id = review.Id == Guid.Empty ? Guid.NewGuid() : review.Id;

        // One review item per analysis: a redelivered result must not queue the same work twice.
        var inserted = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            INSERT INTO human_review (id, tenant_id, analysis_id, entity_type, entity_id, status,
                review_reason, previous_result)
            VALUES (@Id, @TenantId, @AnalysisId, @EntityType, @EntityId, @Status, @ReviewReason,
                @PreviousResult::jsonb)
            ON CONFLICT (analysis_id) DO NOTHING
            RETURNING id
            """, new
        {
            Id = id,
            review.TenantId,
            review.AnalysisId,
            review.EntityType,
            review.EntityId,
            review.Status,
            review.ReviewReason,
            review.PreviousResult
        }, cancellationToken: ct));

        return inserted ?? await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT id FROM human_review WHERE analysis_id = @analysisId",
            new { analysisId = review.AnalysisId }, cancellationToken: ct));
    }

    public async Task<HumanReview?> GetAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<HumanReview>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM human_review WHERE tenant_id = @tenantId AND id = @id",
            new { tenantId, id }, cancellationToken: ct));
    }

    public async Task<HumanReview?> GetByAnalysisAsync(string tenantId, Guid analysisId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<HumanReview>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM human_review WHERE tenant_id = @tenantId AND analysis_id = @analysisId",
            new { tenantId, analysisId }, cancellationToken: ct));
    }

    public async Task<Page<HumanReviewListItem>> QueryAsync(ReviewQuery query, CancellationToken ct = default)
    {
        // Positional records bind by constructor parameter name, and Dapper's
        // underscore matching does not apply there - so every column is aliased.
        var sql = new StringBuilder("""
            SELECT hr.id                   AS "Id",
                   hr.analysis_id          AS "AnalysisId",
                   hr.entity_type          AS "EntityType",
                   hr.entity_id            AS "EntityId",
                   a.customer_id           AS "CustomerId",
                   hr.status               AS "Status",
                   hr.review_reason        AS "ReviewReason",
                   a.confidence            AS "Confidence",
                   a.result->>'sentiment'  AS "Sentiment",
                   a.result->>'category'   AS "Category",
                   a.result->>'severity'   AS "Severity",
                   a.result->>'summary'    AS "Summary",
                   hr.created_at           AS "CreatedAt",
                   hr.reviewed_at          AS "ReviewedAt",
                   hr.reviewer_id          AS "ReviewerId"
            FROM human_review hr
            JOIN ai_analysis a ON a.id = hr.analysis_id
            WHERE hr.tenant_id = @tenantId
            """);

        var parameters = new DynamicParameters();
        parameters.Add("tenantId", query.TenantId);

        if (!string.IsNullOrWhiteSpace(query.Status)) { sql.Append(" AND hr.status = @status"); parameters.Add("status", query.Status); }
        if (!string.IsNullOrWhiteSpace(query.EntityType)) { sql.Append(" AND hr.entity_type = @entityType"); parameters.Add("entityType", query.EntityType); }
        if (!string.IsNullOrWhiteSpace(query.ReviewReason)) { sql.Append(" AND hr.review_reason = @reviewReason"); parameters.Add("reviewReason", query.ReviewReason); }

        if (query.Cursor is { } c)
        {
            sql.Append(" AND (hr.created_at, hr.id) < (@cursorTime, @cursorId::uuid)");
            parameters.Add("cursorTime", c.Timestamp);
            parameters.Add("cursorId", c.Tiebreaker);
        }

        var take = Math.Clamp(query.Limit, 1, 200);
        sql.Append(" ORDER BY hr.created_at DESC, hr.id DESC LIMIT @limit");
        parameters.Add("limit", take + 1);

        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = (await connection.QueryAsync<HumanReviewListItem>(
            new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct))).ToList();

        return Page<HumanReviewListItem>.From(rows, take, x => new Cursor(x.CreatedAt, x.Id.ToString()).Encode());
    }

    public async Task ResolveAsync(Guid id, string status, string reviewerId, string? overrideResult, string? reason,
        string? notes, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE human_review
            SET status = @status,
                reviewer_id = @reviewerId,
                override_result = COALESCE(@overrideResult::jsonb, override_result),
                reason = COALESCE(@reason, reason),
                notes = COALESCE(@notes, notes),
                reviewed_at = now()
            WHERE id = @id
            """, new { id, status, reviewerId, overrideResult, reason, notes }, cancellationToken: ct));
    }

    public async Task<long> BacklogCountAsync(string tenantId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM human_review WHERE tenant_id = @tenantId AND status = 'PENDING'",
            new { tenantId }, cancellationToken: ct));
    }
}
