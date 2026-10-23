using Cx.Application.Options;
using Cx.Infrastructure.Security;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Cx.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class UserAccountsTests(PostgresFixture fixture)
{
    private UserAccounts Accounts => new(fixture.DataSource, Options.Create(new AuthOptions()));

    private async Task<string> CreateAsync()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var id = $"account-{Guid.NewGuid():N}";
        await using var connection = await fixture.DataSource.OpenAsync();
        await connection.ExecuteAsync("INSERT INTO customer_identity (id, tenant_id, customer_id, display_name, first_seen_at, last_seen_at) VALUES (@uuid, 'bestcx', @id, 'Customer Name', now(), now())", new { id, uuid = Guid.NewGuid() });
        await Accounts.SaveCustomerAsync("bestcx", id, new(id, "test-password"), default);
        return id;
    }

    [SkippableFact]
    public async Task Database_logins_use_hashed_passwords_and_enforce_tenant_and_role()
    {
        var id = await CreateAsync();
        var user = (await Accounts.ValidateAsync("bestcx", id.ToUpperInvariant(), "test-password", "customer", default)).User;
        user.Should().NotBeNull();
        user!.CustomerId.Should().Be(id);
        user.DisplayName.Should().Be("Customer Name");
        user.PasswordHash.Should().NotBe("test-password");
        (await Accounts.ValidateAsync("other", id, "test-password", "customer", default)).User.Should().BeNull();
        (await Accounts.ValidateAsync("bestcx", id, "test-password", "admin", default)).User.Should().BeNull();
        (await Accounts.ValidateAsync("bestcx", id, "wrong", "customer", default)).User.Should().BeNull();
    }

    [SkippableFact]
    public async Task Lockout_survives_a_new_service_instance_and_expires()
    {
        var id = await CreateAsync();
        for (var i = 0; i < 5; i++) await Accounts.ValidateAsync("bestcx", id, "wrong", "customer", default);
        (await Accounts.ValidateAsync("bestcx", id, "test-password", "customer", default)).Locked.Should().BeTrue();
        await using var connection = await fixture.DataSource.OpenAsync();
        await connection.ExecuteAsync("UPDATE app_user SET locked_until = now() - interval '1 minute' WHERE username = @id", new { id });
        (await Accounts.ValidateAsync("bestcx", id, "test-password", "customer", default)).User.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task Password_resets_rotate_the_session_stamp_and_disabling_blocks_login()
    {
        var id = await CreateAsync();
        var old = await Accounts.GetCustomerAsync("bestcx", id, default);
        await Accounts.SaveCustomerAsync("bestcx", id, new(id, "new-password"), default);
        var updated = await Accounts.GetCustomerAsync("bestcx", id, default);
        updated!.SecurityStamp.Should().NotBe(old!.SecurityStamp);
        (await Accounts.ValidateAsync("bestcx", id, "test-password", "customer", default)).User.Should().BeNull();
        (await Accounts.ValidateAsync("bestcx", id, "new-password", "customer", default)).User.Should().NotBeNull();
        await Accounts.SaveCustomerAsync("bestcx", id, new(id, null, false), default);
        (await Accounts.ValidateAsync("bestcx", id, "new-password", "customer", default)).User.Should().BeNull();
    }

    [SkippableFact]
    public async Task Usernames_are_unique_without_case_and_admin_bootstrap_is_idempotent()
    {
        var id = await CreateAsync();
        var another = await CreateAsync();
        var duplicate = () => Accounts.SaveCustomerAsync("bestcx", another, new(id.ToUpperInvariant(), "test-password"), default);
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
        var adminName = $"admin-{Guid.NewGuid():N}";
        var options = new AuthOptions { Mode = "login", Users = { new() { Username = adminName, Password = "admin-password", Roles = { "admin" } } } };
        var admins = new UserAccounts(fixture.DataSource, Options.Create(options));
        await admins.EnsureAdminsAsync(default);
        var before = await admins.GetAsync("bestcx", adminName, default);
        await admins.EnsureAdminsAsync(default);
        var after = await admins.GetAsync("bestcx", adminName, default);
        after!.SecurityStamp.Should().Be(before!.SecurityStamp);
        (await admins.ValidateAsync("bestcx", adminName, "admin-password", "admin", default)).User.Should().NotBeNull();
        (await admins.ValidateAsync("bestcx", adminName, "admin-password", "customer", default)).User.Should().BeNull();
    }
}
