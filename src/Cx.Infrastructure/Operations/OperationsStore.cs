using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cx.Application.Operations;
using Cx.Application.Options;
using Cx.Contracts;
using Cx.Infrastructure.Database;
using Dapper;
using Npgsql;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Operations;

public sealed partial class OperationsStore(CxDataSource dataSource, TimeProvider clock, IOptions<KafkaOptions> messaging)
{
    private DateTimeOffset Now => clock.GetUtcNow().ToUniversalTime();
    public static readonly string[] Priorities = ["critical", "high", "medium", "low"];
    public static readonly string[] StaffRoles = ["admin", "cx_analyst", "cx_lead", "support_supervisor", "data_ai_operator"];
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, CxJson.Options);
    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    public static string CustomerActor(string customer) => $"customer:{StableId(customer):N}";
    private static void Text(string? value, int maximum, string label, bool required = true)
    {
        if (value?.Length > maximum || (required && string.IsNullOrWhiteSpace(value))) throw new OperationsException($"{label} Not valid (Max {maximum} Character).");
    }
    private static void Priority(string priority)
    {
        if (!Priorities.Contains(priority)) throw new OperationsException("Severity is not valid.");
    }
    public async Task EnsureDefaultsAsync(string tenant, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        var calendar = Json(new BusinessCalendar());
        foreach (var (priority, response, resolution, name) in new[] {
            ("critical", 30, 240, "Critical"), ("high", 60, 480, "High"), ("medium", 240, 1440, "Medium"), ("low", 480, 2400, "Low") })
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT INTO cx_sla_policy(id,tenant_id,name,priority,response_minutes,resolution_minutes,calendar)
                VALUES(@id,@tenant,@name,@priority,@response,@resolution,@calendar::jsonb)
                ON CONFLICT(tenant_id,priority) DO NOTHING
                """, new { id = StableId($"sla:{tenant}:{priority}"), tenant, name = $"Deadline {name}", priority, response, resolution, calendar }, cancellationToken: ct));
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_survey_campaign(id,tenant_id,name,question,metric,trigger,cooldown_days,reminder_days,expires_days,enabled)
            VALUES(@id,@tenant,'Consent after processing','How satisfied are you with the handling of your request?','csat','case_resolved',30,3,14,true)
            ON CONFLICT(id) DO NOTHING
            """, new { id = StableId($"aftercare:{tenant}"), tenant }, cancellationToken: ct));
    }

    private static async Task ValidateAssignmentAsync(NpgsqlConnection db, NpgsqlTransaction? tx, string tenant, Guid? team, Guid? assignee, CancellationToken ct)
    {
        if (team is not null && !await db.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM cx_team WHERE tenant_id=@tenant AND id=@team AND enabled)", new { tenant, team }, tx, cancellationToken: ct)))
            throw new OperationsException("Select the active team.");
        if (assignee is not null && !await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM app_user u WHERE u.tenant_id=@tenant AND u.id=@assignee AND u.enabled AND u.role IN ('admin','cx_analyst','cx_lead','support_supervisor')
              AND (@team IS NULL OR EXISTS(SELECT 1 FROM cx_team_member m WHERE m.tenant_id=@tenant AND m.team_id=@team AND m.user_id=u.id)))
            """, new { tenant, team, assignee }, tx, cancellationToken: ct)))
            throw new OperationsException("Select the active team member expert.");
    }

    private static async Task ActivityAsync(NpgsqlConnection db, NpgsqlTransaction tx, string tenant, Guid id, string actor,
        string kind, string text, bool isPublic, DateTimeOffset now, CancellationToken ct, Guid? activityId = null)
    {
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_case_activity(id,tenant_id,case_id,kind,actor,public,text,created_at,team_id,stage_title)
            SELECT @activityId,@tenant,@id,@kind,@actor,@isPublic,@text,@now,
              CASE WHEN @kind='customer_message' THEN NULL ELSE team_id END,
              CASE WHEN @kind<>'customer_message' AND stage_index>=0 THEN workflow_snapshot->'stages'->stage_index->>'title' ELSE NULL END
            FROM cx_case WHERE tenant_id=@tenant AND id=@id ON CONFLICT(id) DO NOTHING
            """, new { activityId = activityId ?? Guid.NewGuid(), tenant, id, kind, actor, isPublic, text, now }, tx, cancellationToken: ct));
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_log(tenant_id,actor_id,action,resource_type,resource_id,after,occurred_at)
            VALUES(@tenant,@actor,'case.'||@kind,'cx_case',@resource,@detail::jsonb,@now)
            """, new { tenant, actor, kind, resource = id.ToString(), detail = Json(new { text, isPublic }), now }, tx, cancellationToken: ct));
    }

    private static Task NotifyAsync(NpgsqlConnection db, NpgsqlTransaction? tx, string tenant, string audience, string recipient,
        string title, string body, string link, string key, DateTimeOffset now, CancellationToken ct) => db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_notification(id,tenant_id,audience,recipient,title,body,link,dedup_key,created_at)
            VALUES(@id,@tenant,@audience,@recipient,@title,@body,@link,@key,@now)
            ON CONFLICT(tenant_id,audience,recipient,dedup_key) DO NOTHING
            """, new { id = Guid.NewGuid(), tenant, audience, recipient, title, body, link, key, now }, tx, cancellationToken: ct));

    private const string CaseSelect = """
        SELECT w.*, w.sla_calendar::text AS sla_calendar_json,w.workflow_snapshot::text AS workflow_json, COALESCE(c.display_name,w.customer_id) AS customer_name,
          t.name AS team_name, COALESCE(u.display_name,u.username) AS assignee_name,p.title AS problem_title
        FROM cx_case w JOIN customer_identity c ON c.tenant_id=w.tenant_id AND c.customer_id=w.customer_id
        LEFT JOIN cx_team t ON t.tenant_id=w.tenant_id AND t.id=w.team_id
        LEFT JOIN app_user u ON u.tenant_id=w.tenant_id AND u.id=w.assignee_id
        LEFT JOIN cx_problem p ON p.tenant_id=w.tenant_id AND p.id=w.problem_id
        """ + " ";

    public async Task<Guid?> StaffIdAsync(string tenant, string username, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM app_user WHERE tenant_id=@tenant AND lower(username)=lower(@username) AND role<>'customer' AND enabled",
            new { tenant, username }, cancellationToken: ct));
    }
    public async Task<Guid?> CaseForFeedbackAsync(string tenant, Guid feedback, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT case_id FROM cx_case_feedback WHERE tenant_id=@tenant AND feedback_id=@feedback", new { tenant, feedback }, cancellationToken: ct));
    }
    public async Task<bool> CanAccessAsync(string tenant, Guid id, Guid? staffId, bool manager, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        return await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM cx_case w WHERE tenant_id=@tenant AND id=@id AND
              (@manager OR assignee_id=@staffId OR (assignee_id IS NULL AND team_id IS NULL) OR
                EXISTS(SELECT 1 FROM cx_team_member WHERE tenant_id=@tenant AND team_id=w.team_id AND user_id=@staffId)))
            """, new { tenant, id, staffId, manager }, cancellationToken: ct));
    }

    public async Task<object> CasesAsync(string tenant, Guid? staffId, bool manager, string? customer, string? status,
        string? search, Guid? team, bool mine, bool overdue, int page, CancellationToken ct, int pageSize = 20)
    {
        page = Math.Clamp(page, 1, 100000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        Text(search, 200, "Search", false);
        await using var db = await dataSource.OpenAsync(ct);
        const string filter = """
            WHERE w.tenant_id=@tenant AND (@customer IS NULL OR w.customer_id=@customer) AND (@status IS NULL OR w.status=@status)
            AND EXISTS(SELECT 1 FROM cx_case_feedback scope_link JOIN feedback scope_feedback
              ON scope_feedback.tenant_id=scope_link.tenant_id AND scope_feedback.id=scope_link.feedback_id
              WHERE scope_link.tenant_id=w.tenant_id AND scope_link.case_id=w.id AND scope_feedback.feedback_type='complaint')
            AND (@search IS NULL OR w.title ILIKE @search OR w.reference ILIKE @search OR c.display_name ILIKE @search)
            AND (@team IS NULL OR w.team_id=@team) AND (NOT @mine OR w.assignee_id=@staffId)
            AND (NOT @overdue OR (w.status NOT IN ('resolved','closed') AND (w.first_response_at IS NULL AND w.response_due_at<@now
              OR w.status<>'waiting_customer' AND w.resolution_due_at<@now)))
            AND (@manager OR w.assignee_id=@staffId OR (w.assignee_id IS NULL AND w.team_id IS NULL)
              OR EXISTS(SELECT 1 FROM cx_team_member WHERE tenant_id=@tenant AND team_id=w.team_id AND user_id=@staffId))
            """;
        var args = new { tenant, staffId, manager, customer, status, search = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%", team, mine, overdue, take = pageSize, offset = (page - 1) * pageSize, now = Now };
        var items = (await db.QueryAsync<WorkCase>(new CommandDefinition(CaseSelect + filter + " ORDER BY w.updated_at DESC,w.id LIMIT @take OFFSET @offset", args, cancellationToken: ct))).AsList();
        var total = await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM cx_case w JOIN customer_identity c ON c.tenant_id=w.tenant_id AND c.customer_id=w.customer_id " + filter, args, cancellationToken: ct));
        return new { items, total, page, page_size = pageSize };
    }

    public async Task<CaseDetail?> DetailAsync(string tenant, Guid id, string? customer, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        var item = await db.QuerySingleOrDefaultAsync<WorkCase>(new CommandDefinition(CaseSelect +
            " WHERE w.tenant_id=@tenant AND w.id=@id AND (@customer IS NULL OR w.customer_id=@customer)", new { tenant, id, customer }, cancellationToken: ct));
        if (item is null) return null;
        var activities = (await db.QueryAsync<CaseActivity>(new CommandDefinition(
            "SELECT id,kind,actor,public,text,created_at,team_id,stage_title FROM cx_case_activity WHERE tenant_id=@tenant AND case_id=@id AND (@customer IS NULL OR public) ORDER BY created_at,id",
            new { tenant, id, customer }, cancellationToken: ct))).AsList();
        var attachments = (await db.QueryAsync<CaseAttachment>(new CommandDefinition(
            "SELECT * FROM cx_case_attachment WHERE tenant_id=@tenant AND case_id=@id AND (@customer IS NULL OR public) ORDER BY created_at,id",
            new { tenant, id, customer }, cancellationToken: ct))).AsList();
        var feedback = (await db.QueryAsync<Guid>(new CommandDefinition(
            "SELECT feedback_id FROM cx_case_feedback WHERE tenant_id=@tenant AND case_id=@id", new { tenant, id }, cancellationToken: ct))).AsList();
        return new(item, activities, attachments, feedback);
    }

    public async Task<Guid> CreateAsync(string tenant, string actor, CreateCaseInput input, CancellationToken ct)
    {
        if (input.Id == Guid.Empty) throw new OperationsException("The file ID is not valid.");
        Text(input.CustomerId, 128, "Customer"); Text(input.Title, 256, "Title"); Text(input.Description, 20000, "Description"); Text(input.Topic, 128, "Topic", false); Priority(input.Priority);
        await EnsureDefaultsAsync(tenant, ct);
        await using var db = await dataSource.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
            new { key = $"case:{tenant}:{input.FeedbackId?.ToString() ?? input.Id.ToString()}" }, tx, cancellationToken: ct));
        var duplicate = await db.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
            SELECT id FROM cx_case WHERE tenant_id=@tenant AND id=@id
            UNION SELECT case_id FROM cx_case_feedback WHERE tenant_id=@tenant AND feedback_id=@feedback LIMIT 1
            """, new { tenant, input.Id, feedback = input.FeedbackId }, tx, cancellationToken: ct));
        if (duplicate is not null) { await tx.CommitAsync(ct); return duplicate.Value; }
        if (!await db.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM customer_identity WHERE tenant_id=@tenant AND customer_id=@customer AND merged_into_id IS NULL)", new { tenant, customer = input.CustomerId }, tx, cancellationToken: ct)))
            throw new OperationsException("The client is not valid.");
        await ValidateAssignmentAsync(db, tx, tenant, input.TeamId, input.AssigneeId, ct);
        if (input.FeedbackId is not null && !await db.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM feedback WHERE tenant_id=@tenant AND id=@feedback AND customer_id=@customer AND feedback_type='complaint')", new { tenant, feedback = input.FeedbackId, customer = input.CustomerId }, tx, cancellationToken: ct)))
            throw new OperationsException("A new case is created only for the complaint belonging to this customer.");
        var policy = await db.QuerySingleOrDefaultAsync<SlaPolicy>(new CommandDefinition(
            "SELECT *,calendar::text AS calendar_json FROM cx_sla_policy WHERE tenant_id=@tenant AND priority=@priority AND enabled",
            new { tenant, input.Priority }, tx, cancellationToken: ct));
        if (policy is null) throw new OperationsException("Define an active timeout policy for this severity.");
        var now = Now;
        var received = input.FeedbackId is null ? now : new DateTimeOffset(await db.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT submitted_at FROM feedback WHERE tenant_id=@tenant AND id=@feedback", new { tenant, feedback = input.FeedbackId }, tx, cancellationToken: ct)));
        if (received > now) received = now;
        var reference = $"CX-{input.Id.ToString("N")[..12].ToUpperInvariant()}";
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_case(id,tenant_id,reference,customer_id,title,description,topic,priority,team_id,assignee_id,problem_id,
              sla_policy_id,sla_calendar,response_minutes,resolution_minutes,response_due_at,resolution_due_at,resolution_started_at,created_by,created_at,updated_at)
            VALUES(@id,@tenant,@reference,@customer,@title,@description,@topic,@priority,@team,@assignee,@problem,
              @policy,@calendar::jsonb,@response,@resolution,@responseDue,@resolutionDue,@received,@actor,@received,@now)
            """, new { input.Id, tenant, reference, customer = input.CustomerId, title = input.Title.Trim(), description = input.Description.Trim(), input.Topic,
                input.Priority, team = input.TeamId, assignee = input.AssigneeId, problem = input.ProblemId, policy = policy.Id,
                calendar = Json(policy.Calendar), response = policy.ResponseMinutes, resolution = policy.ResolutionMinutes,
                responseDue = BusinessClock.Add(received, policy.ResponseMinutes, policy.Calendar), resolutionDue = BusinessClock.Add(received, policy.ResolutionMinutes, policy.Calendar), actor, now, received }, tx, cancellationToken: ct));
        if (input.FeedbackId is not null) await db.ExecuteAsync(new CommandDefinition(
            "INSERT INTO cx_case_feedback(tenant_id,case_id,feedback_id) VALUES(@tenant,@id,@feedback)", new { tenant, input.Id, feedback = input.FeedbackId }, tx, cancellationToken: ct));
        await ActivityAsync(db, tx, tenant, input.Id, actor, "created", "The case was registered.", true, now, ct);
        await NotifyAsync(db, tx, tenant, "customer", input.CustomerId, "Your request has been registered", reference, $"/tracking/{input.Id}", $"created:{input.Id}", now, ct);
        if (input.AssigneeId is not null) await NotifyAsync(db, tx, tenant, "staff", input.AssigneeId.Value.ToString(), "New file for you", input.Title, $"/complaints/cases/{input.Id}", $"assigned:{input.Id}:1", now, ct);
        await tx.CommitAsync(ct);
        return input.Id;
    }

    public async Task UpdateAsync(string tenant, string actor, Guid id, UpdateCaseInput input, CancellationToken ct, string? customerComment = null, WorkflowChange? workflowChange = null)
    {
        if (!new[] { "new", "in_progress", "waiting_customer", "resolved", "closed" }.Contains(input.Status)) throw new OperationsException("The status is not valid.");
        Text(input.ResolutionText, 12000, "Result", input.Status is "resolved" or "closed");
        Text(input.Comment, 12000, "Explanation of the change", false);
        await using var db = await dataSource.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var item = await db.QuerySingleOrDefaultAsync<WorkCase>(new CommandDefinition(
            "SELECT *,sla_calendar::text AS sla_calendar_json,workflow_snapshot::text AS workflow_json FROM cx_case WHERE tenant_id=@tenant AND id=@id FOR UPDATE", new { tenant, id }, tx, cancellationToken: ct))
            ?? throw new OperationsException("File not found.", 404);
        if (item.Version != input.Version) throw new OperationsException("The file has changed; Refresh the information.", 409);
        if (workflowChange is not null)
        {
            if (!workflowChange.Manager && item.TeamId is not null && !await db.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM cx_team_member WHERE tenant_id=@tenant AND team_id=@team AND user_id=@staff)",
                new { tenant, team = item.TeamId, staff = workflowChange.StaffId }, tx, cancellationToken: ct)))
                throw new OperationsException("Only the team in charge of the stage can advance or close the proceedings.", 403);
        }
        await ValidateAssignmentAsync(db, tx, tenant, input.TeamId, input.AssigneeId, ct);
        if (input.ProblemId is not null && !await db.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM cx_problem WHERE tenant_id=@tenant AND id=@problem)", new { tenant, problem = input.ProblemId }, tx, cancellationToken: ct)))
            throw new OperationsException("Common problem is not valid.");
        var now = Now;
        var previousStatus = item.Status;
        var previousAssignee = item.AssigneeId;
        var previousTeam = item.TeamId;
        var previousPriority = item.Priority;
        var previousProblem = item.ProblemId;
        var previousResolution = item.ResolutionText;
        if (item.PauseStartedAt is not null && input.Status != "waiting_customer")
        {
            var paused = BusinessClock.Between(item.PauseStartedAt.Value, now, item.Calendar);
            item.PausedMinutes += paused;
            item.ResolutionDueAt = BusinessClock.Add(item.ResolutionDueAt, paused, item.Calendar);
            item.PauseStartedAt = null;
        }
        if (input.Priority is not null && input.Priority != item.Priority)
        {
            Priority(input.Priority);
            var policy = await db.QuerySingleOrDefaultAsync<SlaPolicy>(new CommandDefinition(
                "SELECT *,calendar::text AS calendar_json FROM cx_sla_policy WHERE tenant_id=@tenant AND priority=@priority AND enabled", new { tenant, input.Priority }, tx, cancellationToken: ct))
                ?? throw new OperationsException("There is no active timeout policy for this severity.");
            item.Priority = input.Priority; item.SlaPolicyId = policy.Id;
            item.ResponseMinutes = policy.ResponseMinutes; item.ResolutionMinutes = policy.ResolutionMinutes;
            if (item.FirstResponseAt is null) item.ResponseDueAt = BusinessClock.Add(item.CreatedAt, policy.ResponseMinutes, item.Calendar);
            item.ResolutionDueAt = BusinessClock.Add(item.ResolutionStartedAt, policy.ResolutionMinutes + item.PausedMinutes, item.Calendar);
        }
        var reopening = previousStatus is "resolved" or "closed" && input.Status is not ("resolved" or "closed");
        if (reopening)
        {
            item.ReopenCount++; item.ResolvedAt = null; item.ClosedAt = null;
            item.ResolutionStartedAt = now; item.PausedMinutes = 0;
            item.ResolutionDueAt = BusinessClock.Add(now, item.ResolutionMinutes, item.Calendar);
        }
        if (input.Status == "waiting_customer" && item.PauseStartedAt is null) item.PauseStartedAt = now;
        if (input.Status is "resolved" or "closed")
        {
            if (item.FirstResponseAt is null) { item.ResponseBusinessMinutes = BusinessClock.Between(item.CreatedAt, now, item.Calendar); item.ResponseWithinSla = now <= item.ResponseDueAt; }
            if (item.FirstResolvedAt is null) { item.ResolutionBusinessMinutes = Math.Max(0, BusinessClock.Between(item.CreatedAt, now, item.Calendar) - item.PausedMinutes); item.ResolutionWithinSla = now <= item.ResolutionDueAt; }
            item.ResolvedAt ??= now; item.FirstResolvedAt ??= now; item.FirstResponseAt ??= now;
            if (input.Status == "closed") item.ClosedAt ??= now;
        }
        // The note belongs to the team and stage completing the work, before the handoff.
        if (!string.IsNullOrWhiteSpace(input.Comment))
            await ActivityAsync(db, tx, tenant, id, actor, "message", input.Comment.Trim(), false, now, ct);
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE cx_case SET status=@status,team_id=@team,assignee_id=@assignee,problem_id=@problem,
              priority=@Priority,sla_policy_id=@SlaPolicyId,sla_calendar=@SlaCalendarJson::jsonb,response_minutes=@ResponseMinutes,resolution_minutes=@ResolutionMinutes,
              response_due_at=@ResponseDueAt,resolution_due_at=@ResolutionDueAt,first_response_at=@FirstResponseAt,first_resolved_at=@FirstResolvedAt,
              resolution_started_at=@ResolutionStartedAt,
              resolved_at=@ResolvedAt,closed_at=@ClosedAt,pause_started_at=@PauseStartedAt,paused_minutes=@PausedMinutes,
              response_business_minutes=@ResponseBusinessMinutes,resolution_business_minutes=@ResolutionBusinessMinutes,
              response_within_sla=@ResponseWithinSla,resolution_within_sla=@ResolutionWithinSla,
              resolution_text=@resolution,reopen_count=@ReopenCount,version=version+1,updated_at=@now WHERE tenant_id=@tenant AND id=@id
            """, new { tenant, id, input.Status, team = input.TeamId, assignee = input.AssigneeId, problem = input.ProblemId,
                item.Priority, item.SlaPolicyId, item.SlaCalendarJson, item.ResponseMinutes, item.ResolutionMinutes, item.ResponseDueAt, item.ResolutionDueAt,
                item.FirstResponseAt, item.FirstResolvedAt, item.ResolvedAt, item.ClosedAt, item.PauseStartedAt, item.PausedMinutes,
                item.ResolutionStartedAt,
                item.ResponseBusinessMinutes, item.ResolutionBusinessMinutes, item.ResponseWithinSla, item.ResolutionWithinSla,
                resolution = input.ResolutionText?.Trim() ?? "", item.ReopenCount, now }, tx, cancellationToken: ct));
        var changes = new[] {
            new { field = "status", before = previousStatus, after = input.Status },
            new { field = "team", before = previousTeam?.ToString(), after = input.TeamId?.ToString() },
            new { field = "assignee", before = previousAssignee?.ToString(), after = input.AssigneeId?.ToString() },
            new { field = "priority", before = previousPriority, after = item.Priority },
            new { field = "problem", before = previousProblem?.ToString(), after = input.ProblemId?.ToString() },
            new { field = "resolution", before = previousResolution, after = input.ResolutionText?.Trim() ?? "" }
        }.Where(change => change.before != change.after).ToArray();
        if (changes.Length > 0)
            await ActivityAsync(db, tx, tenant, id, actor, "updated", Json(new { from = previousStatus, to = input.Status,
                team = input.TeamId, assignee = input.AssigneeId, changes }), false, now, ct);
        if (workflowChange is not null)
        {
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE cx_case SET workflow_id=@workflow,workflow_snapshot=@snapshot::jsonb,stage_index=@stage,
                  routing_reason=@reason,routing_model=@model WHERE tenant_id=@tenant AND id=@id
                """, new { tenant, id, workflow = workflowChange.WorkflowId, snapshot = Json(workflowChange.Workflow),
                    stage = workflowChange.StageIndex, reason = workflowChange.Reason, model = workflowChange.Model }, tx, cancellationToken: ct));
            var activityId = Guid.NewGuid();
            await ActivityAsync(db, tx, tenant, id, actor, "workflow", workflowChange.Activity, false, now, ct, activityId);
            if (item.StageIndex >= 0 && item.StageIndex < item.Workflow.Stages.Length)
                await db.ExecuteAsync(new CommandDefinition("UPDATE cx_case_activity SET team_id=@team,stage_title=@stage WHERE id=@activityId AND tenant_id=@tenant",
                    new { tenant, activityId, team = item.TeamId, stage = item.Workflow.Stages[item.StageIndex].Title }, tx, cancellationToken: ct));
            if (item.TeamId != input.TeamId && input.TeamId is not null)
            {
                var members = await db.QueryAsync<Guid>(new CommandDefinition("SELECT user_id FROM cx_team_member WHERE tenant_id=@tenant AND team_id=@team",
                    new { tenant, team = input.TeamId }, tx, cancellationToken: ct));
                foreach (var member in members) await NotifyAsync(db, tx, tenant, "staff", member.ToString(), "A new step for your team", item.Title,
                    $"/complaints/cases/{id}", $"stage:{id}:{item.Version + 1}", now, ct);
            }
        }
        if (previousStatus != input.Status)
        {
            var text = input.Status is "resolved" or "closed" ? input.ResolutionText!.Trim() : input.Status switch {
                "waiting_customer" => "We are waiting for your reply to proceed.", "in_progress" => "The processing of the request has started.", _ => "Request status changed." };
            await ActivityAsync(db, tx, tenant, id, actor, "status", text, true, now, ct);
            await NotifyAsync(db, tx, tenant, "customer", item.CustomerId, "The status of your request has changed", text, $"/tracking/{id}", $"status:{id}:{item.Version + 1}", now, ct);
            if (input.Status is "resolved" or "closed" && previousStatus is not ("resolved" or "closed"))
                await InviteAftercareAsync(db, tx, tenant, item.CustomerId, id, item.ReopenCount, now, ct);
        }
        if (input.AssigneeId is not null && input.AssigneeId != previousAssignee)
            await NotifyAsync(db, tx, tenant, "staff", input.AssigneeId.Value.ToString(), "The case was referred to you", item.Title, $"/complaints/cases/{id}", $"assigned:{id}:{item.Version + 1}", now, ct);
        if (customerComment is not null)
        {
            await ActivityAsync(db, tx, tenant, id, actor, "customer_message", customerComment.Trim(), true, now, ct);
            if (item.AssigneeId is not null) await NotifyAsync(db, tx, tenant, "staff", item.AssigneeId.Value.ToString(), "The customer asked for reopening", item.Reference,
                $"/complaints/cases/{id}", $"reopened:{id}:{item.Version + 1}", now, ct);
        }
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE feedback SET status=@feedbackStatus,assignee_id=@assignee WHERE tenant_id=@tenant AND id IN
              (SELECT feedback_id FROM cx_case_feedback WHERE tenant_id=@tenant AND case_id=@id)
            """, new { tenant, id, feedbackStatus = input.Status is "resolved" or "closed" ? "closed" : input.Status == "new" ? "new" : "in_progress", assignee = input.AssigneeId?.ToString() }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task MessageAsync(string tenant, string actor, Guid id, CaseMessageInput input, string? customer, CancellationToken ct)
    {
        Text(input.Text, 12000, "Message text");
        if (input.Id == Guid.Empty) throw new OperationsException("The message identifier is not valid.");
        await using var db = await dataSource.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var item = await db.QuerySingleOrDefaultAsync<WorkCase>(new CommandDefinition(
            "SELECT *,sla_calendar::text AS sla_calendar_json FROM cx_case WHERE tenant_id=@tenant AND id=@id AND (@customer IS NULL OR customer_id=@customer) FOR UPDATE",
            new { tenant, id, customer }, tx, cancellationToken: ct)) ?? throw new OperationsException("File not found.", 404);
        var duplicate = await db.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition("""
            SELECT tenant_id=@tenant AND case_id=@id AND actor=@actor AND text=@text AND public=@isPublic
            FROM cx_case_activity WHERE id=@activity
            """, new { tenant, id, actor, text = input.Text.Trim(), isPublic = customer is not null || input.Public, activity = input.Id }, tx, cancellationToken: ct));
        if (duplicate is false) throw new OperationsException("Message ID is already used.", 409);
        if (duplicate is true) { await tx.CommitAsync(ct); return; }
        if (item.Status is "resolved" or "closed" && (customer is not null || input.Public)) throw new OperationsException("First, open the file.", 409);
        var now = Now;
        if (customer is not null && item.PauseStartedAt is not null)
        {
            var paused = BusinessClock.Between(item.PauseStartedAt.Value, now, item.Calendar);
            item.ResolutionDueAt = BusinessClock.Add(item.ResolutionDueAt, paused, item.Calendar);
            item.PausedMinutes += paused; item.PauseStartedAt = null; item.Status = "in_progress";
        }
        if (customer is null && input.Public && item.FirstResponseAt is null)
        {
            item.FirstResponseAt = now;
            item.ResponseBusinessMinutes = BusinessClock.Between(item.CreatedAt, now, item.Calendar);
            item.ResponseWithinSla = now <= item.ResponseDueAt;
        }
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE cx_case SET first_response_at=@FirstResponseAt,status=@Status,resolution_due_at=@ResolutionDueAt,
              paused_minutes=@PausedMinutes,pause_started_at=@PauseStartedAt,response_business_minutes=@ResponseBusinessMinutes,
              response_within_sla=@ResponseWithinSla,updated_at=@now,version=version+1 WHERE tenant_id=@tenant AND id=@id
            """, new { tenant, id, item.FirstResponseAt, item.Status, item.ResolutionDueAt, item.PausedMinutes, item.PauseStartedAt, item.ResponseBusinessMinutes, item.ResponseWithinSla, now }, tx, cancellationToken: ct));
        await ActivityAsync(db, tx, tenant, id, actor, customer is null ? "message" : "customer_message", input.Text.Trim(), customer is not null || input.Public, now, ct, input.Id);
        if (customer is null && input.Public) await NotifyAsync(db, tx, tenant, "customer", item.CustomerId, "New response to your request", item.Reference, $"/tracking/{id}", $"message:{input.Id}", now, ct);
        if (customer is not null && item.AssigneeId is not null) await NotifyAsync(db, tx, tenant, "staff", item.AssigneeId.Value.ToString(), "The customer replied", item.Reference, $"/complaints/cases/{id}", $"message:{input.Id}", now, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<CustomerCaseAction?> CustomerActionAsync(string tenant, string customer, Guid id, CustomerCaseAction input, CancellationToken ct)
    {
        var detail = await DetailAsync(tenant, id, customer, ct) ?? throw new OperationsException("File not found.", 404);
        var item = detail.Case;
        if (input.Action == "confirm" && item.Status != "resolved") throw new OperationsException("The file is not yet ready for approval.", 409);
        if (input.Action == "reopen" && item.Status is not ("resolved" or "closed")) throw new OperationsException("This file is open.", 409);
        if (input.Action is not ("confirm" or "reopen")) throw new OperationsException("The operation is not valid.");
        Text(input.Comment, 12000, "Reason for reopening", input.Action == "reopen");
        await UpdateAsync(tenant, CustomerActor(customer), id, new(input.Version, input.Action == "confirm" ? "closed" : "in_progress", item.TeamId, item.AssigneeId, item.ProblemId, item.ResolutionText), ct,
            input.Action == "reopen" ? input.Comment : null);
        return input;
    }

    public async Task<IReadOnlyList<WorkNotification>> NotificationsAsync(string tenant, string audience, string recipient, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        return (await db.QueryAsync<WorkNotification>(new CommandDefinition(
            "SELECT id,title,body,link,created_at,read_at FROM cx_notification WHERE tenant_id=@tenant AND audience=@audience AND recipient=@recipient ORDER BY created_at DESC LIMIT 100",
            new { tenant, audience, recipient }, cancellationToken: ct))).AsList();
    }
    public async Task ReadNotificationAsync(string tenant, string audience, string recipient, Guid id, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("UPDATE cx_notification SET read_at=COALESCE(read_at,@now) WHERE tenant_id=@tenant AND audience=@audience AND recipient=@recipient AND id=@id",
            new { tenant, audience, recipient, id, now = Now }, cancellationToken: ct));
    }

    public async Task<long> UnreadNotificationsAsync(string tenant, string audience, string recipient, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        return await db.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM cx_notification WHERE tenant_id=@tenant AND audience=@audience AND recipient=@recipient AND read_at IS NULL",
            new { tenant, audience, recipient }, cancellationToken: ct));
    }

    public async Task<object> NotificationGridAsync(string tenant, string audience, string recipient, string? search, string? status,
        int page, int pageSize, CancellationToken ct)
    {
        Text(search, 200, "Search", false);
        if (status is not (null or "" or "read" or "unread")) throw new OperationsException("Notification status is not valid.");
        page = Math.Clamp(page, 1, 100000); pageSize = Math.Clamp(pageSize, 1, 100);
        await using var db = await dataSource.OpenAsync(ct);
        const string filter = """
            WHERE tenant_id=@tenant AND audience=@audience AND recipient=@recipient
              AND (@search IS NULL OR title ILIKE @search OR body ILIKE @search)
              AND (@status IS NULL OR @status='' OR (@status='read' AND read_at IS NOT NULL) OR (@status='unread' AND read_at IS NULL))
            """;
        var values = new { tenant, audience, recipient, search = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%", status, take = pageSize, skip = (page - 1) * pageSize };
        var total = await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM cx_notification " + filter, values, cancellationToken: ct));
        var items = (await db.QueryAsync<WorkNotification>(new CommandDefinition(
            "SELECT id,title,body,link,created_at,read_at FROM cx_notification " + filter + " ORDER BY created_at DESC,id DESC LIMIT @take OFFSET @skip", values, cancellationToken: ct))).AsList();
        return new { items, total, page, page_size = pageSize };
    }
}
