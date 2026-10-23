using Cx.Application.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Cx.Infrastructure.Database;

/// <summary>Shared, pooled Npgsql data source. PostgreSQL is the operational source of truth (rule R3).</summary>
public sealed class CxDataSource : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public CxDataSource(IOptions<DatabaseOptions> options)
    {
        Options = options.Value;
        var builder = new NpgsqlDataSourceBuilder(Options.ConnectionString);
        builder.ConnectionStringBuilder.ApplicationName = "cx-platform";
        _dataSource = builder.Build();
    }

    public DatabaseOptions Options { get; }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default) =>
        await _dataSource.OpenConnectionAsync(ct);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
