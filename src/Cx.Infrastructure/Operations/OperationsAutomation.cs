using Cx.Application.Operations;
using Dapper;
using Npgsql;

namespace Cx.Infrastructure.Operations;

public sealed partial class OperationsStore
{
    // A session advisory lock prevents two application replicas from processing the
    // same batch. Durable execution keys and transactional notifications survive retries.
    public async Task TickAsync(CancellationToken ct, string? onlyTenant = null)
    {
        await using var gate = await dataSource.OpenAsync(ct);
        const long key = 8421337007;
        if (!await gate.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT pg_try_advisory_lock(@key)", new { key }, cancellationToken: ct))) return;
        try
        {
            var tenants = await gate.QueryAsync<string>(new CommandDefinition("SELECT DISTINCT tenant_id FROM customer_identity WHERE (@onlyTenant IS NULL OR tenant_id=@onlyTenant) UNION SELECT DISTINCT tenant_id FROM app_user WHERE (@onlyTenant IS NULL OR tenant_id=@onlyTenant)", new { onlyTenant }, cancellationToken: ct));
            foreach (var tenant in tenants)
            {
                await EnsureDefaultsAsync(tenant, ct);
                await CaptureComplaintsAsync(tenant, ct);
                await ExecuteRulesAsync(tenant, ct);
                await SlaAlertsAsync(tenant, ct);
                await EventInvitationsAsync(tenant, ct);
                await RemindersAsync(tenant, ct);
            }
        }
        finally { await gate.ExecuteAsync("SELECT pg_advisory_unlock(@key)", new { key }); }
    }
    private async Task CaptureComplaintsAsync(string tenant, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        var complaints = await db.QueryAsync<FeedbackFact>(new CommandDefinition(FeedbackFacts + """
            AND f.feedback_type='complaint' AND f.status<>'closed' AND f.customer_id IS NOT NULL
            AND EXISTS(SELECT 1 FROM customer_identity c WHERE c.tenant_id=@tenant AND c.customer_id=f.customer_id AND c.merged_into_id IS NULL)
            AND NOT EXISTS(SELECT 1 FROM cx_case_feedback x WHERE x.tenant_id=@tenant AND x.feedback_id=f.id)
            ORDER BY f.created_at LIMIT 100
            """, new { tenant }, cancellationToken: ct));
        foreach (var feedback in complaints)
        {
            var title = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT title FROM topic WHERE tenant_id=@tenant AND code=@topic", new { tenant, feedback.Topic }, cancellationToken: ct));
            var caseTitle = title is null ? "Dealing with customer complaints" : $"Complaint processing {title}";
            try
            {
                await CreateAsync(tenant, "automation", new(StableId($"feedback-case:{tenant}:{feedback.Id}"), feedback.CustomerId,
                    caseTitle[..Math.Min(256, caseTitle.Length)], string.IsNullOrWhiteSpace(feedback.Comment) ? "Registered customer complaint" : feedback.Comment,
                    feedback.Topic, Priorities.Contains(feedback.Severity) ? feedback.Severity : "medium", FeedbackId: feedback.Id), ct);
            }
            catch (OperationsException ex)
            {
                await using var tx = await db.BeginTransactionAsync(ct);
                await NotifyLeadsAsync(db, tx, tenant, "Failed to automatically register the file", ex.Message, $"/complaints?id={feedback.Id}", $"capture-error:{feedback.Id}", Now, ct);
                await tx.CommitAsync(ct);
            }
        }
        var pending = await db.QueryAsync<FeedbackFact>(new CommandDefinition("""
            WITH facts AS (
            """ + FeedbackFacts + """
            ) SELECT f.* FROM facts f JOIN cx_case_feedback x ON x.tenant_id=@tenant AND x.feedback_id=f.id
            JOIN cx_case w ON w.tenant_id=x.tenant_id AND w.id=x.case_id WHERE f.has_analysis AND w.created_by='automation'
              AND w.status='new' AND w.assignee_id IS NULL AND w.first_response_at IS NULL AND w.priority<>f.severity
              AND NOT EXISTS(SELECT 1 FROM cx_case_activity h WHERE h.tenant_id=w.tenant_id AND h.case_id=w.id AND h.kind='updated' AND h.actor NOT IN ('automation','sla-escalation'))
            ORDER BY f.created_at DESC LIMIT 100
            """, new { tenant }, cancellationToken: ct));
        foreach (var feedback in pending)
        {
            var id = await db.ExecuteScalarAsync<Guid>(new CommandDefinition("SELECT case_id FROM cx_case_feedback WHERE tenant_id=@tenant AND feedback_id=@id", new { tenant, feedback.Id }, cancellationToken: ct));
            var item = (await DetailAsync(tenant, id, null, ct))!.Case;
            if (Priorities.Contains(feedback.Severity) && Array.IndexOf(Priorities, feedback.Severity) < Array.IndexOf(Priorities, item.Priority))
                try { await UpdateAsync(tenant, "automation", id, new(item.Version, item.Status, item.TeamId, item.AssigneeId, item.ProblemId, item.ResolutionText, feedback.Severity), ct); }
                catch (OperationsException ex) when (ex.Status == 409) { /* A human edit wins this iteration. */ }
        }
    }
    private async Task ExecuteRulesAsync(string tenant, CancellationToken ct)
    {
        var rules = (await RulesAsync(tenant, ct)).Where(rule => rule.Enabled);
        await using var db = await dataSource.OpenAsync(ct);
        foreach (var rule in rules)
        {
            var candidates = await db.QueryAsync<FeedbackFact>(new CommandDefinition("""
                WITH RECURSIVE branch AS (
                  SELECT id,code FROM topic WHERE tenant_id=@tenant AND code=@topic
                  UNION ALL SELECT t.id,t.code FROM topic t JOIN branch b ON t.parent_id=b.id WHERE t.tenant_id=@tenant
                ), facts AS (
                """ + FeedbackFacts + """
                ) SELECT f.* FROM facts f JOIN customer_identity c ON c.tenant_id=@tenant AND c.customer_id=f.customer_id AND c.merged_into_id IS NULL
                WHERE f.feedback_type='complaint' AND f.created_at>=(SELECT created_at FROM cx_automation_rule WHERE tenant_id=@tenant AND id=@rule)
                  AND (@topic IS NULL OR f.topic IN (SELECT code FROM branch))
                  AND (@severity IS NULL OR (f.has_analysis AND f.severity=@severity)) AND (@channel IS NULL OR f.channel=@channel)
                  AND (SELECT count(*) FROM facts h WHERE h.feedback_type='complaint' AND h.customer_id=f.customer_id AND h.submitted_at>=f.submitted_at-interval '30 days'
                    AND h.submitted_at<=f.submitted_at AND (@topic IS NULL OR h.topic IN (SELECT code FROM branch)))>=@repeat
                  AND NOT EXISTS(SELECT 1 FROM cx_automation_execution e WHERE e.rule_id=@rule AND e.feedback_id=f.id AND e.state='completed')
                ORDER BY f.created_at LIMIT 100
                """, new { tenant, rule = rule.Id, rule.Topic, rule.Severity, rule.Channel, repeat = rule.RepeatCount }, cancellationToken: ct));
            foreach (var feedback in candidates)
            {
                Guid? caseId = null;
                try
                {
                    if (rule.CreateCase)
                    {
                        caseId = await CreateAsync(tenant, "automation", new(StableId($"feedback-case:{tenant}:{feedback.Id}"), feedback.CustomerId,
                            $"Follow-up: {rule.Name}", feedback.Comment ?? "Customer feedback", feedback.Topic, rule.Priority, rule.TeamId, rule.AssigneeId, feedback.Id), ct);
                        var item = (await DetailAsync(tenant, caseId.Value, null, ct))!.Case;
                        var humanEdited = await db.ExecuteScalarAsync<bool>(new CommandDefinition(
                            "SELECT EXISTS(SELECT 1 FROM cx_case_activity WHERE tenant_id=@tenant AND case_id=@id AND kind='updated' AND actor NOT IN ('automation','sla-escalation'))",
                            new { tenant, id = item.Id }, cancellationToken: ct));
                        if (!humanEdited && item.CreatedBy == "automation" && item.Status == "new" && (item.TeamId != rule.TeamId || item.AssigneeId != rule.AssigneeId || item.Priority != rule.Priority))
                            await UpdateAsync(tenant, "automation", item.Id, new(item.Version, item.Status, rule.TeamId, rule.AssigneeId, item.ProblemId, item.ResolutionText, rule.Priority), ct);
                    }
                    await using var tx = await db.BeginTransactionAsync(ct);
                    if (rule.Notify) await NotifyLeadsAsync(db, tx, tenant, $"The rule was implemented: {rule.Name}", feedback.Comment?.Length > 250 ? feedback.Comment[..250] : feedback.Comment ?? "Customer feedback",
                        caseId is null ? $"/complaints?id={feedback.Id}" : $"/complaints/cases/{caseId}", $"rule:{rule.Id}:{feedback.Id}", Now, ct);
                    await db.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO cx_automation_execution(tenant_id,rule_id,feedback_id,case_id,state,error,executed_at)
                        VALUES(@tenant,@rule,@feedback,@caseId,'completed',NULL,@now)
                        ON CONFLICT(rule_id,feedback_id) DO UPDATE SET case_id=@caseId,state='completed',error=NULL,executed_at=@now
                        """, new { tenant, rule = rule.Id, feedback = feedback.Id, caseId, now = Now }, tx, cancellationToken: ct));
                    await tx.CommitAsync(ct);
                }
                catch (OperationsException ex)
                {
                    await db.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO cx_automation_execution(tenant_id,rule_id,feedback_id,case_id,state,error,executed_at)
                        VALUES(@tenant,@rule,@feedback,@caseId,'failed',@error,@now)
                        ON CONFLICT(rule_id,feedback_id) DO UPDATE SET state='failed',error=@error,executed_at=@now
                        """, new { tenant, rule = rule.Id, feedback = feedback.Id, caseId, error = ex.Message, now = Now }, cancellationToken: ct));
                }
            }
        }
    }
    private static async Task NotifyLeadsAsync(NpgsqlConnection db, NpgsqlTransaction tx, string tenant, string title, string body,
        string link, string key, DateTimeOffset now, CancellationToken ct, Guid? assignee = null)
    {
        var people = await db.QueryAsync<Guid>(new CommandDefinition("""
            SELECT id FROM app_user WHERE tenant_id=@tenant AND enabled AND (role IN ('admin','cx_lead','support_supervisor') OR id=@assignee)
            """, new { tenant, assignee }, tx, cancellationToken: ct));
        foreach (var person in people) await NotifyAsync(db, tx, tenant, "staff", person.ToString(), title, body, link, key, now, ct);
    }
    private async Task SlaAlertsAsync(string tenant, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        var cases = await db.QueryAsync<WorkCase>(new CommandDefinition(
            "SELECT *,sla_calendar::text AS sla_calendar_json FROM cx_case WHERE tenant_id=@tenant AND status NOT IN ('resolved','closed') ORDER BY response_due_at LIMIT 1000", new { tenant }, cancellationToken: ct));
        foreach (var item in cases)
        {
            var now = Now;
            var breaches = false;
            foreach (var (metric, due, minutes, active) in new[] {
                ("response", item.ResponseDueAt, item.ResponseMinutes, item.FirstResponseAt is null),
                ("resolution", item.ResolutionDueAt, item.ResolutionMinutes, item.Status != "waiting_customer") })
            {
                if (!active) continue;
                var breached = now > due;
                if (!breached && BusinessClock.Between(now, due, item.Calendar) > minutes * .2) continue;
                var title = breached ? "The case passed the deadline" : "The case is close to the deadline";
                await using var tx = await db.BeginTransactionAsync(ct);
                await NotifyLeadsAsync(db, tx, tenant, title, $"{item.Reference} · {(metric == "response" ? "Initial response" : "Problem solving")}", $"/complaints/cases/{item.Id}",
                    $"sla:{item.Id}:{item.ReopenCount}:{metric}:{due.UtcTicks}:{(breached ? "breach" : "warning")}", now, ct, item.AssigneeId);
                await tx.CommitAsync(ct);
                breaches |= breached;
            }
            if (breaches && item.TeamId is not null)
            {
                var lead = await db.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
                    SELECT t.lead_id FROM cx_team t JOIN app_user u ON u.id=t.lead_id AND u.tenant_id=t.tenant_id
                    WHERE t.tenant_id=@tenant AND t.id=@team AND t.enabled AND u.enabled
                    """, new { tenant, team = item.TeamId }, cancellationToken: ct));
                if (lead is not null && item.AssigneeId != lead)
                    try { await UpdateAsync(tenant, "sla-escalation", item.Id, new(item.Version, item.Status, item.TeamId, lead, item.ProblemId, item.ResolutionText), ct); }
                    catch (OperationsException ex) when (ex.Status == 409) { }
            }
        }
    }
    private async Task EventInvitationsAsync(string tenant, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        var campaigns = await db.QueryAsync<SurveyCampaign>(new CommandDefinition(
            "SELECT * FROM cx_survey_campaign WHERE tenant_id=@tenant AND trigger='event' AND enabled ORDER BY created_at,id", new { tenant }, cancellationToken: ct));
        foreach (var campaign in campaigns)
        {
            var events = await db.QueryAsync<TriggerEvent>(new CommandDefinition("""
                SELECT e.id,e.customer_id FROM cx_event e JOIN customer_identity c ON c.tenant_id=e.tenant_id AND c.customer_id=e.customer_id AND c.merged_into_id IS NULL
                WHERE e.tenant_id=@tenant AND e.event_name=@eventName AND e.occurred_at>=(SELECT created_at FROM cx_survey_campaign WHERE id=@campaign AND tenant_id=@tenant)
                  AND NOT EXISTS(SELECT 1 FROM cx_campaign_event x WHERE x.campaign_id=@campaign AND x.event_id=e.id)
                ORDER BY e.id LIMIT 200
                """, new { tenant, campaign = campaign.Id, campaign.EventName }, cancellationToken: ct));
            foreach (var evt in events)
            {
                await using var tx = await db.BeginTransactionAsync(ct);
                var invited = await InviteAsync(db, tx, tenant, campaign, evt.CustomerId, $"event:{evt.Id}", null, Now, ct);
                await db.ExecuteAsync(new CommandDefinition("INSERT INTO cx_campaign_event(tenant_id,campaign_id,event_id,invited) VALUES(@tenant,@campaign,@eventId,@invited) ON CONFLICT DO NOTHING",
                    new { tenant, campaign = campaign.Id, eventId = evt.Id, invited }, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
            }
        }
    }
    private async Task RemindersAsync(string tenant, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var invitations = await db.QueryAsync<SurveyInvitation>(new CommandDefinition("""
            SELECT i.*,c.name AS campaign_name FROM cx_survey_invitation i JOIN cx_survey_campaign c ON c.tenant_id=i.tenant_id AND c.id=i.campaign_id
            WHERE i.tenant_id=@tenant AND c.enabled AND i.answered_at IS NULL AND i.declined_at IS NULL AND i.reminder_at IS NULL
              AND i.expires_at>@now AND i.sent_at+c.reminder_days*interval '1 day'<=@now
            ORDER BY i.sent_at LIMIT 200 FOR UPDATE OF i SKIP LOCKED
            """, new { tenant, now = Now }, tx, cancellationToken: ct));
        foreach (var invitation in invitations)
        {
            await NotifyAsync(db, tx, tenant, "customer", invitation.CustomerId, $"Reminder: {invitation.CampaignName}", invitation.Question,
                $"/surveys/{invitation.Id}", $"reminder:{invitation.Id}", Now, ct);
            await db.ExecuteAsync(new CommandDefinition("UPDATE cx_survey_invitation SET reminder_at=@now WHERE tenant_id=@tenant AND id=@id",
                new { tenant, invitation.Id, now = Now }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }
    private sealed class FeedbackFact
    {
        public Guid Id { get; set; }
        public string CustomerId { get; set; } = "";
        public string? Comment { get; set; }
        public string? Topic { get; set; }
        public string Severity { get; set; } = "medium";
    }
    private sealed class TriggerEvent
    {
        public long Id { get; set; }
        public string CustomerId { get; set; } = "";
    }
}
