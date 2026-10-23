using System.Security.Claims;
using Cx.Contracts.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Cx.Infrastructure.Security;

/// <summary>The authenticated caller, as the API layer sees it.</summary>
public sealed record CxUser(string Subject, string TenantId, IReadOnlySet<string> Permissions, string? IpAddress)
{
    public bool Has(string permission) =>
        Permissions.Contains(permission) || Permissions.Contains(CxPermissions.Admin);

    public static CxUser Anonymous => new("anonymous", "bestcx",
        new HashSet<string>(StringComparer.Ordinal), null);
}

public static class CxPrincipalExtensions
{
    public static CxUser ToCxUser(this HttpContext context, string defaultTenantId = "bestcx")
    {
        var principal = context.User;
        if (principal?.Identity?.IsAuthenticated != true) return CxUser.Anonymous;

        var permissions = principal.FindAll(CxClaimTypes.Permission).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

        // A JWT from the organization IdP may pack permissions into one space/comma
        // separated claim, or grant them through roles.
        foreach (var claim in principal.FindAll("cx_permissions"))
        {
            foreach (var value in claim.Value.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                permissions.Add(value);
        }

        foreach (var role in principal.FindAll(ClaimTypes.Role).Select(c => c.Value))
        {
            if (CxRoles.Defaults.TryGetValue(role, out var rolePermissions))
                permissions.UnionWith(rolePermissions);
        }

        var tenant = principal.FindFirst(CxClaimTypes.TenantId)?.Value
                     ?? principal.FindFirst("tid")?.Value
                     ?? defaultTenantId;

        var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? principal.FindFirst("sub")?.Value
                      ?? principal.Identity.Name
                      ?? "unknown";

        return new CxUser(subject, tenant, permissions, context.Connection.RemoteIpAddress?.ToString());
    }
}

/// <summary>
/// Backend enforcement of the permission constants (spec 27).
/// Every protected endpoint names the permission it needs; hiding UI is never the control.
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permission) => Permission = permission;
    public string Permission { get; }
}

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var permissions = context.User.FindAll(CxClaimTypes.Permission).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

        foreach (var claim in context.User.FindAll("cx_permissions"))
        {
            foreach (var value in claim.Value.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                permissions.Add(value);
        }

        foreach (var role in context.User.FindAll(ClaimTypes.Role).Select(c => c.Value))
        {
            if (CxRoles.Defaults.TryGetValue(role, out var rolePermissions))
                permissions.UnionWith(rolePermissions);
        }

        if (permissions.Contains(requirement.Permission) || permissions.Contains(CxPermissions.Admin))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
