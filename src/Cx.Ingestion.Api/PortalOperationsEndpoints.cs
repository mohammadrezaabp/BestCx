using System.Security.Claims;
using Cx.Application.Abstractions;
using Cx.Application.Operations;
using Cx.Infrastructure.Operations;
using Cx.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Cx.Ingestion.Api;

public static class PortalOperationsEndpoints
{
    public static void MapPortalOperations(this RouteGroupBuilder customer)
    {
        var ops = customer.MapGroup("").AddEndpointFilter<OperationsErrorFilter>();
        ops.MapGet("/cases", async (HttpContext ctx, OperationsStore store, [FromQuery] int? page, CancellationToken ct) =>
        { var (tenant, id) = Identity(ctx); var result = await store.PortalCasesAsync(tenant, id, page ?? 1, ct); return Results.Ok(new { items = result.Items.Select(PublicCase), result.Total, result.Page, page_size = 20 }); });
        ops.MapGet("/history", async (HttpContext ctx, OperationsStore store, [FromQuery] int? page, CancellationToken ct) =>
        { var (tenant, id) = Identity(ctx); return Results.Ok(await store.PortalFeedbackAsync(tenant, id, page ?? 1, ct)); });
        ops.MapGet("/cases/{id:guid}", async (HttpContext ctx, Guid id, OperationsStore store, CancellationToken ct) =>
        {
            var (tenant, customerId) = Identity(ctx);
            var detail = await store.DetailAsync(tenant, id, customerId, ct);
            return detail is null ? Results.NotFound() : Results.Ok(new { @case = PublicCase(detail.Case),
                activities = detail.Activities.Select(activity => new { activity.Id, activity.Kind, actor = activity.Actor.StartsWith("customer:", StringComparison.Ordinal) ? "You" : "Agent", activity.Text, activity.CreatedAt }),
                attachments = detail.Attachments.Select(attachment => new { attachment.Id, attachment.Filename, attachment.SizeBytes, attachment.CreatedAt }) });
        });
        ops.MapPost("/cases/{id:guid}/messages", async (HttpContext ctx, Guid id, CaseMessageInput body, OperationsStore store, CancellationToken ct) =>
        { var (tenant, customerId) = Identity(ctx); await store.MessageAsync(tenant, OperationsStore.CustomerActor(customerId), id, body, customerId, ct); return Results.NoContent(); });
        ops.MapPost("/cases/{id:guid}/action", async (HttpContext ctx, Guid id, CustomerCaseAction body, OperationsStore store, CancellationToken ct) =>
        { var (tenant, customerId) = Identity(ctx); await store.CustomerActionAsync(tenant, customerId, id, body, ct); return Results.NoContent(); });
        ops.MapPost("/cases/{id:guid}/attachments", async (HttpRequest request, Guid id, OperationsStore store, IObjectStorage storage, CancellationToken ct) =>
        {
            var (tenant, customerId) = Identity(request.HttpContext);
            if (await store.DetailAsync(tenant, id, customerId, ct) is null) return Results.NotFound();
            return await OperationsHttp.UploadAsync(request, store, storage, tenant, OperationsStore.CustomerActor(customerId), id, customerId, ct);
        }).DisableAntiforgery();
        ops.MapGet("/cases/{caseId:guid}/attachments/{id:guid}", async (HttpContext ctx, Guid caseId, Guid id, OperationsStore store, IObjectStorage storage, CancellationToken ct) =>
        { var (tenant, customerId) = Identity(ctx); return await OperationsHttp.DownloadAsync(store, storage, tenant, caseId, id, customerId, ct); });
        ops.MapGet("/surveys", async (HttpContext ctx, OperationsStore store, CancellationToken ct) =>
        { var (tenant, customerId) = Identity(ctx); return Results.Ok(await store.InvitationsAsync(tenant, customerId, ct)); });
        ops.MapGet("/surveys/{id:guid}", async (HttpContext ctx, Guid id, OperationsStore store, CancellationToken ct) =>
        { var (tenant, customerId) = Identity(ctx); var result = await store.InvitationAsync(tenant, customerId, id, ct); return result is null ? Results.NotFound(new { error = "Survey invitation not found." }) : Results.Ok(result); });
        ops.MapPost("/surveys/{id:guid}/answer", async (HttpContext ctx, Guid id, SurveyAnswerInput body, OperationsStore store, CancellationToken ct) =>
        { var (tenant, customerId) = Identity(ctx); await store.AnswerAsync(tenant, customerId, id, body, ct); return Results.NoContent(); });
        ops.MapPost("/surveys/{id:guid}/decline", async (HttpContext ctx, Guid id, OperationsStore store, CancellationToken ct) =>
        { var (tenant, customerId) = Identity(ctx); await store.DeclineAsync(tenant, customerId, id, ct); return Results.NoContent(); });
        ops.MapGet("/notifications", async (HttpContext ctx, OperationsStore store, CancellationToken ct) =>
        { var (tenant, customerId) = Identity(ctx); return Results.Ok(await store.NotificationsAsync(tenant, "customer", customerId, ct)); });
        ops.MapPost("/notifications/{id:guid}/read", async (HttpContext ctx, Guid id, OperationsStore store, CancellationToken ct) =>
        { var (tenant, customerId) = Identity(ctx); await store.ReadNotificationAsync(tenant, "customer", customerId, id, ct); return Results.NoContent(); });
    }
    private static (string Tenant, string Customer) Identity(HttpContext ctx) =>
        (ctx.User.FindFirst(CxClaimTypes.TenantId)!.Value, ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    private static object PublicCase(WorkCase item) => new { item.Id, item.Reference, item.Title, item.Priority, item.Status, item.Version,
        item.CreatedAt, item.UpdatedAt, item.ResolutionText, item.ReopenCount };
}
