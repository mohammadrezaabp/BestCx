using Cx.Application.Operations;
using Cx.Contracts;
using System.Text.Json;
using Dapper;

namespace Cx.Infrastructure.Operations;

public sealed partial class OperationsStore
{
    private async Task AuditConfigurationAsync(string tenant, string actor, string resource, Guid id, object input, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_log(tenant_id,actor_id,action,resource_type,resource_id,after)
            VALUES(@tenant,@actor,'operations.configure',@resource,@id::text,@input::jsonb)
            """, new { tenant, actor, resource, id, input = Json(input) }, cancellationToken: ct));
    }
    public async Task<IReadOnlyList<WorkTeam>> TeamsAsync(string tenant, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        return (await db.QueryAsync<WorkTeam>(new CommandDefinition("""
            SELECT t.*,ARRAY(SELECT user_id FROM cx_team_member WHERE tenant_id=@tenant AND team_id=t.id ORDER BY user_id) AS members
            FROM cx_team t WHERE tenant_id=@tenant ORDER BY name
            """, new { tenant }, cancellationToken: ct))).AsList();
    }
    public async Task SaveTeamAsync(string tenant, string actor, TeamInput input, CancellationToken ct)
    {
        Text(input.Name, 128, "Name of the team");
        Text(input.Description, 4000, "Description of team duties", false);
        if (input.Id == Guid.Empty || input.Members is null || input.Members.Length > 200) throw new OperationsException("Team members are not valid.");
        var members = input.Members.Concat(input.LeadId is { } lead ? new[] { lead } : []).Distinct().ToArray();
        await using var db = await dataSource.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var valid = await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM app_user WHERE tenant_id=@tenant AND id=ANY(@members) AND enabled AND role IN ('admin','cx_analyst','cx_lead','support_supervisor')", new { tenant, members }, tx, cancellationToken: ct));
        if (valid != members.Length) throw new OperationsException("Select active members of the same organization.");
        if (await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM cx_case WHERE tenant_id=@tenant AND team_id=@id AND status NOT IN ('resolved','closed')
              AND assignee_id IS NOT NULL AND NOT (assignee_id=ANY(@members)))
            """, new { tenant, input.Id, members }, tx, cancellationToken: ct))) throw new OperationsException("Before removing the member, refer his open files to another member.", 409);
        var changed = await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_team(id,tenant_id,name,lead_id,enabled,description) VALUES(@id,@tenant,@name,@leadId,@enabled,@description)
            ON CONFLICT(id) DO UPDATE SET name=@name,lead_id=@leadId,enabled=@enabled,description=@description WHERE cx_team.tenant_id=@tenant
            """, new { input.Id, tenant, name = input.Name.Trim(), input.LeadId, input.Enabled, description = input.Description?.Trim() ?? "" }, tx, cancellationToken: ct));
        if (changed == 0) throw new OperationsException("The team is not valid.", 404);
        await db.ExecuteAsync(new CommandDefinition("DELETE FROM cx_team_member WHERE tenant_id=@tenant AND team_id=@id", new { tenant, input.Id }, tx, cancellationToken: ct));
        foreach (var member in members) await db.ExecuteAsync(new CommandDefinition(
            "INSERT INTO cx_team_member(tenant_id,team_id,user_id) VALUES(@tenant,@id,@member)", new { tenant, input.Id, member }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        await AuditConfigurationAsync(tenant, actor, "cx_team", input.Id, input, ct);
    }
    public async Task<IReadOnlyList<SlaPolicy>> PoliciesAsync(string tenant, CancellationToken ct)
    {
        await EnsureDefaultsAsync(tenant, ct);
        await using var db = await dataSource.OpenAsync(ct);
        return (await db.QueryAsync<SlaPolicy>(new CommandDefinition(
            "SELECT *,calendar::text AS calendar_json FROM cx_sla_policy WHERE tenant_id=@tenant ORDER BY response_minutes", new { tenant }, cancellationToken: ct))).AsList();
    }
    public async Task SavePolicyAsync(string tenant, string actor, SlaPolicyInput input, CancellationToken ct)
    {
        Text(input.Name, 128, "Policy name"); Priority(input.Priority); BusinessClock.Validate(input.Calendar);
        if (input.Id == Guid.Empty || input.ResponseMinutes is < 1 or > 525600 || input.ResolutionMinutes < input.ResponseMinutes || input.ResolutionMinutes > 525600)
            throw new OperationsException("The solution deadline must be at least equal to the response deadline and a maximum of 525,600 working minutes.");
        await using var db = await dataSource.OpenAsync(ct);
        var changed = await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_sla_policy(id,tenant_id,name,priority,response_minutes,resolution_minutes,calendar,enabled)
            VALUES(@id,@tenant,@name,@priority,@responseMinutes,@resolutionMinutes,@calendar::jsonb,@enabled)
            ON CONFLICT(id) DO UPDATE SET name=@name,priority=@priority,response_minutes=@responseMinutes,
              resolution_minutes=@resolutionMinutes,calendar=@calendar::jsonb,enabled=@enabled WHERE cx_sla_policy.tenant_id=@tenant
            """, new { input.Id, tenant, name = input.Name.Trim(), input.Priority, input.ResponseMinutes, input.ResolutionMinutes, calendar = Json(input.Calendar), input.Enabled }, cancellationToken: ct));
        if (changed == 0) throw new OperationsException("Policy not found.", 404);
        await AuditConfigurationAsync(tenant, actor, "cx_sla_policy", input.Id, input, ct);
    }
    public async Task<IReadOnlyList<AutomationRule>> RulesAsync(string tenant, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        return (await db.QueryAsync<AutomationRule>(new CommandDefinition("SELECT * FROM cx_automation_rule WHERE tenant_id=@tenant ORDER BY name", new { tenant }, cancellationToken: ct))).AsList();
    }
    public async Task SaveRuleAsync(string tenant, string actor, RuleInput input, CancellationToken ct)
    {
        Text(input.Name, 128, "The name of the rule"); Text(input.Topic, 128, "Topic", false); Text(input.Channel, 64, "Channel", false); Priority(input.Priority);
        if (input.Id == Guid.Empty || input.RepeatCount is < 1 or > 1000 || input.Severity is not null && !Priorities.Contains(input.Severity)) throw new OperationsException("The condition of the rule is not valid.");
        await using var db = await dataSource.OpenAsync(ct);
        await ValidateAssignmentAsync(db, null, tenant, input.TeamId, input.AssigneeId, ct);
        var changed = await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_automation_rule(id,tenant_id,name,topic,severity,channel,repeat_count,priority,team_id,assignee_id,create_case,notify,enabled)
            VALUES(@id,@tenant,@name,@topic,@severity,@channel,@repeatCount,@priority,@teamId,@assigneeId,@createCase,@notify,@enabled)
            ON CONFLICT(id) DO UPDATE SET name=@name,topic=@topic,severity=@severity,channel=@channel,repeat_count=@repeatCount,
              priority=@priority,team_id=@teamId,assignee_id=@assigneeId,create_case=@createCase,notify=@notify,
              created_at=CASE WHEN @enabled AND NOT cx_automation_rule.enabled THEN now() ELSE cx_automation_rule.created_at END,
              enabled=@enabled WHERE cx_automation_rule.tenant_id=@tenant
            """, new { input.Id, tenant, name = input.Name.Trim(), input.Topic, input.Severity, input.Channel, input.RepeatCount, input.Priority, input.TeamId, input.AssigneeId, input.CreateCase, input.Notify, input.Enabled }, cancellationToken: ct));
        if (changed == 0) throw new OperationsException("The rule was not found.", 404);
        await AuditConfigurationAsync(tenant, actor, "cx_automation_rule", input.Id, input, ct);
    }
    public async Task<IReadOnlyList<object>> ExecutionsAsync(string tenant, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        return (await db.QueryAsync(new CommandDefinition("""
            SELECT e.rule_id,r.name AS rule_name,e.feedback_id,e.case_id,e.state,e.error,e.executed_at
            FROM cx_automation_execution e JOIN cx_automation_rule r ON r.tenant_id=e.tenant_id AND r.id=e.rule_id
            WHERE e.tenant_id=@tenant ORDER BY executed_at DESC LIMIT 100
            """, new { tenant }, cancellationToken: ct))).Select(row => (object)row).ToList();
    }
    public async Task SaveProblemAsync(string tenant, string actor, ProblemInput input, CancellationToken ct)
    {
        Text(input.Title, 256, "Title of the problem"); Text(input.Topic, 128, "Topic", false);
        Text(input.RootCause, 12000, "The main cause", input.Status is "fixed" or "closed");
        Text(input.Improvement, 12000, "Corrective action", input.Status is "fixed" or "closed");
        if (input.Id == Guid.Empty || !new[] { "open", "investigating", "fixed", "closed" }.Contains(input.Status)) throw new OperationsException("The problem is not valid.");
        await using var db = await dataSource.OpenAsync(ct);
        await ValidateAssignmentAsync(db, null, tenant, null, input.OwnerId, ct);
        var changed = await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_problem(id,tenant_id,title,topic,root_cause,improvement,status,owner_id,fixed_at)
            SELECT @id,@tenant,@title,@topic,@rootCause,@improvement,@status,@ownerId,
              CASE WHEN @status IN ('fixed','closed') THEN @now ELSE NULL END WHERE @version=0
            ON CONFLICT(id) DO UPDATE SET title=@title,topic=@topic,root_cause=@rootCause,improvement=@improvement,status=@status,owner_id=@ownerId,
              fixed_at=CASE WHEN @status IN ('fixed','closed') THEN COALESCE(cx_problem.fixed_at,@now) ELSE NULL END,version=cx_problem.version+1
            WHERE cx_problem.tenant_id=@tenant AND cx_problem.version=@version;
            UPDATE cx_problem SET title=@title,topic=@topic,root_cause=@rootCause,improvement=@improvement,status=@status,owner_id=@ownerId,
              fixed_at=CASE WHEN @status IN ('fixed','closed') THEN COALESCE(fixed_at,@now) ELSE NULL END,version=version+1
            WHERE tenant_id=@tenant AND id=@id AND @version>0 AND version=@version
            """, new { tenant, input.Id, title = input.Title.Trim(), input.Topic, input.RootCause, input.Improvement, input.Status, input.OwnerId, input.Version, now = Now }, cancellationToken: ct));
        if (changed == 0) throw new OperationsException("The problem has changed; Refresh the information.", 409);
        await AuditConfigurationAsync(tenant, actor, "cx_problem", input.Id, input, ct);
    }
    public async Task SaveJourneyAsync(string tenant, string actor, JourneyInput input, CancellationToken ct)
    {
        Text(input.Name, 128, "The name of the journey");
        Text(input.Persona, 4000, "Customer persona", false);
        Text(input.Scenario, 4000, "Travel scenario", false);
        Text(input.Goal, 4000, "The purpose of the map", false);
        if (input.MapType is not ("current_state" or "future_state")) throw new OperationsException("The map type is not valid.");
        if (input.Id == Guid.Empty || input.WindowDays is < 1 or > 365 || input.Stages is not { Length: >= 2 and <= 12 }) throw new OperationsException("The journey should have 2 to 12 stages and a period of 1 to 365 days..");
        foreach (var stage in input.Stages)
        {
            if (stage is null || stage.Experience is null) throw new OperationsException("Step information is not valid.");
            Text(stage.Title, 128, "Step title");
            if (stage.EventNames is not { Length: <= 30 } || stage.EventNames.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 128)) throw new OperationsException("The name of the events attached to the step is not valid.");
            var experience = stage.Experience;
            foreach (var text in new[] { experience.Goal, experience.Actions, experience.Touchpoints, experience.Thoughts, experience.Feeling,
                experience.PainPoints, experience.Opportunities, experience.Evidence, experience.SuccessMetric, experience.Owner })
                Text(text, 4000, "Description of customer experience", false);
            if (experience.Emotion is not ("unknown" or "negative" or "neutral" or "mixed" or "positive")) throw new OperationsException("Step sense is not valid.");
            if (experience.EvidenceStatus is not ("hypothesis" or "researched")) throw new OperationsException("The status of the evidence is not valid.");
            if (experience.EvidenceStatus == "researched") Text(experience.Evidence, 4000, "Source of evidence");
        }
        var events = input.Stages.SelectMany(stage => stage.EventNames).ToArray();
        if (events.Select(name => name.Trim()).Distinct(StringComparer.Ordinal).Count() != events.Length) throw new OperationsException("Each event can only belong to one phase.");
        await using var db = await dataSource.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var changed = await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_journey(id,tenant_id,name,window_days,enabled,persona,scenario,goal,map_type)
            VALUES(@id,@tenant,@name,@windowDays,@enabled,@persona,@scenario,@goal,@mapType)
            ON CONFLICT(id) DO UPDATE SET name=@name,window_days=@windowDays,enabled=@enabled,
              persona=@persona,scenario=@scenario,goal=@goal,map_type=@mapType WHERE cx_journey.tenant_id=@tenant
            """, new { tenant, input.Id, name = input.Name.Trim(), input.WindowDays, input.Enabled, input.Persona, input.Scenario, input.Goal, input.MapType }, tx, cancellationToken: ct));
        if (changed == 0) throw new OperationsException("Trip not found.", 404);
        await db.ExecuteAsync(new CommandDefinition("DELETE FROM cx_journey_stage WHERE tenant_id=@tenant AND journey_id=@id", new { tenant, input.Id }, tx, cancellationToken: ct));
        for (var index = 0; index < input.Stages.Length; index++) await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_journey_stage(id,tenant_id,journey_id,title,position,event_names,experience)
            VALUES(@stageId,@tenant,@id,@title,@index,@events,@experience::jsonb)
            """, new { stageId = Guid.NewGuid(), tenant, input.Id, title = input.Stages[index].Title.Trim(), index,
                events = input.Stages[index].EventNames.Select(name => name.Trim()).ToArray(), experience = Json(input.Stages[index].Experience) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        await AuditConfigurationAsync(tenant, actor, "cx_journey", input.Id, input, ct);
    }
    public async Task<IReadOnlyList<JourneyDefinition>> JourneysAsync(string tenant, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        var journeys = (await db.QueryAsync<JourneyDefinition>(new CommandDefinition("SELECT * FROM cx_journey WHERE tenant_id=@tenant ORDER BY name", new { tenant }, cancellationToken: ct))).AsList();
        foreach (var journey in journeys)
        {
            var rows = await db.QueryAsync<JourneyStageRow>(new CommandDefinition("SELECT title,event_names,experience::text FROM cx_journey_stage WHERE tenant_id=@tenant AND journey_id=@id ORDER BY position", new { tenant, journey.Id }, cancellationToken: ct));
            journey.Stages = rows.Select(row => new JourneyStageInput(row.Title, row.EventNames)
            { Experience = JsonSerializer.Deserialize<JourneyExperience>(row.Experience, CxJson.Options) ?? new() }).ToArray();
        }
        return journeys;
    }
    private sealed class JourneyStageRow
    {
        public string Title { get; set; } = "";
        public string[] EventNames { get; set; } = [];
        public string Experience { get; set; } = "{}";
    }
}
