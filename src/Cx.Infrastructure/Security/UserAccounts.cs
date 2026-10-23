using System.Text.Json.Serialization;
using Cx.Application.Operations;
using Cx.Infrastructure.Operations;
using Cx.Application.Options;
using Cx.Infrastructure.Database;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Security;

public sealed class AppUser
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Username { get; set; } = "";
    [JsonIgnore] public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "customer";
    public string? CustomerId { get; set; }
    public string? DisplayName { get; set; }
    public bool Enabled { get; set; }
    [JsonIgnore] public Guid SecurityStamp { get; set; }
    [JsonIgnore] public int FailedAttempts { get; set; }
    [JsonIgnore] public DateTimeOffset? LockedUntil { get; set; }
}

public sealed record CustomerAccountInput(string? Username, string? Password, bool Enabled = true);
public sealed record StaffAccountInput(Guid Id, string Username, string DisplayName, string Role, string? Password, bool Enabled = true);

/// <summary>Database logins for customer and permission-scoped staff roles.</summary>
public sealed class UserAccounts(CxDataSource dataSource, IOptions<AuthOptions> options)
{
    private static readonly PasswordHasher<AppUser> Hasher = new();
    private static readonly AppUser Dummy = new();
    private static readonly string DummyHash = HashPassword(Guid.NewGuid().ToString());
    private const string Select = """
        SELECT u.id, u.tenant_id, u.username, u.password_hash, u.role, u.customer_id,
          u.enabled, u.security_stamp, u.failed_attempts, u.locked_until, COALESCE(NULLIF(c.display_name, ''), u.display_name, u.username) AS display_name
        FROM app_user u LEFT JOIN customer_identity c
          ON c.tenant_id = u.tenant_id AND c.customer_id = u.customer_id
        WHERE (c.merged_into_id IS NULL)
        """;

    public static string HashPassword(string password) => Hasher.HashPassword(Dummy, password);
    public static bool VerifyPassword(string hash, string password) =>
        Hasher.VerifyHashedPassword(Dummy, hash, password) != PasswordVerificationResult.Failed;

    public async Task<AppUser?> GetAsync(string tenantId, string username, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<AppUser>(new CommandDefinition(
            Select + " AND u.tenant_id = @tenantId AND lower(u.username) = @username",
            new { tenantId, username = username.Trim().ToLowerInvariant() }, cancellationToken: ct));
    }

