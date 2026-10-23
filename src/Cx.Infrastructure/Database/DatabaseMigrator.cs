using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cx.Infrastructure.Database;

/// <summary>
/// Applies numbered SQL scripts in order, once each, inside a transaction, guarded by an
/// advisory lock so concurrent instances of the single image cannot race on startup
/// (spec 37.4: migrations come before the code that depends on them).
/// </summary>
public sealed class DatabaseMigrator
{
    private const long AdvisoryLockKey = 8_421_337_001;

    private readonly CxDataSource _dataSource;
    private readonly ILogger<DatabaseMigrator> _logger;

    public DatabaseMigrator(CxDataSource dataSource, ILogger<DatabaseMigrator> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<int> MigrateAsync(CancellationToken ct = default)
    {
        var directory = ResolveDirectory(_dataSource.Options.MigrationsPath);
        if (directory is null)
        {
            _logger.LogWarning("Migrations path '{Path}' not found; skipping migrations",
                _dataSource.Options.MigrationsPath);
            return 0;
        }

        var scripts = Directory.GetFiles(directory, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        await using var connection = await _dataSource.OpenAsync(ct);
        await connection.ExecuteAsync("SELECT pg_advisory_lock(@key)", new { key = AdvisoryLockKey });

        try
        {
            await connection.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS schema_migration (
                    script      VARCHAR(256) PRIMARY KEY,
                    checksum    VARCHAR(64)  NOT NULL,
                    applied_at  TIMESTAMPTZ  NOT NULL DEFAULT now()
                )
                """);

            var applied = (await connection.QueryAsync<(string Script, string Checksum)>(
                    "SELECT script, checksum FROM schema_migration"))
                .ToDictionary(r => r.Script, r => r.Checksum, StringComparer.Ordinal);

            var count = 0;
            foreach (var path in scripts)
            {
                var name = Path.GetFileName(path);
                var sql = await File.ReadAllTextAsync(path, ct);
                var checksum = Checksum(sql);

                if (applied.TryGetValue(name, out var existing))
                {
                    if (existing != checksum)
                    {
                        // An edited applied migration means the deployed schema and the repository
                        // have diverged - fail loudly instead of silently running on a wrong schema.
                        throw new InvalidOperationException(
                            $"Migration '{name}' was modified after it was applied (checksum mismatch). " +
                            "Add a new migration script instead of editing an applied one.");
                    }
                    continue;
                }

                _logger.LogInformation("Applying migration {Script}", name);
                await using var tx = await connection.BeginTransactionAsync(ct);
                await connection.ExecuteAsync(new CommandDefinition(sql, transaction: tx, commandTimeout: 300,
                    cancellationToken: ct));
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO schema_migration (script, checksum) VALUES (@name, @checksum)",
                    new { name, checksum }, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
                count++;
            }

            if (count > 0) _logger.LogInformation("Applied {Count} migration(s)", count);
            else _logger.LogInformation("Database schema is up to date ({Total} script(s))", scripts.Count);

            return count;
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(@key)", new { key = AdvisoryLockKey });
        }
    }

    /// <summary>Looks beside the app first (container layout), then walks up for a repo checkout.</summary>
    private static string? ResolveDirectory(string configured)
    {
        if (Path.IsPathRooted(configured))
            return Directory.Exists(configured) ? configured : null;

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, configured),
            Path.Combine(Directory.GetCurrentDirectory(), configured)
        };

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, configured));

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string Checksum(string content)
    {
        // Normalize line endings so a Windows checkout and a Linux container agree.
        var normalized = content.Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
