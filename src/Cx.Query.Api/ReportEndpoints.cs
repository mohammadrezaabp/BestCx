using Cx.Application.Query;
using Cx.Contracts.Security;
using Cx.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Cx.Query.Api;

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/reports", async (HttpContext ctx, IReportRepository reports,
            [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
            [FromQuery] string? topics, [FromQuery] string? sentiments, [FromQuery] string? channels,
            [FromQuery] string? severities, [FromQuery] string? types, [FromQuery] string? statuses,
            [FromQuery] string? q, [FromQuery] string? bucket, [FromQuery] string? timeZone,
            [FromQuery] string? customerId, [FromQuery] string? inbox, CancellationToken ct) =>
        {
            if (inbox is not (null or "feedback" or "complaints"))
                return Results.BadRequest(new { error = "The reporting section is not valid." });
            var end = to ?? DateTimeOffset.UtcNow;
            var start = from ?? end.AddDays(-30);
            if (start >= end || end - start > TimeSpan.FromDays(366))
                return Results.BadRequest(new { error = "The time period must be valid and maximum 366 days." });
            if (bucket is not (null or "day" or "week" or "month"))
                return Results.BadRequest(new { error = "Time grouping is not valid." });
            // The same IANA zone controls date boundaries in the UI and chart buckets in SQL.
            var zone = timeZone ?? "UTC";
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone, out var iana)) zone = iana;
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(zone); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            { return Results.BadRequest(new { error = "The time zone is not valid." }); }
            var values = new[] { topics, sentiments, channels, severities, types, statuses };
            if (values.Any(v => v?.Length > 4096) || q?.Length > 200 || customerId?.Length > 200)
                return Results.BadRequest(new { error = "Report filter is too long." });
            string[] Split(string? value) => value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct().ToArray() ?? [];
            return Results.Ok(await reports.GetAsync(new ReportQuery
            {
                TenantId = ctx.ToCxUser().TenantId, Inbox = inbox ?? "feedback", From = start, To = end,
                Topics = Split(topics), Sentiments = Split(sentiments), Channels = Split(channels),
                Severities = Split(severities), Types = Split(types), Statuses = Split(statuses),
                Search = q, Bucket = bucket ?? "day", TimeZone = zone,
                CustomerId = string.IsNullOrWhiteSpace(customerId) ? null : customerId.Trim()
            }, ct));
        }).WithTags("Reports").WithName("GetExperienceReport").RequireAuthorization(CxPermissions.CustomerRead);
        return app;
    }
}
