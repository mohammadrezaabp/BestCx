using Cx.Application.Common;
using Cx.Application.Query;
using Dapper;

namespace Cx.Infrastructure.Database.Repositories;

/// <summary>
/// Read-side queries for the CX panel.
/// The timeline is merged on read from the source-of-truth tables rather than kept in a
/// separate projection: PostgreSQL is the SoT (rule R3) and a merge-on-read view can never
/// drift from it. If read volume outgrows this, the same shape can be materialized.
/// </summary>
public sealed class InsightsRepository : IInsightsRepository
{
    private readonly CxDataSource _dataSource;

    public InsightsRepository(CxDataSource dataSource) => _dataSource = dataSource;

    private const string TimelineUnion = """
        SELECT 'event' AS kind,
               e.event_id AS id,
               e.occurred_at,
               e.event_name AS title,
               COALESCE(e.payload->'interaction'->>'error_code', e.payload->'interaction'->>'result') AS detail,
               e.source_type AS channel,
               e.product_id,
               NULL::text AS severity,
               NULL::text AS sentiment,
               e.payload->'interaction'->>'result' AS status,
               e.correlation_id,
               false AS reviewed
        FROM cx_event e
        WHERE e.tenant_id = @tenantId AND e.customer_id = @customerId

        UNION ALL

        SELECT 'feedback', f.id::text, f.submitted_at, f.feedback_type,
               f.comment, f.channel, f.product_id, NULL, NULL, f.status, NULL, f.reviewed
        FROM feedback f
        WHERE f.tenant_id = @tenantId AND f.customer_id = @customerId

        UNION ALL

        SELECT 'support', s.id::text, s.opened_at, COALESCE(s.subject, s.external_id),
               s.description, s.channel, s.product_id, s.priority, NULL, s.status, s.correlation_id, false
        FROM support_case s
        WHERE s.tenant_id = @tenantId AND s.customer_id = @customerId

        UNION ALL

        SELECT 'voice', v.id::text, COALESCE(v.started_at, v.created_at), v.call_id,
               v.end_reason, 'voice', v.product_id, NULL, NULL, v.lifecycle_state, v.correlation_id, false
        FROM voice_call v
        WHERE v.tenant_id = @tenantId AND v.customer_id = @customerId

        UNION ALL

        SELECT 'insight', a.id::text, a.created_at,
               COALESCE(a.result->>'category', a.analysis_type),
               a.result->>'summary', 'ai', NULL,
               a.result->>'severity', a.result->>'sentiment',
               a.result->>'resolution_status', NULL,
               EXISTS (SELECT 1 FROM human_review hr
                       WHERE hr.analysis_id = a.id AND hr.status <> 'PENDING')
        FROM ai_analysis a
        WHERE a.tenant_id = @tenantId AND a.customer_id = @customerId AND a.status <> 'failed'
        """;

    public async Task<Page<TimelineEntry>> GetTimelineAsync(TimelineQuery query, CancellationToken ct = default)
    {
        var filters = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", query.TenantId);
        parameters.Add("customerId", query.CustomerId);

        if (query.Kinds is { Count: > 0 })
        {
            filters.Add("kind = ANY(@kinds)");
            parameters.Add("kinds", query.Kinds.ToArray());
        }

        if (!string.IsNullOrWhiteSpace(query.ProductId))
        {
            filters.Add("product_id = @productId");
            parameters.Add("productId", query.ProductId);
        }

        if (!string.IsNullOrWhiteSpace(query.Severity))
        {
            filters.Add("severity = @severity");
            parameters.Add("severity", query.Severity);
        }

        if (query.From is not null)
        {
            filters.Add("occurred_at >= @from");
            parameters.Add("from", query.From.Value.ToUniversalTime());
        }

        if (query.To is not null)
        {
            filters.Add("occurred_at <= @to");
            parameters.Add("to", query.To.Value.ToUniversalTime());
        }

        if (query.Cursor is { } cursor)
        {
            filters.Add("(occurred_at, id) < (@cursorTime, @cursorId)");
            parameters.Add("cursorTime", cursor.Timestamp);
            parameters.Add("cursorId", cursor.Tiebreaker);
        }

        var where = filters.Count > 0 ? "WHERE " + string.Join(" AND ", filters) : string.Empty;
        var take = Math.Clamp(query.Limit, 1, 200);
        parameters.Add("limit", take + 1);

        var sql = $"""
            WITH merged AS (
            {TimelineUnion}
            )
            SELECT kind, id, occurred_at, title, detail, channel, product_id, severity, sentiment,
                   status, correlation_id, reviewed
            FROM merged
            {where}
            ORDER BY occurred_at DESC, id DESC
            LIMIT @limit
            """;

        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = (await connection.QueryAsync<TimelineEntry>(
            new CommandDefinition(sql, parameters, cancellationToken: ct))).ToList();

        return Page<TimelineEntry>.From(rows, take, x => new Cursor(x.OccurredAt, x.Id).Encode());
    }