    public async Task<AppUser?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<AppUser>(new CommandDefinition(
            Select + " AND u.id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<AppUser?> GetCustomerAsync(string tenantId, string customerId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<AppUser>(new CommandDefinition(
            Select + " AND u.tenant_id = @tenantId AND u.customer_id = @customerId AND u.role = 'customer'",
            new { tenantId, customerId }, cancellationToken: ct));
    }

    public async Task<(AppUser? User, bool Locked)> ValidateAsync(string tenantId, string? username,
        string? password, string role, CancellationToken ct)
    {
        if (username?.Length > 128 || password?.Length > 256) return (null, false);
        var user = await GetAsync(tenantId, username ?? "", ct);
        var valid = VerifyPassword(user?.PasswordHash ?? DummyHash, password ?? "");
        if (user is null || !user.Enabled || (role == "staff" ? !OperationsStore.StaffRoles.Contains(user.Role) : user.Role != role)) return (null, false);
        if (user.LockedUntil > DateTimeOffset.UtcNow) return (null, true);
        await using var connection = await dataSource.OpenAsync(ct);
        if (!valid)
        {
            var locked = await connection.QuerySingleOrDefaultAsync<bool>(new CommandDefinition("""
                UPDATE app_user SET
                  failed_attempts = CASE WHEN locked_until <= now() THEN 1 ELSE failed_attempts + 1 END,
                  locked_until = CASE WHEN (CASE WHEN locked_until <= now() THEN 1 ELSE failed_attempts + 1 END) >= 5
                    THEN now() + interval '5 minutes' ELSE NULL END
                WHERE id = @id AND enabled AND security_stamp = @stamp
                RETURNING locked_until IS NOT NULL
                """, new { user.Id, stamp = user.SecurityStamp }, cancellationToken: ct));
            return (null, locked);
        }
        // The stamp guards against a password reset or disable that raced with this login.
        var updated = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE app_user SET failed_attempts = 0, locked_until = NULL
            WHERE id = @id AND enabled AND security_stamp = @stamp
              AND (locked_until IS NULL OR locked_until <= now())
            """, new { user.Id, stamp = user.SecurityStamp }, cancellationToken: ct));
        return (updated == 1 ? user : null, false);
    }

    public static IReadOnlyList<string> ValidateCustomerInput(CustomerAccountInput input, bool exists)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Username) || input.Username.Trim().Length > 128)
            errors.Add("Enter the username (Maximum 128 characters).");
        if ((!exists || !string.IsNullOrEmpty(input.Password)) && input.Password?.Length is not (>= 8 and <= 256))
            errors.Add("The password must be between 8 and 256 characters long.");
        return errors;
    }

    public async Task SaveCustomerAsync(string tenantId, string customerId, CustomerAccountInput input, CancellationToken ct)
    {
        var existing = await GetCustomerAsync(tenantId, customerId, ct);
        var errors = ValidateCustomerInput(input, existing is not null);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        var hash = string.IsNullOrEmpty(input.Password) ? existing!.PasswordHash : HashPassword(input.Password);
        await using var connection = await dataSource.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO app_user (id, tenant_id, username, password_hash, role, customer_id, enabled, security_stamp)
            VALUES (@id, @tenantId, @username, @hash, 'customer', @customerId, @enabled, @stamp)
            ON CONFLICT (tenant_id, customer_id) DO UPDATE SET username = @username, password_hash = @hash,
              enabled = @enabled, security_stamp = @stamp, failed_attempts = 0, locked_until = NULL, updated_at = now()
            """, new { id = Guid.NewGuid(), tenantId, customerId, username = input.Username!.Trim(), hash,
                input.Enabled, stamp = Guid.NewGuid() }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AppUser>> ListStaffAsync(string tenantId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenAsync(ct);
        return (await connection.QueryAsync<AppUser>(new CommandDefinition(Select +
            " AND u.tenant_id=@tenantId AND u.role<>'customer' ORDER BY u.display_name,u.username", new { tenantId }, cancellationToken: ct))).AsList();
    }

    public async Task<AppUser> UpdateProfileAsync(string tenantId, Guid id, Guid stamp, string displayName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 256)
            throw new OperationsException("Enter a display name (Maximum 256 characters).");
        await using var db = await dataSource.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var user = await db.QuerySingleOrDefaultAsync<AppUser>(new CommandDefinition(
            "SELECT * FROM app_user WHERE tenant_id=@tenantId AND id=@id AND enabled AND role<>'customer' AND security_stamp=@stamp FOR UPDATE",
            new { tenantId, id, stamp }, tx, cancellationToken: ct)) ?? throw new OperationsException("The account balance has changed; Sign in again.", 409);
        await db.ExecuteAsync(new CommandDefinition("UPDATE app_user SET display_name=@displayName,updated_at=now() WHERE tenant_id=@tenantId AND id=@id",
            new { tenantId, id, displayName = displayName.Trim() }, tx, cancellationToken: ct));
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_log(tenant_id,actor_id,action,resource_type,resource_id,before,after)
            VALUES(@tenantId,@actor,'user.profile.update','app_user',@resource,@previous::jsonb,@detail::jsonb)
            """, new { tenantId, actor = user.Username, resource = id.ToString(),
                previous = System.Text.Json.JsonSerializer.Serialize(new { display_name = user.DisplayName }),
                detail = System.Text.Json.JsonSerializer.Serialize(new { display_name = displayName.Trim() }) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        user.DisplayName = displayName.Trim();
        return user;
    }
    public async Task SaveStaffAsync(string tenantId, string actor, StaffAccountInput input, CancellationToken ct)
    {
        if (input.Id == Guid.Empty || string.IsNullOrWhiteSpace(input.Username) || input.Username.Length > 128 ||
            string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Length > 256 || !OperationsStore.StaffRoles.Contains(input.Role))
            throw new OperationsException("Enter a valid name, username and role.");
        await using var connection = await dataSource.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
            new { key = $"staff:{tenantId}" }, tx, cancellationToken: ct));
        var existing = await connection.QuerySingleOrDefaultAsync<AppUser>(new CommandDefinition(
            "SELECT * FROM app_user WHERE tenant_id=@tenantId AND id=@id FOR UPDATE", new { tenantId, input.Id }, tx, cancellationToken: ct));
        if (existing?.Role == "customer") throw new OperationsException("Manage the customer account from the customer file.");
        if ((existing is null || !string.IsNullOrEmpty(input.Password)) && input.Password?.Length is not (>= 8 and <= 256))
            throw new OperationsException("The password must be between 8 and 256 characters long.");
        if (existing is { Role: "admin", Enabled: true } && (!input.Enabled || input.Role != "admin") &&
            await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM app_user WHERE tenant_id=@tenantId AND role='admin' AND enabled", new { tenantId }, tx, cancellationToken: ct)) <= 1)
            throw new OperationsException("The last active administrator cannot be disabled or his role changed.", 409);
        var hash = string.IsNullOrEmpty(input.Password) ? existing!.PasswordHash : HashPassword(input.Password);
        var stamp = existing is not null && string.IsNullOrEmpty(input.Password) && existing.Enabled == input.Enabled && existing.Role == input.Role
            ? existing.SecurityStamp : Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO app_user(id,tenant_id,username,display_name,role,password_hash,enabled,security_stamp)
            VALUES(@id,@tenantId,@username,@displayName,@role,@hash,@enabled,@stamp)
            ON CONFLICT(id) DO UPDATE SET username=@username,display_name=@displayName,role=@role,
              password_hash=@hash,enabled=@enabled,security_stamp=@stamp,failed_attempts=0,locked_until=NULL,updated_at=now()
            WHERE app_user.tenant_id=@tenantId AND app_user.role<>'customer'
            """, new { input.Id, tenantId, username = input.Username.Trim(), displayName = input.DisplayName.Trim(), input.Role, hash, input.Enabled, stamp }, tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_log(tenant_id,actor_id,action,resource_type,resource_id,after)
            VALUES(@tenantId,@actor,'role.change','app_user',@id::text,@detail::jsonb)
            """, new { tenantId, actor, input.Id, detail = System.Text.Json.JsonSerializer.Serialize(new { input.Role, input.Enabled }) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    /// <summary>Only admin accounts are bootstrapped from deployment secrets. Customers are created through the staff API.</summary>
    public async Task EnsureAdminsAsync(CancellationToken ct)
    {
        if (!options.Value.Mode.Equals("login", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var admin in options.Value.Users.Where(u => u.Roles.Contains("admin") &&
                     !string.IsNullOrWhiteSpace(u.Username) && !string.IsNullOrEmpty(u.Password)))
        {
            var existing = await GetAsync(admin.TenantId, admin.Username, ct);
            if (existing is { Role: "customer" }) throw new InvalidOperationException("Configured admin username belongs to a customer.");
            var passwordChanged = existing is null || !VerifyPassword(existing.PasswordHash, admin.Password);
            await using var connection = await dataSource.OpenAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO app_user (id, tenant_id, username, password_hash, role, display_name, security_stamp)
                VALUES (@id, @tenantId, @username, @hash, 'admin', @displayName, @stamp)
                ON CONFLICT (tenant_id, lower(username)) DO UPDATE SET password_hash = @hash,
                  display_name = COALESCE(NULLIF(app_user.display_name, ''), @displayName), security_stamp = @stamp, updated_at = now()
                WHERE app_user.role = 'admin'
                """, new { id = Guid.NewGuid(), admin.TenantId, admin.Username, admin.DisplayName,
                    hash = passwordChanged ? HashPassword(admin.Password) : existing!.PasswordHash,
                    stamp = passwordChanged ? Guid.NewGuid() : existing!.SecurityStamp }, cancellationToken: ct));
        }
    }
}
