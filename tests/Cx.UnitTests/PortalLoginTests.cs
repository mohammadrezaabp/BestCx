using System.Security.Claims;
using Cx.Contracts.Security;
using Cx.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace Cx.UnitTests;

public class PortalLoginTests
{
    [Fact]
    public void Customer_sessions_have_only_customer_role_and_no_staff_permissions()
    {
        var principal = CustomerPortalAccounts.Principal(new("bestcx", "C-123", "maryam", "Maryam", Guid.NewGuid()));
        principal.Identity!.IsAuthenticated.Should().BeTrue();
        principal.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("C-123");
        principal.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Equal("customer");
        principal.FindAll(CxClaimTypes.Permission).Should().BeEmpty();
        principal.Identity.AuthenticationType.Should().Be(CustomerPortalAccounts.Scheme);
    }

}