    public async Task<OverviewSnapshot> GetOverviewAsync(string tenantId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var p = new { tenantId, from = from.ToUniversalTime(), to = to.ToUniversalTime() };

        var snapshot = new OverviewSnapshot { From = p.from, To = p.to };

        var totals = await connection.QuerySingleAsync<(long Events, long Customers)>(new CommandDefinition("""
            SELECT count(*) AS events, count(DISTINCT customer_id) AS customers
            FROM cx_event WHERE tenant_id = @tenantId AND occurred_at >= @from AND occurred_at < @to
            """, p, cancellationToken: ct));
        snapshot.TotalInteractions = totals.Events;
        snapshot.ActiveCustomers = totals.Customers;

        snapshot.FeedbackCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM feedback WHERE tenant_id = @tenantId AND submitted_at >= @from AND submitted_at < @to",
            p, cancellationToken: ct));

        var nps = await connection.QuerySingleAsync<(long Promoters, long Passives, long Detractors)>(
            new CommandDefinition("""
                SELECT count(*) FILTER (WHERE nps_score >= 9),
                       count(*) FILTER (WHERE nps_score BETWEEN 7 AND 8),
                       count(*) FILTER (WHERE nps_score <= 6)
                FROM feedback
                WHERE tenant_id = @tenantId AND submitted_at >= @from AND submitted_at < @to
                  AND feedback_type IN ('nps', 'voc') AND nps_score BETWEEN 0 AND 10
                """, p, cancellationToken: ct));
        snapshot.Nps = new NpsSummary(nps.Promoters, nps.Passives, nps.Detractors);

        snapshot.SupportCases = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM support_case WHERE tenant_id = @tenantId AND opened_at >= @from AND opened_at < @to",
            p, cancellationToken: ct));

        snapshot.CallCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT count(*) FROM voice_call
            WHERE tenant_id = @tenantId AND COALESCE(started_at, created_at) >= @from
              AND COALESCE(started_at, created_at) < @to
            """, p, cancellationToken: ct));

        var sentiments = (await connection.QueryAsync<(string? Name, long Count)>(new CommandDefinition("""
            SELECT COALESCE(result->>'sentiment', 'unknown') AS name, count(*) AS count
            FROM ai_analysis
            WHERE tenant_id = @tenantId AND created_at >= @from AND created_at < @to AND status <> 'failed'
            GROUP BY 1 ORDER BY 2 DESC
            """, p, cancellationToken: ct))).ToList();

        snapshot.SentimentBreakdown = sentiments
            .Select(s => new NamedCount(s.Name ?? "unknown", s.Count)).ToList();

        var analysed = sentiments.Sum(s => s.Count);
        var negative = sentiments.Where(s => s.Name is "negative" or "mixed").Sum(s => s.Count);
        snapshot.NegativeSentimentShare = analysed == 0 ? 0 : Math.Round((double)negative / analysed, 4);

        snapshot.UnresolvedHighSeverity = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT count(*) FROM ai_analysis
            WHERE tenant_id = @tenantId AND created_at >= @from AND created_at < @to
              AND status = 'succeeded'
              AND result->>'severity' IN ('high', 'critical')
              AND result->>'resolution_status' IN ('unresolved', 'partially_resolved')
            """, p, cancellationToken: ct));

        snapshot.AiReviewBacklog = 0; // Human review has been retired; historical records are retained.

        snapshot.TopPainPoints = (await connection.QueryAsync<NamedCount>(new CommandDefinition("""
            SELECT COALESCE(result->>'category', 'unknown') AS "Name", count(*) AS "Count"
            FROM ai_analysis
            WHERE tenant_id = @tenantId AND created_at >= @from AND created_at < @to AND status <> 'failed'
              AND result->>'sentiment' IN ('negative', 'mixed')
            GROUP BY 1 ORDER BY 2 DESC LIMIT 8
            """, p, cancellationToken: ct))).ToList();

        snapshot.TopAffectedProducts = (await connection.QueryAsync<NamedCount>(new CommandDefinition("""
            SELECT product_id AS "Name", count(*) AS "Count"
            FROM cx_event
            WHERE tenant_id = @tenantId AND occurred_at >= @from AND occurred_at < @to
              AND product_id IS NOT NULL
              AND (event_name LIKE 'api.%' OR event_name LIKE '%failed%' OR event_category = 'support')
            GROUP BY 1 ORDER BY 2 DESC LIMIT 8
            """, p, cancellationToken: ct))).ToList();

        snapshot.DailyTrend = (await connection.QueryAsync<TrendPoint>(new CommandDefinition("""
            WITH days AS (
                SELECT generate_series(date_trunc('day', @from::timestamptz),
                                       date_trunc('day', @to::timestamptz), interval '1 day') AS day
            )
            SELECT d.day AS "Date",
                   (SELECT count(*) FROM cx_event e
                     WHERE e.tenant_id = @tenantId AND e.occurred_at >= d.day
                       AND e.occurred_at < d.day + interval '1 day') AS "Events",
                   (SELECT count(*) FROM ai_analysis a
                     WHERE a.tenant_id = @tenantId AND a.created_at >= d.day
                       AND a.created_at < d.day + interval '1 day'
                       AND a.result->>'sentiment' IN ('negative','mixed')) AS "Negative",
                   (SELECT count(*) FROM voice_call v
                     WHERE v.tenant_id = @tenantId AND COALESCE(v.started_at, v.created_at) >= d.day
                       AND COALESCE(v.started_at, v.created_at) < d.day + interval '1 day') AS "Calls"
            FROM days d ORDER BY d.day
            """, p, cancellationToken: ct))).ToList();

        var quality = await GetDataQualityAsync(tenantId, ct);
        snapshot.DataQualityWarnings = quality.Sum(q => q.Count);

        return snapshot;
    }

    public async Task<CustomerInsights> GetCustomerInsightsAsync(string tenantId, string customerId,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var p = new { tenantId, customerId };
        var insights = new CustomerInsights { CustomerId = customerId };

        var latest = await connection.QuerySingleOrDefaultAsync<(string? Sentiment, string? Intent, string? Stage, string? Summary)>(
            new CommandDefinition("""
                SELECT result->>'sentiment', result->>'intent', result->>'journey_stage', result->>'summary'
                FROM ai_analysis
                WHERE tenant_id = @tenantId AND customer_id = @customerId AND status <> 'failed'
                ORDER BY created_at DESC LIMIT 1
                """, p, cancellationToken: ct));

        insights.CurrentSentiment = latest.Sentiment;
        insights.DominantIntent = latest.Intent;
        insights.JourneyStage = latest.Stage;
        insights.LatestSummary = latest.Summary;

        insights.RecurringIssues = (await connection.QueryAsync<NamedCount>(new CommandDefinition("""
            SELECT COALESCE(result->>'category', 'unknown') AS "Name", count(*) AS "Count"
            FROM ai_analysis
            WHERE tenant_id = @tenantId AND customer_id = @customerId AND status <> 'failed'
            GROUP BY 1 HAVING count(*) > 0 ORDER BY 2 DESC LIMIT 5
            """, p, cancellationToken: ct))).ToList();

        insights.UnresolvedIssues = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT count(*) FROM ai_analysis
            WHERE tenant_id = @tenantId AND customer_id = @customerId AND status = 'succeeded'
              AND result->>'resolution_status' = 'unresolved'
            """, p, cancellationToken: ct));

        insights.OpenSupportCases = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT count(*) FROM support_case
            WHERE tenant_id = @tenantId AND customer_id = @customerId AND resolved_at IS NULL
            """, p, cancellationToken: ct));

        // Npgsql returns timestamptz scalar values as UTC DateTime. Dapper's scalar
        // conversion cannot cast that value directly to DateTimeOffset.
        var lastInteraction = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition("""
            SELECT max(occurred_at) FROM cx_event WHERE tenant_id = @tenantId AND customer_id = @customerId
            """, p, cancellationToken: ct));
        insights.LastInteractionAt = lastInteraction is { } timestamp
            ? new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc))
            : null;

        // Human-reviewed labels are shown separately from raw model output: a reviewed
        // label is a stronger statement than a prediction (panel proposal 5).
        insights.HumanReviewedLabels = (await connection.QueryAsync<NamedCount>(new CommandDefinition("""
            SELECT COALESCE(hr.override_result->>'category', a.result->>'category', 'unknown') AS "Name",
                   count(*) AS "Count"
            FROM human_review hr
            JOIN ai_analysis a ON a.id = hr.analysis_id
            WHERE hr.tenant_id = @tenantId AND a.customer_id = @customerId
              AND hr.status IN ('APPROVED', 'OVERRIDDEN')
            GROUP BY 1 ORDER BY 2 DESC LIMIT 5
            """, p, cancellationToken: ct))).ToList();

        return insights;
    }

    public async Task<IReadOnlyList<DataQualityCheck>> GetDataQualityAsync(string tenantId,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var checks = new List<DataQualityCheck>();

        async Task<DataQualityCheck> Check(string key, string title, string severity, string owner, string countSql,
            string sampleSql)
        {
            var count = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(countSql, new { tenantId }, cancellationToken: ct));
            var sample = count == 0
                ? Array.Empty<string>()
                : (await connection.QueryAsync<string>(
                    new CommandDefinition(sampleSql, new { tenantId }, cancellationToken: ct))).ToArray();
            return new DataQualityCheck(key, title, count, severity, owner, sample);
        }

        checks.Add(await Check("missing_customer_id", "Events without client ID", "medium", "cx-data",
            "SELECT count(*) FROM cx_event WHERE tenant_id = @tenantId AND customer_id IS NULL AND occurred_at > now() - interval '7 days'",
            "SELECT event_id FROM cx_event WHERE tenant_id = @tenantId AND customer_id IS NULL AND occurred_at > now() - interval '7 days' ORDER BY occurred_at DESC LIMIT 5"));

        checks.Add(await Check("unknown_product", "Event with unknown product", "low", "cx-data",
            "SELECT count(*) FROM cx_event WHERE tenant_id = @tenantId AND product_id IS NULL AND event_category = 'product_interaction' AND occurred_at > now() - interval '7 days'",
            "SELECT event_id FROM cx_event WHERE tenant_id = @tenantId AND product_id IS NULL AND event_category = 'product_interaction' ORDER BY occurred_at DESC LIMIT 5"));

        checks.Add(await Check("old_schema_version", "Edition schema Old", "medium", "platform",
            "SELECT count(*) FROM cx_event WHERE tenant_id = @tenantId AND schema_version <> '1.0' AND occurred_at > now() - interval '7 days'",
            "SELECT DISTINCT schema_version FROM cx_event WHERE tenant_id = @tenantId AND schema_version <> '1.0' LIMIT 5"));

        checks.Add(await Check("missing_recording", "Ended call without audio file", "high", "voice-ops",
            """
            SELECT count(*) FROM voice_call v
            WHERE v.tenant_id = @tenantId AND v.ended_at IS NOT NULL
              AND v.ended_at < now() - interval '1 hour'
              AND NOT EXISTS (SELECT 1 FROM voice_recording r WHERE r.call_id = v.id)
            """,
            """
            SELECT v.call_id FROM voice_call v
            WHERE v.tenant_id = @tenantId AND v.ended_at IS NOT NULL
              AND v.ended_at < now() - interval '1 hour'
              AND NOT EXISTS (SELECT 1 FROM voice_recording r WHERE r.call_id = v.id)
            ORDER BY v.ended_at DESC LIMIT 5
            """));

        checks.Add(await Check("recording_not_transcribed", "Audio file without transcript", "high", "voice-ops",
            """
            SELECT count(*) FROM voice_recording r
            WHERE r.tenant_id = @tenantId AND r.storage_state = 'READY'
              AND r.created_at < now() - interval '30 minutes'
              AND NOT EXISTS (SELECT 1 FROM voice_transcript t WHERE t.recording_id = r.id AND t.status = 'SUCCEEDED')
            """,
            """
            SELECT r.object_key FROM voice_recording r
            WHERE r.tenant_id = @tenantId AND r.storage_state = 'READY'
              AND NOT EXISTS (SELECT 1 FROM voice_transcript t WHERE t.recording_id = r.id AND t.status = 'SUCCEEDED')
            ORDER BY r.created_at DESC LIMIT 5
            """));

        checks.Add(await Check("low_confidence_transcript", "transcript With low confidence", "medium", "voice-ops",
            "SELECT count(*) FROM voice_transcript WHERE tenant_id = @tenantId AND status = 'SUCCEEDED' AND average_confidence < 0.6",
            "SELECT id::text FROM voice_transcript WHERE tenant_id = @tenantId AND status = 'SUCCEEDED' AND average_confidence < 0.6 ORDER BY created_at DESC LIMIT 5"));

        checks.Add(await Check("orphan_support_case", "Support ticket without customer", "medium", "support",
            "SELECT count(*) FROM support_case WHERE tenant_id = @tenantId AND customer_id IS NULL",
            "SELECT external_id FROM support_case WHERE tenant_id = @tenantId AND customer_id IS NULL ORDER BY opened_at DESC LIMIT 5"));

        checks.Add(await Check("invalid_timestamp", "Invalid event time", "high", "platform",
            "SELECT count(*) FROM cx_event WHERE tenant_id = @tenantId AND (occurred_at > received_at + interval '5 minutes' OR occurred_at < now() - interval '365 days')",
            "SELECT event_id FROM cx_event WHERE tenant_id = @tenantId AND occurred_at > received_at + interval '5 minutes' ORDER BY received_at DESC LIMIT 5"));

        checks.Add(await Check("integration_lag", "Integration delay", "high", "platform",
            "SELECT count(*) FROM integration_checkpoint WHERE tenant_id = @tenantId AND (status = 'error' OR last_success_at < now() - interval '1 hour')",
            "SELECT integration_name FROM integration_checkpoint WHERE tenant_id = @tenantId AND (status = 'error' OR last_success_at < now() - interval '1 hour') LIMIT 5"));

        checks.Add(await Check("outbox_backlog", "queue outbox Unpublished", "critical", "platform",
            "SELECT count(*) FROM cx_outbox WHERE published_at IS NULL AND created_at < now() - interval '5 minutes'",
            "SELECT aggregate_id FROM cx_outbox WHERE published_at IS NULL ORDER BY created_at LIMIT 5"));

        return checks.Where(c => c.Count > 0).Concat(checks.Where(c => c.Count == 0)).ToList();
    }

    public async Task<PipelineHealthSnapshot> GetPipelineHealthAsync(string tenantId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var p = new { tenantId };
        var snapshot = new PipelineHealthSnapshot();

        snapshot.OutboxBacklog = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM cx_outbox WHERE published_at IS NULL", cancellationToken: ct));

        var oldest = await connection.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
            "SELECT min(created_at) FROM cx_outbox WHERE published_at IS NULL", cancellationToken: ct));
        snapshot.OutboxLagSeconds = oldest is null ? null : (DateTimeOffset.UtcNow - oldest.Value).TotalSeconds;

        snapshot.EventsLastHour = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM cx_event WHERE tenant_id = @tenantId AND received_at > now() - interval '1 hour'",
            p, cancellationToken: ct));

        snapshot.EventsLast24H = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM cx_event WHERE tenant_id = @tenantId AND received_at > now() - interval '24 hours'",
            p, cancellationToken: ct));

        snapshot.ReviewBacklog = 0; // Human review has been retired; historical records are retained.

        snapshot.JobsByState = (await connection.QueryAsync<(string State, long Count)>(new CommandDefinition(
            "SELECT state, count(*) FROM processing_job WHERE tenant_id = @tenantId GROUP BY state",
            p, cancellationToken: ct))).ToDictionary(r => r.State, r => r.Count);

        snapshot.RecordingsByState = (await connection.QueryAsync<(string State, long Count)>(new CommandDefinition(
            "SELECT storage_state, count(*) FROM voice_recording WHERE tenant_id = @tenantId GROUP BY storage_state",
            p, cancellationToken: ct))).ToDictionary(r => r.State, r => r.Count);

        snapshot.FailedJobs = (await connection.QueryAsync<FailedJobSummary>(new CommandDefinition("""
            SELECT job_key AS "JobKey", job_type AS "JobType", state AS "State", attempts AS "Attempts",
                   error_category AS "ErrorCategory", last_error AS "LastError", updated_at AS "UpdatedAt"
            FROM processing_job
            WHERE tenant_id = @tenantId AND state IN ('FAILED_RETRYABLE','FAILED_FINAL')
            ORDER BY updated_at DESC LIMIT 20
            """, p, cancellationToken: ct))).ToList();

        snapshot.AiAnalysesLast24H = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM ai_analysis WHERE tenant_id = @tenantId AND created_at > now() - interval '24 hours'",
            p, cancellationToken: ct));

        snapshot.TranscriptsLast24H = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM voice_transcript WHERE tenant_id = @tenantId AND created_at > now() - interval '24 hours'",
            p, cancellationToken: ct));

        return snapshot;
    }

    public async Task<IReadOnlyList<GlobalSearchHit>> GlobalSearchAsync(string tenantId, string term, int limit,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term)) return Array.Empty<GlobalSearchHit>();

        await using var connection = await _dataSource.OpenAsync(ct);
        var p = new { tenantId, exact = term.Trim(), like = $"%{term.Trim()}%", limit = Math.Clamp(limit, 1, 100) };

        // OpenSearch is the right home for heavy full-text (panel proposal 15); this covers
        // identifier lookup and short text search directly against the source of truth.
        var rows = await connection.QueryAsync<GlobalSearchHit>(new CommandDefinition("""
            (SELECT 'customer' AS "Kind", customer_id AS "Id", customer_id AS "Label",
                    business_id AS "Detail", customer_id AS "CustomerId", last_seen_at AS "OccurredAt"
             FROM customer_identity
             WHERE tenant_id = @tenantId AND (customer_id ILIKE @like OR business_id ILIKE @like)
             LIMIT @limit)
            UNION ALL
            (SELECT 'event', event_id, event_name, correlation_id, customer_id, occurred_at
             FROM cx_event
             WHERE tenant_id = @tenantId AND (event_id = @exact OR correlation_id = @exact OR trace_id = @exact)
             LIMIT @limit)
            UNION ALL
            (SELECT 'support', id::text, COALESCE(subject, external_id), status, customer_id, opened_at
             FROM support_case
             WHERE tenant_id = @tenantId AND (external_id ILIKE @like OR subject ILIKE @like OR description ILIKE @like)
             LIMIT @limit)
            UNION ALL
            (SELECT 'feedback', id::text, feedback_type, comment, customer_id, submitted_at
             FROM feedback
             WHERE tenant_id = @tenantId AND comment ILIKE @like
             LIMIT @limit)
            UNION ALL
            (SELECT 'voice', id::text, call_id, end_reason, customer_id, COALESCE(started_at, created_at)
             FROM voice_call
             WHERE tenant_id = @tenantId AND (call_id ILIKE @like OR correlation_id ILIKE @like)
             LIMIT @limit)
            UNION ALL
            (SELECT 'transcript', t.id::text, r.object_key, left(t.transcript_text, 200), c.customer_id, t.created_at
             FROM voice_transcript t
             JOIN voice_recording r ON r.id = t.recording_id
             JOIN voice_call c ON c.id = r.call_id
             WHERE t.tenant_id = @tenantId AND t.transcript_text ILIKE @like
             LIMIT @limit)
            ORDER BY "OccurredAt" DESC NULLS LAST
            LIMIT @limit
            """, p, cancellationToken: ct));

        return rows.ToList();
    }
}
