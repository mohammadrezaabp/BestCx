using Cx.Application.Abstractions;
using Cx.Contracts.Security;
using Cx.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cx.Query.Api;

public static class AnalysisEndpoints
{
    // Version history remains available; analyses no longer enter a human review queue.
    public static IEndpointRouteBuilder MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/ai/analyses/{entityType}/{entityId}", async (HttpContext ctx, string entityType, string entityId,
            IAiAnalysisRepository repository, CancellationToken ct) =>
        {
            var versions = await repository.GetVersionsForEntityAsync(ctx.ToCxUser().TenantId, entityType, entityId, ct);
            return Results.Ok(new { entity_type = entityType, entity_id = entityId, versions });
        }).RequireAuthorization(CxPermissions.CustomerRead).WithTags("Analysis").WithName("GetAnalysisVersions");
        return app;
    }
}
