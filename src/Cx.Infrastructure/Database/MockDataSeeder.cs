using Cx.Application.Options;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Database;

/// <summary>
/// Loads an idempotent SQL fixture after schema migrations. It is kept separate from the
/// numbered migrations because sample business data is an environment choice, not schema.
/// </summary>
public sealed class MockDataSeeder
{
    private const long AdvisoryLockKey = 8_421_337_002;

    private readonly CxDataSource _dataSource;
    private readonly MockDataOptions _options;
    private readonly ILogger<MockDataSeeder> _logger;

    public MockDataSeeder(CxDataSource dataSource, IOptions<MockDataOptions> options,
        ILogger<MockDataSeeder> logger)
    {
        _dataSource = dataSource;
        _options = options.Value;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!Enabled) return;

        var path = ResolveFile(_options.SeedPath)
            ?? throw new FileNotFoundException($"Mock-data seed file '{_options.SeedPath}' was not found.");
        var sql = await File.ReadAllTextAsync(path, ct);

        await using var connection = await _dataSource.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_lock(@key)", new { key = AdvisoryLockKey }, cancellationToken: ct));

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, commandTimeout: 300, cancellationToken: ct));
            _logger.LogInformation("Database mock data is ready from {Path}", path);
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_unlock(@key)", new { key = AdvisoryLockKey }, cancellationToken: ct));
        }
    }

    private static string? ResolveFile(string configured)
    {
        if (Path.IsPathRooted(configured)) return File.Exists(configured) ? configured : null;

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, configured),
            Path.Combine(Directory.GetCurrentDirectory(), configured)
        };

        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 6 && directory is not null; i++, directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, configured));

        return candidates.FirstOrDefault(File.Exists);
    }
}
