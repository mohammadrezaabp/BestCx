using Cx.Application.Abstractions;
using Cx.Infrastructure.Database;
using Cx.Infrastructure.Messaging;
using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cx.Host;

/// <summary>
/// Readiness: the process is only ready when the dependencies it actually uses answer.
/// Kafka and object storage are reported as degraded rather than unhealthy, because the
/// API can still serve reads from PostgreSQL while the bus recovers.
/// </summary>
public sealed class CxHealthCheck : IHealthCheck
{
    private readonly CxDataSource _dataSource;
    private readonly IObjectStorage _storage;
    private readonly KafkaTopicProvisioner? _kafka;
    private readonly CxRoleSet _roles;

    public CxHealthCheck(CxDataSource dataSource, IObjectStorage storage, CxRoleSet roles,
        KafkaTopicProvisioner? kafka = null)
    {
        _dataSource = dataSource;
        _storage = storage;
        _roles = roles;
        _kafka = kafka;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken ct = default)
    {
        var data = new Dictionary<string, object> { ["roles"] = _roles.ToString() };

        try
        {
            await using var connection = await _dataSource.OpenAsync(ct);
            await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
            data["postgres"] = "ok";
        }
        catch (Exception ex)
        {
            data["postgres"] = ex.Message;
            return HealthCheckResult.Unhealthy("PostgreSQL is not reachable", ex, data);
        }

        var degraded = new List<string>();

        if (_kafka is not null)
        {
            var reachable = await _kafka.IsReachableAsync(ct);
            data["kafka"] = reachable ? "ok" : "unreachable";
            if (!reachable) degraded.Add("kafka");
        }
        else
        {
            data["kafka"] = "in-memory bus";
        }

        try
        {
            await _storage.StatAsync("health-probe", ct);
            data["object_storage"] = "ok";
        }
        catch (Exception ex)
        {
            data["object_storage"] = ex.Message;
            degraded.Add("object_storage");
        }

        return degraded.Count == 0
            ? HealthCheckResult.Healthy("all dependencies reachable", data)
            : HealthCheckResult.Degraded($"degraded: {string.Join(", ", degraded)}", data: data);
    }
}
