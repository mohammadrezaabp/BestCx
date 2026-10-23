using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Jobs;
using Cx.Application.Options;
using Cx.Application.Query;
using Cx.Contracts;
using Cx.Contracts.Ai;
using Cx.Contracts.Events;
using Cx.Contracts.Security;
using Cx.Domain.Entities;
using Cx.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Cx.Admin.Api;

/// <summary>Operations surface: overview, topic tree, data quality, pipeline health, audit (spec 24, 29).</summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        MapOverview(app);
        app.MapTopicEndpoints();
        MapOperations(app);
        MapAudit(app);
        return app;
    }

    private static void MapOverview(IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/overview", async (HttpContext ctx, [FromQuery] int? days,
            IInsightsRepository insights, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            var window = Math.Clamp(days ?? 7, 1, 90);
            var to = DateTimeOffset.UtcNow;
            var from = to.AddDays(-window);

            return Results.Ok(await insights.GetOverviewAsync(user.TenantId, from, to, ct));
        })
        .WithTags("Overview")
        .RequireAuthorization(CxPermissions.CustomerRead)
        .WithName("GetOverview");
    }

    private static void MapOperations(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/v1/admin")
            .WithTags("Operations")
            .RequireAuthorization(CxPermissions.PipelineManage);

        admin.MapGet("/data-quality", async (HttpContext ctx, IInsightsRepository insights, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            var checks = await insights.GetDataQualityAsync(user.TenantId, ct);
            return Results.Ok(new { generated_at = DateTimeOffset.UtcNow, checks });
        }).WithName("GetDataQuality");

        admin.MapGet("/pipelines", async (HttpContext ctx, IInsightsRepository insights, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            return Results.Ok(await insights.GetPipelineHealthAsync(user.TenantId, ct));
        }).WithName("GetPipelineHealth");

        admin.MapGet("/integrations", async (HttpContext ctx, IIntegrationCheckpointRepository checkpoints,
            IVoipConnector voip, IAiCompletionClient ai, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            var stored = await checkpoints.ListAsync(user.TenantId, ct);

            var voipHealth = await voip.HealthCheckAsync(ct);
            var aiHealthy = await ai.HealthCheckAsync(ct);

            return Results.Ok(new
            {
                checkpoints = stored,
                live = new object[]
                {
                    new { name = voip.Provider, type = "voip", healthy = voipHealth.Healthy, detail = voipHealth.Detail },
                    new { name = ai.Provider, type = "ai", healthy = aiHealthy, detail = ai.DefaultModel }
                }
            });
        }).WithName("GetIntegrations");

        admin.MapGet("/jobs/failed", async (HttpContext ctx, [FromQuery] int? limit,
            IProcessingJobRepository jobs, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            return Results.Ok(await jobs.ListFailedAsync(user.TenantId, limit ?? 50, ct));
        }).WithName("ListFailedJobs");

        admin.MapPost("/reprocess", ReprocessAsync).WithName("AdminReprocess");
    }

    /// <summary>
    /// Bulk reprocess by entity list (spec 24, panel proposal 12).
    /// It enqueues new analysis versions; nothing existing is deleted or overwritten.
    /// </summary>
    private static async Task<IResult> ReprocessAsync(HttpContext ctx,
        [FromBody] AdminReprocessRequest body, IMessagePublisher publisher, IAiAnalysisRepository analyses,
        IAuditRepository audit, IOptions<KafkaOptions> kafka, IOptions<AiOptions> ai, CancellationToken ct)
    {
        var user = ctx.ToCxUser();
        if (!user.Has(CxPermissions.AiReprocess)) return Results.Forbid();

        if (body.EntityIds is null or { Count: 0 })
            return Results.BadRequest(new { error = "entity_ids is required" });

        // Guardrail: a bulk action needs a bound, and the operator must confirm (panel proposal 16).
        if (body.EntityIds.Count > 500)
            return Results.BadRequest(new { error = "a single reprocess request is limited to 500 entities" });

        if (!body.Confirm)
        {
            return Results.Ok(new
            {
                preview = true,
                entity_type = body.EntityType,
                count = body.EntityIds.Count,
                prompt_version = body.PromptVersion ?? ai.Value.PromptVersion,
                note = "set confirm=true to execute"
            });
        }

        var entityType = body.EntityType ?? AnalysisEntityTypes.VoiceCall;
        var promptVersion = body.PromptVersion ?? ai.Value.PromptVersion;
        var analysisType = entityType == AnalysisEntityTypes.VoiceCall
            ? AnalysisTypes.VoiceAnalysis
            : AnalysisTypes.EventAnalysis;

        var queued = 0;
        foreach (var entityId in body.EntityIds.Distinct())
        {
            var previous = await analyses.GetLatestForEntityAsync(user.TenantId, entityType, entityId, ct);

            await publisher.PublishAsync(kafka.Value.Topics.AiAnalysisRequest,
                PartitionKeys.ForEntity(entityId), CxJson.Serialize(new AiAnalysisRequest
                {
                    JobKey = $"{JobKeys.Analyze(entityType, entityId, promptVersion)}:{Ulid.NewUlid()}",
                    TenantId = user.TenantId,
                    EntityType = entityType,
                    EntityId = entityId,
                    AnalysisType = analysisType,
                    CustomerId = previous?.CustomerId,
                    PromptVersion = promptVersion,
                    Force = true,
                    SupersedesAnalysisId = previous?.Id
                }), null, ct);

            queued++;
        }

        await audit.WriteAsync(new AuditLog
        {
            TenantId = user.TenantId,
            ActorId = user.Subject,
            Action = AuditActions.Reprocess,
            ResourceType = entityType,
            ResourceId = $"bulk:{queued}",
            After = CxJson.Serialize(new { entity_ids = body.EntityIds.Take(50), queued, promptVersion }),
            Reason = body.Reason,
            IpAddress = user.IpAddress,
            OccurredAt = DateTimeOffset.UtcNow
        }, ct);

        return Results.Accepted("/v1/admin/pipelines", new { queued, entity_type = entityType, promptVersion });
    }

    private static void MapAudit(IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/admin/audit", async (HttpContext ctx, [FromQuery] string? actorId,
            [FromQuery] string? action, [FromQuery] string? resourceType, [FromQuery] string? resourceId,
            [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
            [FromQuery] string? cursor, [FromQuery] int? limit,
            IAuditRepository repository, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            return Results.Ok(await repository.QueryAsync(new AuditQuery
            {
                TenantId = user.TenantId,
                ActorId = actorId,
                Action = action,
                ResourceType = resourceType,
                ResourceId = resourceId,
                From = from,
                To = to,
                Cursor = Cursor.TryDecode(cursor, out var c) ? c : null,
                Limit = limit ?? 50
            }, ct));
        })
        .WithTags("Audit")
        .RequireAuthorization(CxPermissions.AuditRead)
        .WithName("QueryAuditLog");
    }
}

public sealed record AdminReprocessRequest(
    string? EntityType,
    List<string>? EntityIds,
    string? PromptVersion,
    bool Confirm,
    string? Reason);
