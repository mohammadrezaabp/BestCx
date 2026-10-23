using Cx.Application.Operations;
using Dapper;

namespace Cx.Infrastructure.Operations;

public sealed partial class OperationsStore
{
    private const string FeedbackFacts = """
        SELECT f.*,COALESCE(NULLIF(r.value->>'subcategory',''),NULLIF(NULLIF(r.value->>'category',''),'unclassified'),f.selected_topic) AS topic,
          COALESCE(r.value->>'severity','medium') AS severity,r.value->>'sentiment' AS sentiment,a.id IS NOT NULL AS has_analysis
        FROM feedback f
        LEFT JOIN LATERAL(SELECT a.id,a.result FROM ai_analysis a WHERE a.tenant_id=f.tenant_id
          AND a.entity_type=CASE WHEN f.call_id IS NULL THEN 'cx_event' ELSE 'voice_call' END
          AND a.entity_id=COALESCE(f.call_id,f.event_id) ORDER BY a.created_at DESC LIMIT 1) a ON true
        LEFT JOIN human_review h ON h.analysis_id=a.id
        CROSS JOIN LATERAL(SELECT COALESCE(CASE WHEN h.status='OVERRIDDEN' THEN h.override_result END,a.result) AS value) r
        WHERE f.tenant_id=@tenant
        """ + " ";

