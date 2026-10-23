using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;

namespace Cx.Infrastructure.Security;

public sealed record CustomerPortalAccount(string TenantId, string CustomerId, string Username,
    string DisplayName, Guid SecurityStamp);

/// <summary>Customer accounts are stored in the database and use a separate customer session.</summary>
public sealed class CustomerPortalAccounts(UserAccounts users)
{
    public const string Scheme = "CxPortalSession";
    public const string Policy = "PortalCustomer";
    public const string StampClaim = "portal_security_stamp";
    public const string UsernameClaim = "portal_username";

    private static CustomerPortalAccount Account(AppUser user) => new(user.TenantId, user.CustomerId!,
        user.Username, user.DisplayName ?? user.Username, user.SecurityStamp);

    public async Task<(CustomerPortalAccount? Account, bool Locked)> ValidateAsync(
        string tenantId, string? username, string? password, CancellationToken ct)
    {
        var result = await users.ValidateAsync(tenantId, username, password, "customer", ct);
        return (result.User is null ? null : Account(result.User), result.Locked);
    }

    public async Task<CustomerPortalAccount?> GetAsync(string tenantId, string username, CancellationToken ct)
    {
        var user = await users.GetAsync(tenantId, username, ct);
        return user is { Enabled: true, Role: "customer", CustomerId: not null } ? Account(user) : null;
    }

    public static ClaimsPrincipal Principal(CustomerPortalAccount account) => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, account.CustomerId),
        new Claim(ClaimTypes.Name, account.DisplayName),
        new Claim(CxClaimTypes.TenantId, account.TenantId),
        new Claim(UsernameClaim, account.Username),
        new Claim(ClaimTypes.Role, "customer"),
        new Claim(StampClaim, account.SecurityStamp.ToString())
    }, Scheme));

    public static void ConfigureCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = "cx_portal_session";
        options.Cookie.Path = "/v1/portal";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
        options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        options.Events.OnValidatePrincipal = async ctx =>
        {
            var tenant = ctx.Principal?.FindFirst(CxClaimTypes.TenantId)?.Value;
            var username = ctx.Principal?.FindFirst(UsernameClaim)?.Value;
            var accounts = ctx.HttpContext.RequestServices.GetRequiredService<CustomerPortalAccounts>();
            var account = tenant is null || username is null ? null
                : await accounts.GetAsync(tenant, username, ctx.HttpContext.RequestAborted);
            if (account is null || account.SecurityStamp.ToString() != ctx.Principal?.FindFirst(StampClaim)?.Value
                || account.CustomerId != ctx.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value)
                ctx.RejectPrincipal();
        };
    }
}
