using System.Text;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Query;
using Cx.Domain.Entities;
using Dapper;

namespace Cx.Infrastructure.Database.Repositories;

public sealed class CustomerRepository : ICustomerRepository
{
    private const string SelectColumns = """
        id, tenant_id, customer_id, business_id, display_name, phone_hash, email_hash, segment,
        attributes, first_seen_at, last_seen_at, merged_into_id, created_at, updated_at
        """;

    private readonly CxDataSource _dataSource;

    public CustomerRepository(CxDataSource dataSource) => _dataSource = dataSource;

    public async Task UpsertFromEventAsync(string tenantId, string customerId, string? businessId,
        DateTimeOffset seenAt, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO customer_identity (id, tenant_id, customer_id, business_id, first_seen_at, last_seen_at)
            VALUES (@id, @tenantId, @customerId, @businessId, @seenAt, @seenAt)
            ON CONFLICT (tenant_id, customer_id) DO UPDATE
            SET last_seen_at = GREATEST(customer_identity.last_seen_at, EXCLUDED.last_seen_at),
                first_seen_at = LEAST(customer_identity.first_seen_at, EXCLUDED.first_seen_at),
                business_id = COALESCE(EXCLUDED.business_id, customer_identity.business_id)
            """, new
        {
            id = Guid.NewGuid(),
            tenantId,
            customerId,
            businessId,
            seenAt = seenAt.ToUniversalTime()
        }, cancellationToken: ct));
    }

    public async Task<CustomerIdentity?> GetAsync(string tenantId, string customerId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<CustomerIdentity>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM customer_identity WHERE tenant_id = @tenantId AND customer_id = @customerId",
            new { tenantId, customerId }, cancellationToken: ct));
    }

    /// <summary>Profile plus feedback counts; the counts are what an operator scans the list for.</summary>
    private const string SummarySelect = """
        SELECT c.customer_id, c.display_name, c.business_id, c.segment, c.attributes::text AS attributes,
               c.first_seen_at, c.last_seen_at, c.created_at,
               (SELECT count(*) FROM feedback f
                 WHERE f.tenant_id = c.tenant_id AND f.customer_id = c.customer_id) AS feedback_count,
               (SELECT count(*) FROM ai_analysis a
                 WHERE a.tenant_id = c.tenant_id AND a.customer_id = c.customer_id
                   AND a.result->>'sentiment' = 'negative'
                   AND NOT EXISTS (SELECT 1 FROM ai_analysis n WHERE n.supersedes_analysis_id = a.id)) AS negative_count
        FROM customer_identity c
        """;

    public async Task<Page<CustomerSummary>> SearchAsync(string tenantId, string? term, string? segment,
        Cursor? cursor, int limit, CancellationToken ct = default)
    {
        var sql = new StringBuilder(SummarySelect)
            .Append(" WHERE c.tenant_id = @tenantId AND c.merged_into_id IS NULL");
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", tenantId);

        if (!string.IsNullOrWhiteSpace(term))
        {
            // A phone typed with English digits or spaces still matches the stored normalized form.
            var phone = CustomerProfiles.NormalizePhone(term);
            sql.Append("""
                 AND (c.customer_id ILIKE @term OR c.business_id ILIKE @term OR c.display_name ILIKE @term
                      OR c.attributes->>'email' ILIKE @term
                """);
            if (phone is { Length: >= 3 })
            {
                sql.Append(" OR c.attributes->>'phone' LIKE @phone");
                parameters.Add("phone", $"%{phone}%");
            }
            sql.Append(')');
            parameters.Add("term", $"%{term.Trim()}%");
        }

        if (!string.IsNullOrWhiteSpace(segment))
        {
            sql.Append(" AND c.segment = @segment");
            parameters.Add("segment", segment);
        }

        if (cursor is { } cur)
        {
            sql.Append(" AND (c.last_seen_at, c.customer_id) < (@cursorTime, @cursorId)");
            parameters.Add("cursorTime", cur.Timestamp);
            parameters.Add("cursorId", cur.Tiebreaker);
        }

        var take = Math.Clamp(limit, 1, 200);
        sql.Append(" ORDER BY c.last_seen_at DESC, c.customer_id DESC LIMIT @limit");
        parameters.Add("limit", take + 1);

        await using var connection = await _dataSource.OpenAsync(ct);
        var rows = (await connection.QueryAsync<CustomerSummary>(
            new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct))).ToList();

        return Page<CustomerSummary>.From(rows, take, x => new Cursor(x.LastSeenAt, x.CustomerId).Encode());
    }

    public async Task<CustomerSummary?> GetSummaryAsync(string tenantId, string customerId,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<CustomerSummary>(new CommandDefinition(
            SummarySelect + " WHERE c.tenant_id = @tenantId AND c.customer_id = @customerId",
            new { tenantId, customerId }, cancellationToken: ct));
    }

    public async Task<bool> CreateAsync(string tenantId, string customerId, CustomerProfileInput profile,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await _dataSource.OpenAsync(ct);
        var inserted = await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO customer_identity (id, tenant_id, customer_id, business_id, display_name, phone_hash,
                email_hash, segment, attributes, first_seen_at, last_seen_at, updated_at)
            VALUES (@id, @tenantId, @customerId, @businessId, @displayName, @phoneHash,
                @emailHash, @segment, @attributes::jsonb, @now, @now, @now)
            ON CONFLICT (tenant_id, customer_id) DO NOTHING
            """, new
        {
            id = Guid.NewGuid(),
            tenantId,
            customerId,
            businessId = Clean(profile.BusinessId),
            displayName = Clean(profile.DisplayName),
            phoneHash = CustomerProfiles.Hash(CustomerProfiles.NormalizePhone(profile.Phone)),
            emailHash = CustomerProfiles.Hash(CustomerProfiles.NormalizeEmail(profile.Email)),
            segment = Clean(profile.Segment),
            attributes = CustomerProfiles.BuildAttributes(null, profile, "manual"),
            now
        }, cancellationToken: ct));
        return inserted == 1;
    }

    public async Task<bool> UpdateProfileAsync(string tenantId, string customerId, CustomerProfileInput profile,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        var existing = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT attributes::text FROM customer_identity WHERE tenant_id = @tenantId AND customer_id = @customerId",
            new { tenantId, customerId }, cancellationToken: ct));
        if (existing is null) return false;

        // Edits replace the operator-maintained fields; identity timestamps stay event-driven.
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE customer_identity
            SET display_name = @displayName, business_id = @businessId, segment = @segment,
                phone_hash = @phoneHash, email_hash = @emailHash, attributes = @attributes::jsonb,
                updated_at = now()
            WHERE tenant_id = @tenantId AND customer_id = @customerId
            """, new
        {
            tenantId,
            customerId,
            businessId = Clean(profile.BusinessId),
            displayName = Clean(profile.DisplayName),
            phoneHash = CustomerProfiles.Hash(CustomerProfiles.NormalizePhone(profile.Phone)),
            emailHash = CustomerProfiles.Hash(CustomerProfiles.NormalizeEmail(profile.Email)),
            segment = Clean(profile.Segment),
            attributes = CustomerProfiles.BuildAttributes(existing, profile, "manual")
        }, cancellationToken: ct));
        return true;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public async Task MergeAsync(string tenantId, string sourceCustomerId, string targetCustomerId,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var target = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM customer_identity WHERE tenant_id = @tenantId AND customer_id = @targetCustomerId",
            new { tenantId, targetCustomerId }, tx, cancellationToken: ct));

        if (target is null)
            throw new InvalidOperationException($"target customer '{targetCustomerId}' does not exist");

        // The source identity is kept and tombstoned rather than deleted, so history
        // and the audit trail stay intact (voice design 14).
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE customer_identity SET merged_into_id = @target
            WHERE tenant_id = @tenantId AND customer_id = @sourceCustomerId
            """, new { target, tenantId, sourceCustomerId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }
}
