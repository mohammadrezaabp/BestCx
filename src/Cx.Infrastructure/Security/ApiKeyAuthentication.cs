using System.Security.Claims;
using System.Text.Encodings.Web;
using Cx.Application.Options;
using Cx.Contracts.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Security;

public static class CxAuthSchemes
{
    public const string ApiKey = "CxApiKey";
    public const string Jwt = "Bearer";
    public const string Mock = "CxMock";
    public const string Session = "CxSession";
}

public static class CxClaimTypes
{
    public const string Permission = "cx_permission";
    public const string TenantId = "tenant_id";
}

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string HeaderName = "X-Api-Key";
}

/// <summary>
/// Self-contained authentication for deployments without organization SSO.
/// Keys come from configuration and map to a tenant plus an explicit permission set;
/// in production the JWT scheme backed by the organization IdP is used instead (spec 2).
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly AuthOptions _auth;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<AuthOptions> auth)
        : base(options, logger, encoder)
        => _auth = auth.Value;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = ReadKey();
        if (presented is null) return Task.FromResult(AuthenticateResult.NoResult());

        // Constant-time comparison so a wrong key cannot be discovered by timing.
        var definition = _auth.ApiKeys.FirstOrDefault(k =>
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(k.Key),
                System.Text.Encoding.UTF8.GetBytes(presented)));

        if (definition is null)
            return Task.FromResult(AuthenticateResult.Fail("Unknown API key"));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, definition.Subject),
            new(ClaimTypes.Name, definition.Subject),
            new(CxClaimTypes.TenantId, definition.TenantId)
        };

        foreach (var role in definition.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
            if (CxRoles.Defaults.TryGetValue(role, out var rolePermissions))
                claims.AddRange(rolePermissions.Select(p => new Claim(CxClaimTypes.Permission, p)));
        }

        claims.AddRange(definition.Permissions.Select(p => new Claim(CxClaimTypes.Permission, p)));

        var identity = new ClaimsIdentity(claims, CxAuthSchemes.ApiKey);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), CxAuthSchemes.ApiKey);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private string? ReadKey()
    {
        if (Request.Headers.TryGetValue(ApiKeyAuthenticationOptions.HeaderName, out var header) &&
            !string.IsNullOrWhiteSpace(header))
            return header.ToString().Trim();

        // The browser cannot set a header on an <audio src>, so a signed-link flow may
        // carry the key in the query string. That is only acceptable because the panel is
        // served over TLS and every audio access is audited.
        if (Request.Query.TryGetValue("api_key", out var queryKey) && !string.IsNullOrWhiteSpace(queryKey))
            return queryKey.ToString().Trim();

        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
            return authorization["ApiKey ".Length..].Trim();

        return null;
    }
}