    public async Task<IReadOnlyList<CommonProblem>> ProblemsAsync(string tenant, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        return (await db.QueryAsync<CommonProblem>(new CommandDefinition("""
            WITH RECURSIVE branch AS (
              SELECT code AS root,id,code FROM topic WHERE tenant_id=@tenant
              UNION ALL SELECT b.root,t.id,t.code FROM topic t JOIN branch b ON t.parent_id=b.id WHERE t.tenant_id=@tenant
            ), facts AS (
            """ + FeedbackFacts + """
            ), problems AS (SELECT p.*,CASE WHEN fixed_at IS NULL THEN 0
              ELSE greatest(0,least(14,floor(extract(epoch FROM (@now-fixed_at))/86400)::int)) END AS comparison_days
              FROM cx_problem p WHERE tenant_id=@tenant)
            SELECT p.*,
              (SELECT count(*) FROM cx_case w WHERE w.tenant_id=@tenant AND EXISTS(SELECT 1 FROM cx_case_feedback scope_link JOIN feedback scope_feedback ON scope_feedback.tenant_id=scope_link.tenant_id AND scope_feedback.id=scope_link.feedback_id WHERE scope_link.tenant_id=w.tenant_id AND scope_link.case_id=w.id AND scope_feedback.feedback_type='complaint') AND w.problem_id=p.id) AS case_count,
              (SELECT count(DISTINCT w.customer_id) FROM cx_case w WHERE w.tenant_id=@tenant AND EXISTS(SELECT 1 FROM cx_case_feedback scope_link JOIN feedback scope_feedback ON scope_feedback.tenant_id=scope_link.tenant_id AND scope_feedback.id=scope_link.feedback_id WHERE scope_link.tenant_id=w.tenant_id AND scope_link.case_id=w.id AND scope_feedback.feedback_type='complaint') AND w.problem_id=p.id) AS affected_customers,
              (SELECT count(*) FROM facts f WHERE f.feedback_type='complaint' AND p.comparison_days>0
                AND (f.topic IN (SELECT code FROM branch WHERE root=p.topic) OR (p.topic IS NULL AND f.id IN
                  (SELECT x.feedback_id FROM cx_case_feedback x JOIN cx_case w ON w.id=x.case_id AND w.tenant_id=x.tenant_id WHERE w.tenant_id=@tenant AND EXISTS(SELECT 1 FROM cx_case_feedback scope_link JOIN feedback scope_feedback ON scope_feedback.tenant_id=scope_link.tenant_id AND scope_feedback.id=scope_link.feedback_id WHERE scope_link.tenant_id=w.tenant_id AND scope_link.case_id=w.id AND scope_feedback.feedback_type='complaint') AND w.problem_id=p.id)))
                AND f.submitted_at>=p.fixed_at-p.comparison_days*interval '1 day' AND f.submitted_at<p.fixed_at) AS before_count,
              (SELECT count(*) FROM facts f WHERE f.feedback_type='complaint' AND p.comparison_days>0
                AND (f.topic IN (SELECT code FROM branch WHERE root=p.topic) OR (p.topic IS NULL AND f.id IN
                  (SELECT x.feedback_id FROM cx_case_feedback x JOIN cx_case w ON w.id=x.case_id AND w.tenant_id=x.tenant_id WHERE w.tenant_id=@tenant AND EXISTS(SELECT 1 FROM cx_case_feedback scope_link JOIN feedback scope_feedback ON scope_feedback.tenant_id=scope_link.tenant_id AND scope_feedback.id=scope_link.feedback_id WHERE scope_link.tenant_id=w.tenant_id AND scope_link.case_id=w.id AND scope_feedback.feedback_type='complaint') AND w.problem_id=p.id)))
                AND f.submitted_at>=p.fixed_at AND f.submitted_at<p.fixed_at+p.comparison_days*interval '1 day') AS after_count
            FROM problems p ORDER BY p.created_at DESC LIMIT 200
            """, new { tenant, now = Now }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<FollowUpCustomer>> FollowUpAsync(string tenant, string? segment, int minScore, CancellationToken ct)
    {
        Text(segment, 128, "Customer group", false);
        minScore = Math.Clamp(minScore, 1, 100);
        await using var db = await dataSource.OpenAsync(ct);
        var rows = (await db.QueryAsync<FollowUpCustomer>(new CommandDefinition("""
            WITH facts AS (
            """ + FeedbackFacts + """
              AND f.feedback_type='complaint' AND f.submitted_at>=@now-interval '60 days'
            ), customers AS (
              SELECT c.customer_id,COALESCE(c.display_name,c.customer_id) AS display_name,c.segment,
                (SELECT count(*) FROM facts f WHERE f.customer_id=c.customer_id AND f.feedback_type='complaint' AND f.submitted_at>=@now-interval '30 days') AS complaints,
                (SELECT count(*) FROM cx_case w WHERE w.tenant_id=@tenant AND EXISTS(SELECT 1 FROM cx_case_feedback scope_link JOIN feedback scope_feedback ON scope_feedback.tenant_id=scope_link.tenant_id AND scope_feedback.id=scope_link.feedback_id WHERE scope_link.tenant_id=w.tenant_id AND scope_link.case_id=w.id AND scope_feedback.feedback_type='complaint') AND w.customer_id=c.customer_id AND w.status NOT IN ('resolved','closed')) AS open_cases,
                (SELECT count(*) FROM cx_case w WHERE w.tenant_id=@tenant AND EXISTS(SELECT 1 FROM cx_case_feedback scope_link JOIN feedback scope_feedback ON scope_feedback.tenant_id=scope_link.tenant_id AND scope_feedback.id=scope_link.feedback_id WHERE scope_link.tenant_id=w.tenant_id AND scope_link.case_id=w.id AND scope_feedback.feedback_type='complaint') AND w.customer_id=c.customer_id AND w.status NOT IN ('resolved','closed')
                  AND ((w.first_response_at IS NULL AND w.response_due_at<@now) OR (w.status<>'waiting_customer' AND w.resolution_due_at<@now))) AS overdue_cases,
                NULL::numeric AS recent_csat, NULL::numeric AS previous_csat, NULL::numeric AS recent_ces, 0::bigint AS negative_feedback
              FROM customer_identity c WHERE c.tenant_id=@tenant AND c.merged_into_id IS NULL AND (@segment IS NULL OR c.segment=@segment)
            ), scored AS (
              SELECT *,least(100,least(32,complaints*8)+least(28,open_cases*7)+least(40,overdue_cases*20)) AS follow_up_score
              FROM customers
            ) SELECT * FROM scored WHERE follow_up_score>=@minScore ORDER BY follow_up_score DESC,customer_id LIMIT 200
            """, new { tenant, segment, minScore, now = Now }, cancellationToken: ct))).AsList();
        foreach (var row in rows) FollowUpPriority.Apply(row);
        return rows;
    }

    public async Task<OperationsReport> OutcomesAsync(string tenant, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        const string raw = """
            WITH cohort AS (
              SELECT w.*,COALESCE(t.name,'No team') AS team_name,COALESCE(w.topic,'No issue') AS topic_name
              FROM cx_case w LEFT JOIN cx_team t ON t.tenant_id=w.tenant_id AND t.id=w.team_id
              WHERE w.tenant_id=@tenant AND EXISTS(SELECT 1 FROM cx_case_feedback scope_link JOIN feedback scope_feedback ON scope_feedback.tenant_id=scope_link.tenant_id AND scope_feedback.id=scope_link.feedback_id WHERE scope_link.tenant_id=w.tenant_id AND scope_link.case_id=w.id AND scope_feedback.feedback_type='complaint') AND w.created_at>=@from AND w.created_at<@to
            )
            """;
        const string metrics = """
            count(*) AS total,count(*) FILTER(WHERE status IN ('resolved','closed')) AS resolved,
            count(*) FILTER(WHERE status NOT IN ('resolved','closed') AND
              ((first_response_at IS NULL AND response_due_at<@now) OR (status<>'waiting_customer' AND resolution_due_at<@now))) AS overdue,
            count(*) FILTER(WHERE reopen_count>0) AS reopened,count(first_response_at) AS response_measured,count(first_resolved_at) AS resolution_measured,
            count(*) FILTER(WHERE response_within_sla) AS response_within_sla,count(*) FILTER(WHERE resolution_within_sla) AS resolution_within_sla,
            avg(response_business_minutes) AS average_response_minutes,avg(resolution_business_minutes) AS average_resolution_minutes
            """;
        var args = new { tenant, from = from.ToUniversalTime(), to = to.ToUniversalTime(), now = Now };
        var summary = await db.QuerySingleAsync<ServiceOutcome>(new CommandDefinition(raw + "SELECT 'All' AS name," + metrics + " FROM cohort", args, cancellationToken: ct));
        var teams = (await db.QueryAsync<ServiceOutcome>(new CommandDefinition(raw + "SELECT team_name AS name," + metrics + " FROM cohort GROUP BY team_name ORDER BY total DESC", args, cancellationToken: ct))).AsList();
        var topics = (await db.QueryAsync<ServiceOutcome>(new CommandDefinition(raw + "SELECT topic_name AS name," + metrics + " FROM cohort GROUP BY topic_name ORDER BY total DESC", args, cancellationToken: ct))).AsList();
        return new(summary, teams, topics);
    }

    public async Task<JourneyResult> FunnelAsync(string tenant, Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        var journey = await db.QuerySingleOrDefaultAsync<JourneyDefinition>(new CommandDefinition(
            "SELECT * FROM cx_journey WHERE tenant_id=@tenant AND id=@id", new { tenant, id }, cancellationToken: ct)) ?? throw new OperationsException("Trip not found.", 404);
        var rows = await db.QueryAsync<FunnelRow>(new CommandDefinition("""
            WITH RECURSIVE stages AS (SELECT * FROM cx_journey_stage WHERE tenant_id=@tenant AND journey_id=@id),
            starts AS (
              SELECT DISTINCT ON(e.customer_id) e.customer_id,e.occurred_at,e.id FROM cx_event e JOIN stages s ON s.position=0 AND e.event_name=ANY(s.event_names)
              WHERE e.tenant_id=@tenant AND e.customer_id IS NOT NULL AND e.occurred_at>=@from AND e.occurred_at<@to AND e.occurred_at<=@now
              ORDER BY e.customer_id,e.occurred_at,e.id
            ), walk AS (
              SELECT customer_id,0 AS position,occurred_at AS reached_at,id AS event_id,occurred_at AS started_at FROM starts
              UNION ALL
              SELECT w.customer_id,s.position,n.occurred_at,n.id,w.started_at FROM walk w JOIN stages s ON s.position=w.position+1
              JOIN LATERAL(SELECT e.id,e.occurred_at FROM cx_event e WHERE e.tenant_id=@tenant AND e.customer_id=w.customer_id
                AND e.event_name=ANY(s.event_names) AND (e.occurred_at,e.id)>(w.reached_at,w.event_id)
                AND e.occurred_at<=w.started_at+@days*interval '1 day' AND e.occurred_at<=@now ORDER BY e.occurred_at,e.id LIMIT 1) n ON true
            )
            SELECT s.title,s.position,count(DISTINCT w.customer_id) AS reached,count(DISTINCT n.customer_id) AS completed_next,
              count(DISTINCT w.customer_id) FILTER(WHERE n.customer_id IS NULL AND w.started_at+@days*interval '1 day'>@now
                AND s.position<(SELECT max(position) FROM stages)) AS pending,
              count(DISTINCT w.customer_id) FILTER(WHERE n.customer_id IS NULL AND w.started_at+@days*interval '1 day'<=@now
                AND s.position<(SELECT max(position) FROM stages)) AS dropped,
              round(100.0*count(f.id) FILTER(WHERE f.csat_score BETWEEN 4 AND 5)/NULLIF(count(f.id) FILTER(WHERE f.csat_score BETWEEN 1 AND 5),0),2) AS csat,
              0::bigint AS complaints
            FROM stages s LEFT JOIN walk w ON w.position=s.position
            LEFT JOIN walk n ON n.customer_id=w.customer_id AND n.position=w.position+1
            LEFT JOIN feedback f ON f.tenant_id=@tenant AND f.feedback_type<>'complaint' AND f.customer_id=w.customer_id AND f.submitted_at>=w.reached_at
              AND f.submitted_at<COALESCE(n.reached_at,least(w.started_at+@days*interval '1 day',@now))
            GROUP BY s.title,s.position ORDER BY s.position
            """, new { tenant, id, from = from.ToUniversalTime(), to = to.ToUniversalTime(), days = journey.WindowDays, now = Now }, cancellationToken: ct));
        return new(id, journey.Name, journey.WindowDays, rows.Select(row => new JourneyStageResult(row.Title, row.Reached, row.CompletedNext, row.Pending, row.Dropped, row.Csat, row.Complaints)).ToList());
    }
    private sealed class FunnelRow
    {
        public string Title { get; set; } = "";
        public long Reached { get; set; }
        public long CompletedNext { get; set; }
        public long Pending { get; set; }
        public long Dropped { get; set; }
        public decimal? Csat { get; set; }
        public long Complaints { get; set; }
    }
}
