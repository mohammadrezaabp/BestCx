using System.Security.Claims;
using Cx.Application.Options;
using Cx.Contracts.Security;
using Cx.Infrastructure.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Security;

public sealed record LoginRequest(string? Username, string? Password);
public sealed record ProfileRequest(string DisplayName);

/// <summary>Session endpoints for the console. <c>/v1/auth/me</c> answers in every auth mode.</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/v1/auth").WithTags("Auth");

        auth.MapPost("/login", async (HttpContext ctx, LoginRequest body, UserAccounts users,
            IOptions<AuthOptions> options, CancellationToken ct) =>
        {
            if (!options.Value.Mode.Equals("login", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "Login with username is not active in this establishment." });

            var tenant = options.Value.Users.FirstOrDefault(u => u.Roles.Contains("admin"))?.TenantId ?? "bestcx";
            var result = await users.ValidateAsync(tenant, body.Username, body.Password, "staff", ct);
            if (result.Locked)
                return Results.Json(new { error = "Login is blocked for several minutes due to unsuccessful attempts." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            if (result.User is null)
                return Results.Json(new { error = "Username or password is incorrect." },
                    statusCode: StatusCodes.Status401Unauthorized);

            var user = result.User;
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Username),
                new(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName),
                new(CxClaimTypes.TenantId, user.TenantId),
                new(ClaimTypes.Role, user.Role),
                new("user_id", user.Id.ToString()),
                new("user_security_stamp", user.SecurityStamp.ToString())
            };

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CxAuthSchemes.Session));
            await ctx.SignInAsync(CxAuthSchemes.Session, principal,
                new AuthenticationProperties { IsPersistent = true });

            // The cookie applies from the next request; answer with this one's identity now.
            ctx.User = principal;
            return Results.Ok(Me(principal, ctx, options.Value));
        }).AllowAnonymous().WithName("Login");

        auth.MapPost("/logout", async (HttpContext ctx, IOptions<AuthOptions> options) =>
        {
            if (options.Value.Mode.Equals("login", StringComparison.OrdinalIgnoreCase))
                await ctx.SignOutAsync(CxAuthSchemes.Session);
            return Results.NoContent();
        }).AllowAnonymous().WithName("Logout");

        auth.MapGet("/me", (HttpContext ctx, IOptions<AuthOptions> options) =>
            Results.Ok(Me(ctx.User, ctx, options.Value))).RequireAuthorization().WithName("Me");

        auth.MapPut("/profile", async (HttpContext ctx, ProfileRequest body, UserAccounts users, IOptions<AuthOptions> options, CancellationToken ct) =>
        {
            if (!options.Value.Mode.Equals("login", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParse(ctx.User.FindFirst("user_id")?.Value, out var id) ||
                !Guid.TryParse(ctx.User.FindFirst("user_security_stamp")?.Value, out var stamp)) return Results.Forbid();
            var current = ctx.ToCxUser();
            var account = await users.GetByIdAsync(id, ct);
            if (account is null || account.TenantId != current.TenantId || account.Role == "customer") return Results.Forbid();
            var updated = await users.UpdateProfileAsync(current.TenantId, id, stamp, body.DisplayName, ct);
            var identity = new ClaimsIdentity(ctx.User.Identity as ClaimsIdentity);
            foreach (var claim in identity.FindAll(ClaimTypes.Name).Concat(identity.FindAll("user_security_stamp")).ToArray()) identity.RemoveClaim(claim);
            identity.AddClaim(new Claim(ClaimTypes.Name, updated.DisplayName!));
            identity.AddClaim(new Claim("user_security_stamp", updated.SecurityStamp.ToString()));
            var principal = new ClaimsPrincipal(identity);
            await ctx.SignInAsync(CxAuthSchemes.Session, principal, new AuthenticationProperties { IsPersistent = true });
            ctx.User = principal;
            return Results.Ok(Me(principal, ctx, options.Value));
        }).RequireAuthorization().AddEndpointFilter<OperationsErrorFilter>().WithName("UpdateCurrentUserProfile");

        return app;
    }

    private static object Me(ClaimsPrincipal principal, HttpContext ctx, AuthOptions options)
    {
        var user = ctx.ToCxUser();
        return new
        {
            username = user.Subject,
            display_name = principal.FindFirst(ClaimTypes.Name)?.Value ?? user.Subject,
            tenant_id = user.TenantId,
            mode = options.Mode.ToLowerInvariant(),
            permissions = user.Permissions.OrderBy(p => p).ToArray()
        };
    }
}

internal static class SessionCookie
{
    public static void Configure(CookieAuthenticationOptions options, AuthOptions auth)
    {
        options.Cookie.Name = "cx_session";
        options.Cookie.HttpOnly = true;
        // Strict same-site is the CSRF defence: the console and API share one origin.
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(Math.Max(1, auth.SessionHours));
        options.SlidingExpiration = true;
        // An API answers with status codes, never with a redirect to a login page.
        options.Events.OnValidatePrincipal = async ctx =>
        {
            var accounts = ctx.HttpContext.RequestServices.GetRequiredService<UserAccounts>();
            var user = Guid.TryParse(ctx.Principal?.FindFirst("user_id")?.Value, out var id)
                ? await accounts.GetByIdAsync(id, ctx.HttpContext.RequestAborted) : null;
            if (user is not { Enabled: true } || user.Role == "customer" ||
                user.SecurityStamp.ToString() != ctx.Principal?.FindFirst("user_security_stamp")?.Value ||
                user.TenantId != ctx.Principal?.FindFirst(CxClaimTypes.TenantId)?.Value)
                ctx.RejectPrincipal();
        };
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }
}
