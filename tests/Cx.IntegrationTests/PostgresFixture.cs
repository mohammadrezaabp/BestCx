using Cx.Application.Options;
using Cx.Infrastructure.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace Cx.IntegrationTests;

/// <summary>
/// Runs the suite against a real, disposable PostgreSQL (spec 32) - the transactional
/// outbox, unique constraints and keyset pagination are exactly the behaviours an
/// in-memory fake would not reproduce.
///
/// Set CX_TEST_POSTGRES to reuse an already running database (for example the compose
/// stack) instead of starting a container.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public CxDataSource DataSource { get; private set; } = null!;

    public bool Available { get; private set; }

    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("CX_TEST_POSTGRES");

        try
        {
            if (!string.IsNullOrWhiteSpace(external))
            {
                ConnectionString = external;
            }
            else
            {
                _container = new PostgreSqlBuilder()
                    .WithImage(Environment.GetEnvironmentVariable("CX_TEST_POSTGRES_IMAGE")
                               ?? "postgres:16-alpine")
                    .WithDatabase("cx_test")
                    .WithUsername("cx")
                    .WithPassword("cx")
                    .Build();

                await _container.StartAsync();
                ConnectionString = _container.GetConnectionString();
            }

            DataSource = new CxDataSource(Options.Create(new DatabaseOptions
            {
                ConnectionString = ConnectionString,
                MigrationsPath = "deploy/postgres/migrations"
            }));

            var migrator = new DatabaseMigrator(DataSource, NullLogger<DatabaseMigrator>.Instance);
            await migrator.MigrateAsync();

            Available = true;
        }
        catch (Exception ex)
        {
            // Docker is not always available on a developer machine or in a restricted CI
            // sandbox. Skipping with a reason beats a red suite that says nothing.
            Available = false;
            SkipReason = $"PostgreSQL is unavailable for integration tests: {ex.Message}";
            if (Environment.GetEnvironmentVariable("CX_TEST_REQUIRE_DEPENDENCIES") == "true")
                throw new InvalidOperationException(SkipReason, ex);
        }
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
