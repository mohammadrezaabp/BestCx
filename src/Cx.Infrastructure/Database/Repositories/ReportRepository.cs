using System.Text.Json;
using Cx.Application.Query;
using Cx.Contracts;
using Dapper;

namespace Cx.Infrastructure.Database.Repositories;

/// <summary>All charts share one materialized, tenant-scoped population and one database snapshot.</summary>
public sealed class ReportRepository(CxDataSource dataSource) : IReportRepository
{
    private const string Metrics = """
        count(id) AS total,
        count(DISTINCT customer_id) AS customers,
        count(id) FILTER (WHERE analyzed) AS analyzed,
        count(id) FILTER (WHERE sentiment IN ('negative', 'mixed')) AS negative,
        count(id) FILTER (WHERE feedback_type = 'complaint') AS complaints,
        count(id) FILTER (WHERE severity IN ('high', 'critical') AND status NOT IN ('resolved', 'closed')) AS open_high_severity,
        count(id) FILTER (WHERE status NOT IN ('resolved', 'closed')) AS open,
        count(id) FILTER (WHERE status IN ('resolved', 'closed')) AS resolved,
        count(id) FILTER (WHERE overdue) AS overdue,
        count(csat) AS csat_responses,
        count(csat) FILTER (WHERE csat >= 4) AS csat_satisfied,
        round(100.0 * count(csat) FILTER (WHERE csat >= 4) / NULLIF(count(csat), 0), 2) AS csat_percent,
        count(ces) AS ces_responses,
        round(avg(ces), 2) AS ces_average,
        round(100.0 * count(ces) FILTER (WHERE ces >= 5) / NULLIF(count(ces), 0), 2) AS ces_easy_percent,
        count(nps) AS nps_responses,
        round(100.0 * (count(nps) FILTER (WHERE nps >= 9) - count(nps) FILTER (WHERE nps <= 6))
              / NULLIF(count(nps), 0), 2) AS nps_score
        """;

    private const string Base = """
        WITH RECURSIVE topic_branch AS (
            SELECT id, code FROM topic WHERE tenant_id = @tenantId AND code = ANY(@topics)
            UNION
            SELECT t.id, t.code FROM topic t JOIN topic_branch p ON t.parent_id = p.id
            WHERE t.tenant_id = @tenantId
        ), raw AS MATERIALIZED (
            SELECT f.id, f.customer_id, f.feedback_type,
                   CASE WHEN @inbox = 'complaints' THEN COALESCE(w.status, f.status) ELSE f.status END AS status, f.comment,
                   COALESCE(w.status NOT IN ('resolved', 'closed') AND
                     ((w.first_response_at IS NULL AND w.response_due_at < now()) OR
                      (w.status <> 'waiting_customer' AND w.resolution_due_at < now())), false) AS overdue,
                   COALESCE(f.channel, 'unknown') AS channel,
                   COALESCE(NULLIF(r.value->>'subcategory', ''), NULLIF(NULLIF(r.value->>'category', 'unclassified'), ''),
                            f.selected_topic, 'unclassified') AS topic,
                   r.value->>'category' AS category,
                   COALESCE(r.value->>'sentiment', 'unknown') AS sentiment,
                   CASE WHEN @inbox='complaints' THEN COALESCE(w.priority, r.value->>'severity', 'unknown') ELSE COALESCE(r.value->>'severity', 'unknown') END AS severity,
                   a.id IS NOT NULL AS analyzed,
                   CASE WHEN @inbox='feedback' AND f.csat_score BETWEEN 1 AND 5 THEN f.csat_score
                        WHEN @inbox='feedback' AND f.csat_score IS NULL AND f.provider = 'cx-customer-portal' AND f.feedback_type IN ('voc', 'generic', 'voice')
                             AND f.rating BETWEEN 1 AND 5 THEN f.rating END AS csat,
                   CASE WHEN @inbox='feedback' AND f.ces_score BETWEEN 1 AND 7 THEN f.ces_score END AS ces,
                   CASE WHEN @inbox='feedback' AND f.feedback_type IN ('nps', 'voc') AND f.nps_score BETWEEN 0 AND 10 THEN f.nps_score END AS nps,
                   date_trunc(@bucket, f.submitted_at AT TIME ZONE @timeZone)::date AS day
            FROM feedback f
            LEFT JOIN cx_case_feedback cf ON cf.tenant_id=f.tenant_id AND cf.feedback_id=f.id AND @inbox='complaints'
            LEFT JOIN cx_case w ON w.tenant_id=cf.tenant_id AND w.id=cf.case_id
            LEFT JOIN LATERAL (
                SELECT x.id, x.result FROM ai_analysis x
                WHERE x.tenant_id = f.tenant_id AND x.status IN ('succeeded', 'superseded')
                  AND x.entity_type = CASE WHEN f.call_id IS NULL THEN 'cx_event' ELSE 'voice_call' END
                  AND x.entity_id = COALESCE(f.call_id, f.event_id)
                ORDER BY x.created_at DESC, x.id DESC LIMIT 1
            ) a ON true
            LEFT JOIN human_review hr ON hr.analysis_id = a.id AND hr.tenant_id = f.tenant_id
            CROSS JOIN LATERAL (SELECT COALESCE(
                CASE WHEN hr.status = 'OVERRIDDEN' THEN hr.override_result END, a.result) AS value) r
            WHERE f.tenant_id = @tenantId AND f.submitted_at >= @from AND f.submitted_at < @to
              AND ((@inbox = 'complaints' AND f.feedback_type = 'complaint') OR
                   (@inbox = 'feedback' AND f.feedback_type <> 'complaint'))
              AND (@customerId IS NULL OR f.customer_id = @customerId)
        ), filtered AS MATERIALIZED (
            SELECT * FROM raw WHERE
              (cardinality(@topics) = 0 OR topic = ANY(@topics) OR (category <> 'unclassified' AND category = ANY(@topics))
               OR topic IN (SELECT code FROM topic_branch))
              AND (cardinality(@sentiments) = 0 OR sentiment = ANY(@sentiments))
              AND (cardinality(@channels) = 0 OR channel = ANY(@channels))
              AND (cardinality(@severities) = 0 OR severity = ANY(@severities))
              AND (cardinality(@types) = 0 OR feedback_type = ANY(@types))
              AND (cardinality(@statuses) = 0 OR status = ANY(@statuses))
              AND (@search IS NULL OR comment ILIKE @search)
        )
        """;

