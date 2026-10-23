using System.Security.Claims;
using System.Text.Encodings.Web;
using Cx.Application.Options;
using Cx.Contracts.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Security;

/// <summary>
/// Passwordless identity for the explicitly enabled local demo. Selecting this scheme is
/// an intentional configuration choice; API-key and JWT modes retain their normal checks.
/// </summary>
public sealed class MockAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly AuthOptions _auth;

    public MockAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, IOptions<AuthOptions> auth)
        : base(options, logger, encoder)
        => _auth = auth.Value;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, _auth.MockSubject),
            new(ClaimTypes.Name, _auth.MockSubject),
            new(ClaimTypes.Role, CxRoles.Admin),
            new(CxClaimTypes.TenantId, _auth.MockTenantId),
            new(CxClaimTypes.Permission, CxPermissions.Admin)
        };

        var identity = new ClaimsIdentity(claims, CxAuthSchemes.Mock);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), CxAuthSchemes.Mock);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
