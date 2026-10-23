using System.Text;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Query;
using Cx.Domain.Entities;
using Dapper;

namespace Cx.Infrastructure.Database.Repositories;

public sealed class FeedbackRepository : IFeedbackRepository
{
    private const string SelectColumns = """
        id, tenant_id, event_id, provider, external_id, customer_id, product_id, feedback_type,
        rating, nps_score, csat_score, ces_score, selected_topic, comment, channel, call_id, status, assignee_id, reviewed,
        submitted_at, created_at
        """;

    private readonly CxDataSource _dataSource;

    public FeedbackRepository(CxDataSource dataSource) => _dataSource = dataSource;

    public async Task<Guid> UpsertAsync(Feedback feedback, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        // The provider's own id is the idempotency key: replaying a webhook must not
        // create a second row (spec 13).
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO feedback (id, tenant_id, event_id, provider, external_id, customer_id, product_id,
                feedback_type, rating, nps_score, csat_score, ces_score, selected_topic, comment, channel, call_id, status, submitted_at)
            VALUES (@Id, @TenantId, @EventId, @Provider, @ExternalId, @CustomerId, @ProductId,
                @FeedbackType, @Rating, @NpsScore, @CsatScore, @CesScore, @SelectedTopic, @Comment, @Channel, @CallId, @Status, @SubmittedAt)
            ON CONFLICT (tenant_id, provider, external_id) DO UPDATE
            SET comment = COALESCE(EXCLUDED.comment, feedback.comment),
                rating = COALESCE(EXCLUDED.rating, feedback.rating),
                nps_score = COALESCE(EXCLUDED.nps_score, feedback.nps_score),
                csat_score = COALESCE(EXCLUDED.csat_score, feedback.csat_score),
                ces_score = COALESCE(EXCLUDED.ces_score, feedback.ces_score),
                selected_topic = COALESCE(EXCLUDED.selected_topic, feedback.selected_topic),
                customer_id = COALESCE(EXCLUDED.customer_id, feedback.customer_id),
                product_id = COALESCE(EXCLUDED.product_id, feedback.product_id),
                event_id = COALESCE(feedback.event_id, EXCLUDED.event_id),
                call_id = COALESCE(feedback.call_id, EXCLUDED.call_id)
            RETURNING id
            """, new
        {
            Id = feedback.Id == Guid.Empty ? Guid.NewGuid() : feedback.Id,
            feedback.TenantId,
            feedback.EventId,
            feedback.Provider,
            feedback.ExternalId,
            feedback.CustomerId,
            feedback.ProductId,
            feedback.FeedbackType,
            feedback.Rating,
            feedback.NpsScore,
            feedback.CsatScore,
            feedback.CesScore,
            feedback.SelectedTopic,
            feedback.Comment,
            feedback.Channel,
            feedback.CallId,
            feedback.Status,
            SubmittedAt = feedback.SubmittedAt.ToUniversalTime()
        }, cancellationToken: ct));
    }

    public async Task<Feedback?> GetAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<Feedback>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM feedback WHERE tenant_id = @tenantId AND id = @id",
            new { tenantId, id }, cancellationToken: ct));
    }

    public async Task<Page<Feedback>> QueryAsync(InboxQuery query, CancellationToken ct = default)
    {
        var sql = new StringBuilder($"SELECT {SelectColumns} FROM feedback WHERE tenant_id = @tenantId");
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", query.TenantId);

        if (!string.IsNullOrWhiteSpace(query.CustomerId)) { sql.Append(" AND customer_id = @customerId"); parameters.Add("customerId", query.CustomerId); }
        if (!string.IsNullOrWhiteSpace(query.ProductId)) { sql.Append(" AND product_id = @productId"); parameters.Add("productId", query.ProductId); }
        if (!string.IsNullOrWhiteSpace(query.Status)) { sql.Append(" AND status = @status"); parameters.Add("status", query.Status); }
        if (!string.IsNullOrWhiteSpace(query.AssigneeId)) { sql.Append(" AND assignee_id = @assigneeId"); parameters.Add("assigneeId", query.AssigneeId); }
        if (query.Reviewed is not null) { sql.Append(" AND reviewed = @reviewed"); parameters.Add("reviewed", query.Reviewed); }
        if (!string.IsNullOrWhiteSpace(query.Search)) { sql.Append(" AND comment ILIKE @search"); parameters.Add("search", $"%{query.Search.Trim()}%"); }
        if (query.From is not null) { sql.Append(" AND submitted_at >= @from"); parameters.Add("from", query.From.Value.ToUniversalTime()); }
        if (query.To is not null) { sql.Append(" AND submitted_at <= @to"); parameters.Add("to", query.To.Value.ToUniversalTime()); }

        if (query.Cursor is { } c)
        {
            sql.Append(" AND (submitted_at, id) < (@cursorTime, @cursorId::uuid)");
            parameters.Add("cursorTime", c.Timestamp);
            parameters.Add("cursorId", c.Tiebreaker);
        }

        var take = Math.Clamp(query.Limit, 1, 200);
        sql.Append(" ORDER BY submitted_at DESC, id DESC LIMIT @limit");
        parameters.Add("limit", take + 1);

        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = (await connection.QueryAsync<Feedback>(
            new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct))).ToList();

        return Page<Feedback>.From(rows, take, x => new Cursor(x.SubmittedAt, x.Id.ToString()).Encode());
    }

    public async Task<IReadOnlyList<Feedback>> GetRecentForCustomerAsync(string tenantId, string customerId, int limit,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = await connection.QueryAsync<Feedback>(new CommandDefinition($"""
            SELECT {SelectColumns} FROM feedback
            WHERE tenant_id = @tenantId AND customer_id = @customerId
            ORDER BY submitted_at DESC LIMIT @limit
            """, new { tenantId, customerId, limit = Math.Clamp(limit, 1, 100) }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task UpdateReviewStateAsync(string tenantId, Guid id, string? status, string? assigneeId,
        bool? reviewed, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE feedback
            SET status = COALESCE(@status, status),
                assignee_id = COALESCE(@assigneeId, assignee_id),
                reviewed = COALESCE(@reviewed, reviewed)
            WHERE tenant_id = @tenantId AND id = @id
            """, new { tenantId, id, status, assigneeId, reviewed }, cancellationToken: ct));
    }

    public async Task SetVoiceTranscriptAsync(string tenantId, string callId, string transcript,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE feedback SET comment = CASE
                WHEN strpos(comment, @marker) > 0
                    THEN left(comment, strpos(comment, @marker) + char_length(@marker) - 1) || @transcript
                ELSE @transcript END
            WHERE tenant_id = @tenantId AND call_id = @callId
            """, new { tenantId, callId, transcript, marker = VoiceFeedbackComment.TranscriptMarker },
            cancellationToken: ct));
    }

    /// <summary>
    /// One row per feedback with its customer name and the effective AI labels (a human
    /// override wins over the model), plus the state of its analysis job so a failure is
    /// visible instead of looking like "still pending".
    /// </summary>
    private const string GridBase = """
        WITH base AS (
            SELECT f.id, f.customer_id, ci.display_name AS customer_name, f.comment, f.channel,
                   f.product_id, f.feedback_type, f.rating, f.nps_score, f.csat_score, f.ces_score, f.status,
                   f.reviewed, f.submitted_at, f.call_id,
                   CASE WHEN f.call_id IS NULL THEN 'text' ELSE 'voice' END AS kind,
                   a.id AS analysis_id, a.model_provider AS analysis_provider,
                   COALESCE(CASE WHEN hr.status = 'OVERRIDDEN' THEN hr.override_result END, a.result) AS r,
                   COALESCE(hr.status IN ('APPROVED', 'OVERRIDDEN'), false) AS human_reviewed,
                   CASE
                       WHEN j.state = 'FAILED' AND (a.id IS NULL OR j.updated_at > a.created_at) THEN 'failed'
                       WHEN a.id IS NOT NULL THEN 'analyzed'
                       ELSE 'pending'
                   END AS analysis_state,
                   CASE WHEN j.state = 'FAILED' THEN j.last_error END AS analysis_error
            FROM feedback f
            LEFT JOIN customer_identity ci
                   ON ci.tenant_id = f.tenant_id AND ci.customer_id = f.customer_id
            LEFT JOIN LATERAL (
                SELECT x.id, x.result, x.model_provider, x.created_at
                FROM ai_analysis x
                WHERE x.tenant_id = f.tenant_id
                  AND x.entity_type = CASE WHEN f.call_id IS NULL THEN 'cx_event' ELSE 'voice_call' END
                  AND x.entity_id = COALESCE(f.call_id, f.event_id)
                ORDER BY x.created_at DESC
                LIMIT 1
            ) a ON true
            LEFT JOIN human_review hr ON hr.analysis_id = a.id
            LEFT JOIN LATERAL (
                SELECT pj.state, pj.last_error, pj.updated_at
                FROM processing_job pj
                WHERE pj.tenant_id = f.tenant_id
                  AND (pj.job_key LIKE (CASE WHEN f.call_id IS NULL THEN 'cx_event' ELSE 'voice_call' END)
                                      || ':' || COALESCE(f.call_id, f.event_id) || ':analyze:%'
                       OR (f.call_id IS NOT NULL AND pj.job_key LIKE (
                              SELECT vr.id::text FROM voice_recording vr
                              JOIN voice_call vc ON vc.id = vr.call_id
                              WHERE vc.tenant_id = f.tenant_id AND vc.call_id = f.call_id
                              ORDER BY vr.created_at DESC LIMIT 1) || ':transcribe:%'))
                ORDER BY pj.updated_at DESC
                LIMIT 1
            ) j ON true
            WHERE f.tenant_id = @tenantId
        )
        """;

    /// <summary>
    /// The inbox grid. Numbered pages, because operators work through a bounded daily
    /// volume and want "page 2 of 7", not an endless feed.
    /// </summary>
    public async Task<NumberedPage<FeedbackGridRow>> QueryGridAsync(FeedbackGridQuery query,
        CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", query.TenantId);

        var filters = new List<string>();
        void Filter(string clause, string name, object? value)
        {
            filters.Add(clause);
            parameters.Add(name, value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            Filter("(comment ILIKE @search OR customer_id ILIKE @search OR customer_name ILIKE @search " +
                   "OR r->>'summary' ILIKE @search)", "search", $"%{query.Search.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(query.CustomerId)) Filter("customer_id = @customerId", "customerId", query.CustomerId);
        if (!string.IsNullOrWhiteSpace(query.Channel)) Filter("channel = @channel", "channel", query.Channel);
        if (!string.IsNullOrWhiteSpace(query.Sentiment)) Filter("r->>'sentiment' = @sentiment", "sentiment", query.Sentiment);
        if (!string.IsNullOrWhiteSpace(query.Severity)) Filter("r->>'severity' = @severity", "severity", query.Severity);
        if (!string.IsNullOrWhiteSpace(query.Category)) Filter("r->>'category' = @category", "category", query.Category);
        if (query.Subcategories is { Count: > 0 })
            Filter("r->>'subcategory' = ANY(@subcategories)", "subcategories", query.Subcategories.ToArray());
        if (query.Kind == "voice") filters.Add("call_id IS NOT NULL");
        if (query.Kind == "text") filters.Add("call_id IS NULL");
        if (query.Inbox == "complaints") filters.Add("feedback_type = 'complaint'");
        if (query.Inbox == "feedback") filters.Add("feedback_type IS DISTINCT FROM 'complaint'");
        if (query.Kind is "complaint" or "voc") Filter("feedback_type = @feedbackType", "feedbackType", query.Kind);
        if (query.AnalysisState is "analyzed" or "pending" or "failed")
            Filter("analysis_state = @analysisState", "analysisState", query.AnalysisState);
        if (query.From is not null) Filter("submitted_at >= @from", "from", query.From.Value.ToUniversalTime());
        if (query.To is not null) Filter("submitted_at <= @to", "to", query.To.Value.ToUniversalTime());

        // Status is applied separately: the per-status counts ignore it, so each status tab
        // can show how many rows it would hold under the other filters.
        var where = filters.Count == 0 ? "TRUE" : string.Join(" AND ", filters);
        var statusClause = "TRUE";
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            statusClause = "status = @status";
            parameters.Add("status", query.Status);
        }

        var pageSize = Math.Clamp(query.PageSize, 5, 100);
        var page = Math.Max(1, query.Page);
        parameters.Add("limit", pageSize);
        parameters.Add("offset", (page - 1) * pageSize);

        var sql = $"""
            {GridBase}
            SELECT id, kind, customer_id, customer_name, comment, channel, product_id, feedback_type,
                   rating, nps_score, csat_score, ces_score, status, reviewed, submitted_at, call_id,
                   analysis_id, analysis_provider, analysis_state, analysis_error, human_reviewed,
                   r->>'sentiment' AS sentiment, r->>'severity' AS severity, r->>'category' AS category,
                   r->>'subcategory' AS subcategory,
                   r->>'summary' AS summary,
                   CASE WHEN jsonb_typeof(r->'confidence') = 'number' THEN (r->>'confidence')::numeric END AS confidence,
                   count(*) OVER () AS total_count
            FROM base
            WHERE {where} AND {statusClause}
            ORDER BY submitted_at DESC, id DESC
            LIMIT @limit OFFSET @offset;

            {GridBase}
            SELECT status AS key, count(*) AS value FROM base WHERE {where} GROUP BY status;
            """;

        await using var connection = await _dataSource.OpenAsync(ct);
        await using var grid = await connection.QueryMultipleAsync(
            new CommandDefinition(sql, parameters, cancellationToken: ct));

        var rows = (await grid.ReadAsync<FeedbackGridRow>()).ToList();
        var counts = (await grid.ReadAsync<(string Key, long Value)>())
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);

        // count(*) OVER () rides on the page rows; past the last page fall back to the counts.
        var total = rows.Count > 0
            ? rows[0].TotalCount
            : string.IsNullOrWhiteSpace(query.Status) ? counts.Values.Sum() : counts.GetValueOrDefault(query.Status);

        return new NumberedPage<FeedbackGridRow>
        {
            Items = rows,
            Total = total,
            Page = page,
            PageSize = pageSize,
            StatusCounts = counts
        };
    }
}

public sealed class SupportCaseRepository : ISupportCaseRepository
{
    private const string SelectColumns = """
        id, tenant_id, provider, external_id, customer_id, product_id, subject, description, status,
        priority, channel, assignee_id, escalated, correlation_id, opened_at, first_response_at,
        resolved_at, updated_at, created_at
        """;

    private readonly CxDataSource _dataSource;

    public SupportCaseRepository(CxDataSource dataSource) => _dataSource = dataSource;

    public async Task<Guid> UpsertAsync(SupportCase supportCase, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO support_case (id, tenant_id, provider, external_id, customer_id, product_id, subject,
                description, status, priority, channel, assignee_id, escalated, correlation_id, opened_at,
                first_response_at, resolved_at, updated_at)
            VALUES (@Id, @TenantId, @Provider, @ExternalId, @CustomerId, @ProductId, @Subject,
                @Description, @Status, @Priority, @Channel, @AssigneeId, @Escalated, @CorrelationId, @OpenedAt,
                @FirstResponseAt, @ResolvedAt, now())
            ON CONFLICT (tenant_id, provider, external_id) DO UPDATE
            SET status = EXCLUDED.status,
                priority = COALESCE(EXCLUDED.priority, support_case.priority),
                subject = COALESCE(EXCLUDED.subject, support_case.subject),
                description = COALESCE(EXCLUDED.description, support_case.description),
                customer_id = COALESCE(EXCLUDED.customer_id, support_case.customer_id),
                product_id = COALESCE(EXCLUDED.product_id, support_case.product_id),
                assignee_id = COALESCE(EXCLUDED.assignee_id, support_case.assignee_id),
                escalated = support_case.escalated OR EXCLUDED.escalated,
                first_response_at = COALESCE(support_case.first_response_at, EXCLUDED.first_response_at),
                resolved_at = COALESCE(EXCLUDED.resolved_at, support_case.resolved_at),
                correlation_id = COALESCE(EXCLUDED.correlation_id, support_case.correlation_id),
                updated_at = now()
            RETURNING id
            """, new
        {
            Id = supportCase.Id == Guid.Empty ? Guid.NewGuid() : supportCase.Id,
            supportCase.TenantId,
            supportCase.Provider,
            supportCase.ExternalId,
            supportCase.CustomerId,
            supportCase.ProductId,
            supportCase.Subject,
            supportCase.Description,
            supportCase.Status,
            supportCase.Priority,
            supportCase.Channel,
            supportCase.AssigneeId,
            supportCase.Escalated,
            supportCase.CorrelationId,
            OpenedAt = supportCase.OpenedAt.ToUniversalTime(),
            FirstResponseAt = supportCase.FirstResponseAt?.ToUniversalTime(),
            ResolvedAt = supportCase.ResolvedAt?.ToUniversalTime()
        }, cancellationToken: ct));
    }

    public async Task<SupportCase?> GetAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<SupportCase>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM support_case WHERE tenant_id = @tenantId AND id = @id",
            new { tenantId, id }, cancellationToken: ct));
    }

    public async Task<Page<SupportCase>> QueryAsync(InboxQuery query, CancellationToken ct = default)
    {
        var sql = new StringBuilder($"SELECT {SelectColumns} FROM support_case WHERE tenant_id = @tenantId");
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", query.TenantId);

        if (!string.IsNullOrWhiteSpace(query.CustomerId)) { sql.Append(" AND customer_id = @customerId"); parameters.Add("customerId", query.CustomerId); }
        if (!string.IsNullOrWhiteSpace(query.ProductId)) { sql.Append(" AND product_id = @productId"); parameters.Add("productId", query.ProductId); }
        if (!string.IsNullOrWhiteSpace(query.Status)) { sql.Append(" AND status = @status"); parameters.Add("status", query.Status); }
        if (!string.IsNullOrWhiteSpace(query.AssigneeId)) { sql.Append(" AND assignee_id = @assigneeId"); parameters.Add("assigneeId", query.AssigneeId); }
        if (!string.IsNullOrWhiteSpace(query.Search)) { sql.Append(" AND (subject ILIKE @search OR description ILIKE @search)"); parameters.Add("search", $"%{query.Search.Trim()}%"); }
        if (query.From is not null) { sql.Append(" AND opened_at >= @from"); parameters.Add("from", query.From.Value.ToUniversalTime()); }
        if (query.To is not null) { sql.Append(" AND opened_at <= @to"); parameters.Add("to", query.To.Value.ToUniversalTime()); }

        if (query.Cursor is { } c)
        {
            sql.Append(" AND (opened_at, id) < (@cursorTime, @cursorId::uuid)");
            parameters.Add("cursorTime", c.Timestamp);
            parameters.Add("cursorId", c.Tiebreaker);
        }

        var take = Math.Clamp(query.Limit, 1, 200);
        sql.Append(" ORDER BY opened_at DESC, id DESC LIMIT @limit");
        parameters.Add("limit", take + 1);

        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = (await connection.QueryAsync<SupportCase>(
            new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct))).ToList();

        return Page<SupportCase>.From(rows, take, x => new Cursor(x.OpenedAt, x.Id.ToString()).Encode());
    }

    public async Task<IReadOnlyList<SupportCase>> GetOpenForCustomerAsync(string tenantId, string customerId,
        int limit, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = await connection.QueryAsync<SupportCase>(new CommandDefinition($"""
            SELECT {SelectColumns} FROM support_case
            WHERE tenant_id = @tenantId AND customer_id = @customerId AND resolved_at IS NULL
            ORDER BY opened_at DESC LIMIT @limit
            """, new { tenantId, customerId, limit = Math.Clamp(limit, 1, 100) }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<SupportCase>> GetRecentForCustomerAsync(string tenantId, string customerId,
        int limit, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = await connection.QueryAsync<SupportCase>(new CommandDefinition($"""
            SELECT {SelectColumns} FROM support_case
            WHERE tenant_id = @tenantId AND customer_id = @customerId
            ORDER BY opened_at DESC LIMIT @limit
            """, new { tenantId, customerId, limit = Math.Clamp(limit, 1, 100) }, cancellationToken: ct));
        return rows.ToList();
    }
}
