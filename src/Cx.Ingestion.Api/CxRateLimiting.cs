using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Cx.Application.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cx.Ingestion.Api;

/// <summary>
/// Rate limiting is mandatory on ingestion (spec 9). Partitioned per authenticated
/// producer so one noisy client cannot starve the others.
/// </summary>
public static class CxRateLimiting
{
    public const string IngestionPolicy = "cx-ingestion";
    public const string PortalPolicy = "cx-customer-portal";

    public static IServiceCollection AddCxRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(PortalPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                    AutoReplenishment = true
                }));

            options.AddPolicy(IngestionPolicy, context =>
            {
                var ingestion = context.RequestServices.GetRequiredService<IOptions<IngestionOptions>>().Value;
                var partition = context.User.Identity?.Name
                                ?? context.Connection.RemoteIpAddress?.ToString()
                                ?? "anonymous";

                return RateLimitPartition.GetTokenBucketLimiter(partition, _ => new TokenBucketRateLimiterOptions
                {
                    // A token bucket absorbs the bursty traffic a browser SDK produces
                    // on page load without rejecting legitimate batches.
                    TokenLimit = Math.Max(1, ingestion.RateLimitPermitPerMinute),
                    TokensPerPeriod = Math.Max(1, ingestion.RateLimitPermitPerMinute),
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = ingestion.RateLimitQueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                });
            });

            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    error = "rate_limit_exceeded",
                    detail = "too many events; retry after the indicated delay"
                }, ct);
            };
        });

        return services;
    }
}
