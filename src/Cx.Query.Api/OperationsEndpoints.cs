using Cx.Application.Abstractions;
using Cx.Application.Operations;
using Cx.Contracts.Security;
using Cx.Infrastructure.Operations;
using Cx.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Cx.Query.Api;

public static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var ops = app.MapGroup("/v1/operations").WithTags("Operational CX").RequireAuthorization(CxPermissions.WorkRead).AddEndpointFilter<OperationsErrorFilter>();
        ops.MapGet("/directory", async (HttpContext ctx, OperationsStore store, UserAccounts users, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            return Results.Ok(new { teams = await store.TeamsAsync(user.TenantId, ct), staff = await users.ListStaffAsync(user.TenantId, ct),
                current_staff_id = await store.StaffIdAsync(user.TenantId, user.Subject, ct) });
        });
        ops.MapGet("/cases", async (HttpContext ctx, OperationsStore store, [FromQuery] string? customerId, [FromQuery] string? status,
            [FromQuery] string? q, [FromQuery] Guid? teamId, [FromQuery] bool? mine, [FromQuery] bool? overdue, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            return Results.Ok(await store.CasesAsync(user.TenantId, await store.StaffIdAsync(user.TenantId, user.Subject, ct),
                user.Has(CxPermissions.OperationsConfigure), customerId, status, q, teamId, mine == true, overdue == true, page ?? 1, ct, pageSize ?? 20));
        });
        ops.MapGet("/cases/{id:guid}", async (HttpContext ctx, Guid id, OperationsStore store, CancellationToken ct) =>
        {
            await AccessAsync(ctx, store, id, ct);
            var result = await store.DetailAsync(ctx.ToCxUser().TenantId, id, null, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
        ops.MapGet("/cases/by-feedback/{feedback:guid}", async (HttpContext ctx, Guid feedback, OperationsStore store, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser(); var id = await store.CaseForFeedbackAsync(user.TenantId, feedback, ct);
            if (id is not null) await AccessAsync(ctx, store, id.Value, ct);
            return Results.Ok(new { id });
        });
        ops.MapPost("/cases", async (HttpContext ctx, CreateCaseInput body, OperationsStore store, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            var staff = await store.StaffIdAsync(user.TenantId, user.Subject, ct);
            if (!user.Has(CxPermissions.OperationsConfigure))
            {
                if (body.AssigneeId is not null && body.AssigneeId != staff || body.TeamId is not null || body.ProblemId is not null)
                    return Results.Forbid();
                body = body with { AssigneeId = staff };
            }
            var id = await store.CreateAsync(user.TenantId, user.Subject, body, ct);
            await AccessAsync(ctx, store, id, ct);
            return Results.Ok(new { id });
        }).RequireAuthorization(CxPermissions.WorkManage);
        ops.MapPut("/cases/{id:guid}", async (HttpContext ctx, Guid id, UpdateCaseInput body, OperationsStore store, CancellationToken ct) =>
        {
            await AccessAsync(ctx, store, id, ct);
            var user = ctx.ToCxUser();
            if (!user.Has(CxPermissions.OperationsConfigure))
            {
                var item = (await store.DetailAsync(user.TenantId, id, null, ct))!.Case;
                var own = await store.StaffIdAsync(user.TenantId, user.Subject, ct);
                if (body.TeamId != item.TeamId || body.AssigneeId != item.AssigneeId && body.AssigneeId != own ||
                    body.ProblemId != item.ProblemId || body.Priority is not null && body.Priority != item.Priority) return Results.Forbid();
            }
            await store.UpdateAsync(user.TenantId, user.Subject, id, body, ct);
            return Results.NoContent();
        }).RequireAuthorization(CxPermissions.WorkManage);
        ops.MapPost("/cases/{id:guid}/messages", async (HttpContext ctx, Guid id, CaseMessageInput body, OperationsStore store, CancellationToken ct) =>
        {
            await AccessAsync(ctx, store, id, ct);
            var user = ctx.ToCxUser(); await store.MessageAsync(user.TenantId, user.Subject, id, body, null, ct); return Results.NoContent();
        }).RequireAuthorization(CxPermissions.WorkManage);
        ops.MapPost("/cases/{id:guid}/workflow", async (HttpContext ctx, Guid id, CaseWorkflowInput body, OperationsStore store, ICaseRouter router, CancellationToken ct) =>
        {
            await AccessAsync(ctx, store, id, ct);
            var user = ctx.ToCxUser();
            CaseRoute? route = null;
            if (body.Action == "start")
            {
                var item = (await store.DetailAsync(user.TenantId, id, null, ct))!.Case;
                if (item.Version != body.Version || item.StageIndex >= 0 || item.Status is "closed" or "resolved")
                    throw new OperationsException("the case has changed or its proceedings have already started; Refresh the information.", 409);
                route = await router.DecideAsync(item, await store.TeamsAsync(user.TenantId, ct), await store.RoutingWorkflowsAsync(user.TenantId, item.Topic, ct), ct);
            }
            var staff = await store.StaffIdAsync(user.TenantId, user.Subject, ct);
            var manager = user.Has(CxPermissions.OperationsConfigure);
            await store.HandleWorkflowAsync(user.TenantId, user.Subject, id, body, staff, manager, route, ct);
            return Results.Ok(new { can_access = await store.CanAccessAsync(user.TenantId, id, staff, manager, ct) });
        }).RequireAuthorization(CxPermissions.WorkManage);
        ops.MapPost("/cases/{id:guid}/attachments", async (HttpRequest request, Guid id, OperationsStore store, IObjectStorage storage, CancellationToken ct) =>
        {
            await AccessAsync(request.HttpContext, store, id, ct);
            var user = request.HttpContext.ToCxUser(); return await OperationsHttp.UploadAsync(request, store, storage, user.TenantId, user.Subject, id, null, ct);
        }).RequireAuthorization(CxPermissions.WorkManage).DisableAntiforgery();
        ops.MapGet("/cases/{caseId:guid}/attachments/{id:guid}", async (HttpContext ctx, Guid caseId, Guid id, OperationsStore store, IObjectStorage storage, CancellationToken ct) =>
        {
            await AccessAsync(ctx, store, caseId, ct);
            return await OperationsHttp.DownloadAsync(store, storage, ctx.ToCxUser().TenantId, caseId, id, null, ct);
        });
        ops.MapGet("/notifications", async (HttpContext ctx, OperationsStore store, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser(); var id = await store.StaffIdAsync(user.TenantId, user.Subject, ct);
            return Results.Ok(id is null ? [] : await store.NotificationsAsync(user.TenantId, "staff", id.Value.ToString(), ct));
        });
        ops.MapPost("/notifications/{id:guid}/read", async (HttpContext ctx, Guid id, OperationsStore store, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser(); var staff = await store.StaffIdAsync(user.TenantId, user.Subject, ct);
            if (staff is not null) await store.ReadNotificationAsync(user.TenantId, "staff", staff.Value.ToString(), id, ct);
            return Results.NoContent();
        });
        ops.MapGet("/notifications/unread-count", async (HttpContext ctx, OperationsStore store, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser(); var id = await store.StaffIdAsync(user.TenantId, user.Subject, ct);
            return Results.Ok(new { count = id is null ? 0 : await store.UnreadNotificationsAsync(user.TenantId, "staff", id.Value.ToString(), ct) });
        });
        ops.MapGet("/notifications/grid", async (HttpContext ctx, OperationsStore store, [FromQuery] string? q, [FromQuery] string? status,
            [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser(); var id = await store.StaffIdAsync(user.TenantId, user.Subject, ct);
            return Results.Ok(await store.NotificationGridAsync(user.TenantId, "staff", id?.ToString() ?? "", q, status, page ?? 1, pageSize ?? 20, ct));
        });
        ops.MapGet("/follow-up-customers", async (HttpContext ctx, OperationsStore store, [FromQuery] string? segment, [FromQuery] int? minScore, CancellationToken ct) =>
            Results.Ok(await store.FollowUpAsync(ctx.ToCxUser().TenantId, segment, minScore ?? 1, ct)));
        ops.MapGet("/outcomes", async (HttpContext ctx, OperationsStore store, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct) =>
        { var range = OperationsHttp.Range(from, to); return Results.Ok(await store.OutcomesAsync(ctx.ToCxUser().TenantId, range.From, range.To, ct)); });
        ops.MapGet("/problems", async (HttpContext ctx, OperationsStore store, CancellationToken ct) => Results.Ok(await store.ProblemsAsync(ctx.ToCxUser().TenantId, ct)));
        ops.MapGet("/journeys", async (HttpContext ctx, OperationsStore store, CancellationToken ct) => Results.Ok(await store.JourneysAsync(ctx.ToCxUser().TenantId, ct)));
        ops.MapGet("/journeys/{id:guid}/funnel", async (HttpContext ctx, Guid id, OperationsStore store, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct) =>
        { var range = OperationsHttp.Range(from, to); return Results.Ok(await store.FunnelAsync(ctx.ToCxUser().TenantId, id, range.From, range.To, ct)); });
        ops.MapGet("/policies", async (HttpContext ctx, OperationsStore store, CancellationToken ct) => Results.Ok(await store.PoliciesAsync(ctx.ToCxUser().TenantId, ct)));
        ops.MapGet("/workflows", async (HttpContext ctx, OperationsStore store, CancellationToken ct) => Results.Ok(await store.WorkflowsAsync(ctx.ToCxUser().TenantId, ct)));
        ops.MapPut("/workflows", async (HttpContext ctx, WorkflowInput body, OperationsStore store, CancellationToken ct) =>
        { var user = ctx.ToCxUser(); await store.SaveWorkflowAsync(user.TenantId, user.Subject, body, ct); return Results.NoContent(); }).RequireAuthorization(CxPermissions.OperationsConfigure);
        ops.MapGet("/rules", async (HttpContext ctx, OperationsStore store, CancellationToken ct) => Results.Ok(await store.RulesAsync(ctx.ToCxUser().TenantId, ct))).RequireAuthorization(CxPermissions.OperationsConfigure);
        ops.MapGet("/executions", async (HttpContext ctx, OperationsStore store, CancellationToken ct) => Results.Ok(await store.ExecutionsAsync(ctx.ToCxUser().TenantId, ct))).RequireAuthorization(CxPermissions.OperationsConfigure);
        ops.MapGet("/campaigns", async (HttpContext ctx, OperationsStore store, CancellationToken ct) => Results.Ok(await store.CampaignsAsync(ctx.ToCxUser().TenantId, ct))).RequireAuthorization(CxPermissions.OperationsConfigure);
        ops.MapPut("/teams", async (HttpContext ctx, TeamInput body, OperationsStore store, CancellationToken ct) =>
        { var user = ctx.ToCxUser(); await store.SaveTeamAsync(user.TenantId, user.Subject, body, ct); return Results.NoContent(); }).RequireAuthorization(CxPermissions.TeamManage);
        ops.MapPut("/staff", async (HttpContext ctx, StaffAccountInput body, UserAccounts users, CancellationToken ct) =>
        { var user = ctx.ToCxUser(); await users.SaveStaffAsync(user.TenantId, user.Subject, body, ct); return Results.NoContent(); }).RequireAuthorization(CxPermissions.TeamManage);
        ops.MapPut("/policies", async (HttpContext ctx, SlaPolicyInput body, OperationsStore store, CancellationToken ct) =>
        { var user = ctx.ToCxUser(); await store.SavePolicyAsync(user.TenantId, user.Subject, body, ct); return Results.NoContent(); }).RequireAuthorization(CxPermissions.OperationsConfigure);
        ops.MapPut("/rules", async (HttpContext ctx, RuleInput body, OperationsStore store, CancellationToken ct) =>
        { var user = ctx.ToCxUser(); await store.SaveRuleAsync(user.TenantId, user.Subject, body, ct); return Results.NoContent(); }).RequireAuthorization(CxPermissions.OperationsConfigure);
        ops.MapPut("/problems", async (HttpContext ctx, ProblemInput body, OperationsStore store, CancellationToken ct) =>
        { var user = ctx.ToCxUser(); await store.SaveProblemAsync(user.TenantId, user.Subject, body, ct); return Results.NoContent(); }).RequireAuthorization(CxPermissions.OperationsConfigure);
        ops.MapPut("/campaigns", async (HttpContext ctx, CampaignInput body, OperationsStore store, CancellationToken ct) =>
        { var user = ctx.ToCxUser(); await store.SaveCampaignAsync(user.TenantId, user.Subject, body, ct); return Results.NoContent(); }).RequireAuthorization(CxPermissions.OperationsConfigure);
        ops.MapPost("/campaigns/{id:guid}/dispatch", async (HttpContext ctx, Guid id, DispatchInput body, OperationsStore store, CancellationToken ct) =>
        { var user = ctx.ToCxUser(); return Results.Ok(await store.DispatchCampaignAsync(user.TenantId, user.Subject, id, body.Customers, ct)); }).RequireAuthorization(CxPermissions.OperationsConfigure);
        ops.MapPut("/journeys", async (HttpContext ctx, JourneyInput body, OperationsStore store, CancellationToken ct) =>
        { var user = ctx.ToCxUser(); await store.SaveJourneyAsync(user.TenantId, user.Subject, body, ct); return Results.NoContent(); }).RequireAuthorization(CxPermissions.OperationsConfigure);
        return app;
    }
    private sealed record DispatchInput(string[] Customers);
    private static async Task AccessAsync(HttpContext ctx, OperationsStore store, Guid id, CancellationToken ct)
    {
        var user = ctx.ToCxUser();
        if (!await store.CanAccessAsync(user.TenantId, id, await store.StaffIdAsync(user.TenantId, user.Subject, ct), user.Has(CxPermissions.OperationsConfigure), ct))
            throw new OperationsException("The file could not be accessed.", 404);
    }
}
