using System.Text.Json;
using Cx.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace Cx.UnitTests;

public class LoginTests
{
    [Fact]
    public void Password_hashes_are_salted_and_verify_only_the_correct_password()
    {
        var first = UserAccounts.HashPassword("mohammad123");
        var second = UserAccounts.HashPassword("mohammad123");
        first.Should().NotBe(second).And.NotContain("mohammad123");
        UserAccounts.VerifyPassword(first, "mohammad123").Should().BeTrue();
        UserAccounts.VerifyPassword(first, "wrong").Should().BeFalse();
    }

    [Fact]
    public void Serialized_accounts_do_not_expose_hashes_or_security_stamps()
    {
        var json = JsonSerializer.Serialize(new AppUser { PasswordHash = "secret-hash", SecurityStamp = Guid.NewGuid() });
        json.Should().NotContain("secret-hash").And.NotContain("SecurityStamp");
    }

    [Theory]
    [InlineData("", "password", false)]
    [InlineData("customer", null, false)]
    [InlineData("customer", "short", false)]
    public void New_accounts_require_a_username_and_valid_password(string username, string? password, bool exists) =>
        UserAccounts.ValidateCustomerInput(new(username, password), exists).Should().NotBeEmpty();

    [Fact]
    public void Existing_accounts_can_keep_their_password() =>
        UserAccounts.ValidateCustomerInput(new("customer", null), true).Should().BeEmpty();
}