    public async Task<ReportSnapshot> GetAsync(ReportQuery query, CancellationToken ct = default)
    {
        // Column names are internal constants; all user input stays in SQL parameters.
        string Groups(string column) => $"""
            COALESCE((SELECT jsonb_agg(to_jsonb(g) ORDER BY g.total DESC, g.name)
                FROM (SELECT {column} AS name, {Metrics} FROM filtered GROUP BY {column}) g), '[]'::jsonb)
            """;
        string Distribution(string column, int max) => $"""
            (SELECT jsonb_agg(to_jsonb(g) ORDER BY g.name::int)
             FROM (SELECT score::text AS name, count(f.{column}) AS count
                   FROM generate_series(1, {max}) score LEFT JOIN filtered f ON f.{column} = score
                   GROUP BY score) g)
            """;

        var sql = $"""
            {Base}
            SELECT jsonb_build_object(
                'summary', (SELECT to_jsonb(s) FROM (SELECT {Metrics} FROM filtered) s),
                'csat_distribution', {Distribution("csat", 5)},
                'ces_distribution', {Distribution("ces", 7)},
                'topics', {Groups("topic")},
                'sentiments', {Groups("sentiment")},
                'channels', {Groups("channel")},
                'severities', {Groups("severity")},
                'types', {Groups("feedback_type")},
                'statuses', {Groups("status")},
                'trend', (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t.name), '[]'::jsonb)
                    FROM (SELECT to_char(d.day, 'YYYY-MM-DD') AS name, {Metrics}
                          FROM generate_series(
                              date_trunc(@bucket, @from AT TIME ZONE @timeZone),
                              date_trunc(@bucket, (@to - interval '1 microsecond') AT TIME ZONE @timeZone),
                              CASE @bucket WHEN 'week' THEN interval '1 week'
                                           WHEN 'month' THEN interval '1 month' ELSE interval '1 day' END
                          ) AS d(day)
                          LEFT JOIN filtered f ON f.day = d.day::date GROUP BY d.day) t),
                'available_channels', (SELECT COALESCE(jsonb_agg(c.channel ORDER BY c.channel), '[]'::jsonb)
                    FROM (SELECT DISTINCT channel FROM raw) c),
                'available_topics', (SELECT COALESCE(jsonb_agg(t.topic ORDER BY t.topic), '[]'::jsonb)
                    FROM (SELECT DISTINCT topic FROM raw UNION SELECT code FROM topic WHERE tenant_id = @tenantId) t)
            )::text
            """;
        await using var connection = await dataSource.OpenAsync(ct);
        var json = await connection.QuerySingleAsync<string>(new CommandDefinition(sql, new
        {
            query.TenantId, query.Inbox, From = query.From.ToUniversalTime(), To = query.To.ToUniversalTime(),
            query.Topics, query.Sentiments, query.Channels, query.Severities, query.Types, query.Statuses,
            Search = string.IsNullOrWhiteSpace(query.Search) ? null : $"%{query.Search.Trim()}%",
            query.Bucket, query.TimeZone, query.CustomerId
        }, cancellationToken: ct));
        var report = JsonSerializer.Deserialize<ReportSnapshot>(json, CxJson.Options)!;
        report.From = query.From;
        report.To = query.To;
        return report;
    }
}
