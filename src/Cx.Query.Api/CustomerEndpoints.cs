using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Query;
using Cx.Contracts;
using Cx.Domain.Entities;
using Cx.Contracts.Security;
using Cx.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Cx.Query.Api;

/// <summary>Customer 360 read endpoints (spec 24).</summary>
public static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var customers = app.MapGroup("/v1/customers")
            .WithTags("Customers")
            .RequireAuthorization(CxPermissions.CustomerRead);

        customers.MapGet("/", async (HttpContext ctx, [FromQuery] string? q, [FromQuery] string? segment,
            [FromQuery] string? cursor, [FromQuery] int? limit, ICustomerRepository repository,
            CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            var page = await repository.SearchAsync(user.TenantId, q, segment,
                QueryHelpers.ParseCursor(cursor), limit ?? 50, ct);
            var canSeePii = user.Has(CxPermissions.PiiRead);
            foreach (var item in page.Items) CustomerProfiles.Project(item, canSeePii);
            return Results.Ok(page);
        }).WithName("SearchCustomers");

        customers.MapPost("/", CreateAsync).WithName("CreateCustomer");
        customers.MapPut("/{customerId}", UpdateAsync).WithName("UpdateCustomer");

        customers.MapGet("/{customerId}/portal-account", async (HttpContext ctx, string customerId,
            UserAccounts accounts, ICustomerRepository repository, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            if (!user.Has(CxPermissions.Admin)) return Results.Forbid();
            if (await repository.GetSummaryAsync(user.TenantId, customerId, ct) is null)
                return Results.NotFound();
            var account = await accounts.GetCustomerAsync(user.TenantId, customerId, ct);
            return Results.Ok(new { exists = account is not null, username = account?.Username,
                enabled = account?.Enabled ?? true, role = "customer" });
        }).WithName("GetCustomerAccount");

        customers.MapPut("/{customerId}/portal-account", async (HttpContext ctx, string customerId,
            CustomerAccountInput body, UserAccounts accounts, ICustomerRepository repository,
            IAuditRepository audit, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            if (!user.Has(CxPermissions.Admin)) return Results.Forbid();
            if (await repository.GetSummaryAsync(user.TenantId, customerId, ct) is null)
                return Results.NotFound();
            var existing = await accounts.GetCustomerAsync(user.TenantId, customerId, ct);
            var errors = UserAccounts.ValidateCustomerInput(body, existing is not null);
            if (errors.Count > 0) return Results.BadRequest(new { error = string.Join(" ", errors) });
            try { await accounts.SaveCustomerAsync(user.TenantId, customerId, body, ct); }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
            { return Results.Conflict(new { error = "This username has already been used." }); }
            await audit.WriteAsync(new AuditLog
            {
                TenantId = user.TenantId, ActorId = user.Subject, Action = "customer.account.update",
                ResourceType = "customer", ResourceId = customerId, IpAddress = user.IpAddress,
                OccurredAt = DateTimeOffset.UtcNow,
                After = CxJson.Serialize(new { body.Username, body.Enabled,
                    password_changed = !string.IsNullOrEmpty(body.Password) })
            }, ct);
            return Results.NoContent();
        }).WithName("SaveCustomerAccount");

        customers.MapGet("/{customerId}", async (HttpContext ctx, string customerId,
            ICustomerRepository customerRepository, ISupportCaseRepository supportCases,
            IAiAnalysisRepository analyses, IVoiceRepository voice, IFeedbackRepository feedback,
            CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            var identity = await customerRepository.GetSummaryAsync(user.TenantId, customerId, ct);
            if (identity is null) return Results.NotFound(new { error = "No customer found" });
            CustomerProfiles.Project(identity, user.Has(CxPermissions.PiiRead));

            var open = await supportCases.GetOpenForCustomerAsync(user.TenantId, customerId, 20, ct);
            var latest = await analyses.GetLatestForEntityAsync(user.TenantId, "customer", customerId, ct)
                         ?? (await analyses.GetRecentForCustomerAsync(user.TenantId, customerId, 1, ct))
                         .FirstOrDefault();
            var calls = await voice.GetRecentForCustomerAsync(user.TenantId, customerId, 5, ct);
            var recentFeedback = await feedback.GetRecentForCustomerAsync(user.TenantId, customerId, 5, ct);

            return Results.Ok(new
            {
                customer = identity,
                open_support_cases = open.Count,
                latest_analysis = latest,
                recent_calls = calls,
                recent_feedback = recentFeedback
            });
        }).WithName("GetCustomer");

        customers.MapGet("/{customerId}/timeline", async (HttpContext ctx, string customerId,
            [FromQuery] string? kinds, [FromQuery] string? productId, [FromQuery] string? severity,
            [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
            [FromQuery] string? cursor, [FromQuery] int? limit,
            IInsightsRepository insights, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();

            var requested = kinds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(TimelineKinds.All.Contains)
                .ToList();

            var page = await insights.GetTimelineAsync(new TimelineQuery
            {
                TenantId = user.TenantId,
                CustomerId = customerId,
                Kinds = requested is { Count: > 0 } ? requested : null,
                ProductId = productId,
                Severity = severity,
                From = from,
                To = to,
                Cursor = QueryHelpers.ParseCursor(cursor),
                Limit = limit ?? 50
            }, ct);

            return Results.Ok(page);
        }).WithName("GetCustomerTimeline");

        customers.MapGet("/{customerId}/insights", async (HttpContext ctx, string customerId,
            IInsightsRepository insights, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            return Results.Ok(await insights.GetCustomerInsightsAsync(user.TenantId, customerId, ct));
        }).WithName("GetCustomerInsights");

        return app;
    }

    private static async Task<IResult> CreateAsync(HttpContext ctx, [FromBody] CustomerProfileInput body,
        ICustomerRepository repository, IAuditRepository audit, CancellationToken ct)
    {
        var user = ctx.ToCxUser();
        if (!user.Has(CxPermissions.FeedbackManage)) return Results.Forbid();

        var errors = CustomerProfiles.Validate(body, creating: true);
        if (errors.Count > 0) return Results.BadRequest(new { error = string.Join(" ", errors), errors });

        var customerId = string.IsNullOrWhiteSpace(body.CustomerId)
            ? CustomerProfiles.NewCustomerId()
            : body.CustomerId.Trim();

        if (!await repository.CreateAsync(user.TenantId, customerId, body, ct))
            return Results.Conflict(new { error = $"Customer with ID «{customerId}» It has already been registered." });

        await audit.WriteAsync(new AuditLog
        {
            TenantId = user.TenantId,
            ActorId = user.Subject,
            Action = AuditActions.CustomerCreate,
            ResourceType = "customer",
            ResourceId = customerId,
            After = CxJson.Serialize(new { body.DisplayName, body.BusinessId, body.Segment }),
            IpAddress = user.IpAddress,
            OccurredAt = DateTimeOffset.UtcNow
        }, ct);

        var created = await repository.GetSummaryAsync(user.TenantId, customerId, ct);
        return Results.Created($"/v1/customers/{Uri.EscapeDataString(customerId)}",
            CustomerProfiles.Project(created!, user.Has(CxPermissions.PiiRead)));
    }

    private static async Task<IResult> UpdateAsync(HttpContext ctx, string customerId,
        [FromBody] CustomerProfileInput body, ICustomerRepository repository, IAuditRepository audit,
        CancellationToken ct)
    {
        var user = ctx.ToCxUser();
        if (!user.Has(CxPermissions.FeedbackManage)) return Results.Forbid();

        var errors = CustomerProfiles.Validate(body, creating: false);
        if (errors.Count > 0) return Results.BadRequest(new { error = string.Join(" ", errors), errors });

        var before = await repository.GetSummaryAsync(user.TenantId, customerId, ct);
        if (before is null) return Results.NotFound(new { error = "No customer found" });

        // Without the PII permission the client only ever saw masked contact details; saving
        // those back would overwrite the real ones, so such edits keep the stored values.
        if (!user.Has(CxPermissions.PiiRead))
        {
            CustomerProfiles.Project(before, canSeePii: true);
            body = body with { Phone = before.Phone, Email = before.Email };
        }

        await repository.UpdateProfileAsync(user.TenantId, customerId, body, ct);

        await audit.WriteAsync(new AuditLog
        {
            TenantId = user.TenantId,
            ActorId = user.Subject,
            Action = AuditActions.CustomerUpdate,
            ResourceType = "customer",
            ResourceId = customerId,
            Before = CxJson.Serialize(new { before.DisplayName, before.BusinessId, before.Segment }),
            After = CxJson.Serialize(new { body.DisplayName, body.BusinessId, body.Segment }),
            IpAddress = user.IpAddress,
            OccurredAt = DateTimeOffset.UtcNow
        }, ct);

        var after = await repository.GetSummaryAsync(user.TenantId, customerId, ct);
        return Results.Ok(CustomerProfiles.Project(after!, user.Has(CxPermissions.PiiRead)));
    }
}

internal static class QueryHelpers
{
    public static Cursor? ParseCursor(string? value) =>
        Cursor.TryDecode(value, out var cursor) ? cursor : null;
}
