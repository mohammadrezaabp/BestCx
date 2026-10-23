using Cx.Application.Operations;
using Dapper;

namespace Cx.Infrastructure.Operations;

public sealed partial class OperationsStore
{
    public async Task<IReadOnlyList<WorkflowDefinition>> WorkflowsAsync(string tenant, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        return (await db.QueryAsync<WorkflowDefinition>(new CommandDefinition(
            "SELECT *,stages::text AS stages_json FROM cx_workflow WHERE tenant_id=@tenant ORDER BY name,id", new { tenant }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<WorkflowDefinition>> RoutingWorkflowsAsync(string tenant, string? topic, CancellationToken ct)
    {
        await using var db = await dataSource.OpenAsync(ct);
        return (await db.QueryAsync<WorkflowDefinition>(new CommandDefinition("""
            WITH RECURSIVE ancestors AS (
              SELECT id,code,parent_id FROM topic WHERE tenant_id=@tenant AND code=@topic
              UNION SELECT p.id,p.code,p.parent_id FROM topic p JOIN ancestors a ON p.id=a.parent_id WHERE p.tenant_id=@tenant
            ) SELECT w.*,w.stages::text AS stages_json FROM cx_workflow w
            WHERE w.tenant_id=@tenant AND w.enabled AND (w.topic IS NULL OR w.topic=@topic OR w.topic IN (SELECT code FROM ancestors))
            ORDER BY w.name,w.id
            """, new { tenant, topic }, cancellationToken: ct))).AsList();
    }

    public async Task SaveWorkflowAsync(string tenant, string actor, WorkflowInput input, CancellationToken ct)
    {
        Text(input.Name, 128, "Name of the procedure"); Text(input.Description, 4000, "Description of the procedure", false); Text(input.Topic, 128, "Topic", false);
        if (input.Id == Guid.Empty || input.Stages is not { Length: >= 1 and <= 20 }) throw new OperationsException("The procedure should have 1 to 20 steps.");
        foreach (var stage in input.Stages) { if (stage is null) throw new OperationsException("The step is not valid."); Text(stage.Title, 128, "Step title"); Text(stage.Description, 4000, "Step description", false); }
        await using var db = await dataSource.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        foreach (var team in input.Stages.Select(stage => stage.TeamId).Distinct()) await ValidateAssignmentAsync(db, tx, tenant, team, null, ct);
        var changed = await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cx_workflow(id,tenant_id,name,description,topic,stages,enabled)
            VALUES(@id,@tenant,@name,@description,@topic,@stages::jsonb,@enabled)
            ON CONFLICT(id) DO UPDATE SET name=@name,description=@description,topic=@topic,stages=@stages::jsonb,
              enabled=@enabled,version=cx_workflow.version+1,updated_at=@now
            WHERE cx_workflow.tenant_id=@tenant AND cx_workflow.version=@version
            """, new { input.Id, tenant, name = input.Name.Trim(), description = input.Description?.Trim() ?? "", input.Topic,
                stages = Json(input.Stages.Select(stage => stage with { Title = stage.Title.Trim(), Description = stage.Description?.Trim() ?? "" })), input.Enabled, input.Version, now = Now }, tx, cancellationToken: ct));
        if (changed == 0) throw new OperationsException("The procedure has changed; Refresh the information.", 409);
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_log(tenant_id,actor_id,action,resource_type,resource_id,after)
            VALUES(@tenant,@actor,'operations.configure','cx_workflow',@id::text,@value::jsonb)
            """, new { tenant, actor, input.Id, value = Json(input) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task HandleWorkflowAsync(string tenant, string actor, Guid id, CaseWorkflowInput input,
        Guid? staff, bool manager, CaseRoute? route, CancellationToken ct)
    {
        if (input.Action is not ("start" or "next" or "close")) throw new OperationsException("The proceedings are not valid.");
        Text(input.Text, 12000, "Description of handling", input.Action != "start");
        var item = (await DetailAsync(tenant, id, null, ct))?.Case ?? throw new OperationsException("File not found.", 404);
        if (item.Version != input.Version) throw new OperationsException("The file has changed; Refresh the information.", 409);
        if (item.Status is "closed" or "resolved") throw new OperationsException("The proceedings of this case have ended.", 409);
        var snapshot = item.Workflow;
        var index = item.StageIndex;
        var workflowId = item.WorkflowId;
        var reason = item.RoutingReason;
        var model = item.RoutingModel;
        Guid? team = item.TeamId, assignee = item.AssigneeId;
        string activity;
        if (input.Action == "start")
        {
            if (index >= 0) throw new OperationsException("Proceedings have already begun.", 409);
            if (route is null || route.Workflow.Stages.Length == 0) throw new OperationsException("The team and procedure are not specified.");
            snapshot = route.Workflow; workflowId = route.WorkflowId; reason = route.Reason; model = route.Model; index = 0;
            team = snapshot.Stages[0].TeamId;
            if (team is null) throw new OperationsException("The team responsible for the stage is not specified.");
            assignee = item.TeamId == team ? item.AssigneeId : null;
            activity = $"The beginning of the procedure «{snapshot.Name}»; stage «{snapshot.Stages[0].Title}»\n{reason}";
        }
        else if (input.Action == "next")
        {
            if (index < 0 || index + 1 >= snapshot.Stages.Length) throw new OperationsException("There is no next step; Record the end of proceedings.");
            var previous = snapshot.Stages[index].Title;
            index++;
            team = snapshot.Stages[index].TeamId;
            if (team is null) throw new OperationsException("The team responsible for the next stage has not been determined.");
            assignee = team == item.TeamId ? item.AssigneeId : null;
            activity = $"The end of the stage «{previous}» And reference to step «{snapshot.Stages[index].Title}»";
        }
        else
        {
            activity = $"The end of proceedings{(index >= 0 ? $" In step «{snapshot.Stages[index].Title}»" : "")}";
        }
        if (assignee is null && staff is not null)
        {
            var teams = await TeamsAsync(tenant, ct);
            if (team is null || teams.Any(candidate => candidate.Id == team && candidate.Members.Contains(staff.Value))) assignee = staff;
        }
        var change = new WorkflowChange(workflowId, snapshot, index, reason, model, activity, staff, manager);
        await UpdateAsync(tenant, actor, id, new(input.Version, input.Action == "close" ? "resolved" : "in_progress", team, assignee,
            item.ProblemId, input.Action == "close" ? input.Text.Trim() : item.ResolutionText, item.Priority,
            input.Action == "start" ? null : input.Text.Trim()), ct, workflowChange: change);
    }
}
