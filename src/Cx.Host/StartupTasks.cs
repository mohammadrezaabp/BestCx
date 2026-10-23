using Cx.Application.Abstractions;
using Cx.Infrastructure.Database;
using Cx.Infrastructure.Messaging;
using Cx.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cx.Host;

/// <summary>
/// Brings the dependencies up before traffic is served: migrations, Kafka topics and the
/// storage bucket. Everything here is idempotent, because several replicas of the same
/// image may start at once.
/// </summary>
public sealed class StartupTasks : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly CxRoleSet _roles;
    private readonly ILogger<StartupTasks> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public StartupTasks(IServiceProvider services, CxRoleSet roles, ILogger<StartupTasks> logger,
        IHostApplicationLifetime lifetime)
    {
        _services = services;
        _roles = roles;
        _logger = logger;
        _lifetime = lifetime;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("BestCx starting with roles: {Roles}", _roles);

        await using var scope = _services.CreateAsyncScope();

        await RunAsync("database migration", async () =>
        {
            var migrator = scope.ServiceProvider.GetRequiredService<DatabaseMigrator>();
            await migrator.MigrateAsync(ct);
        }, required: true, ct);

        await RunAsync("admin account bootstrap", async () =>
        {
            await scope.ServiceProvider.GetRequiredService<UserAccounts>().EnsureAdminsAsync(ct);
        }, required: true, ct);

        var mockData = scope.ServiceProvider.GetRequiredService<MockDataSeeder>();
        if (mockData.Enabled)
        {
            await RunAsync("database mock-data seed", async () =>
            {
                await mockData.SeedAsync(ct);
            }, required: true, ct);
        }

        var provisioner = scope.ServiceProvider.GetService<KafkaTopicProvisioner>();
        if (provisioner is not null)
        {
            await RunAsync("kafka topic provisioning", async () =>
            {
                await provisioner.EnsureTopicsAsync(ct);
            }, required: false, ct);
        }

        await RunAsync("object storage bucket", async () =>
        {
            var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
            await storage.EnsureBucketAsync(ct);
        }, required: false, ct);

        _logger.LogInformation("Startup tasks complete");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Waits for a dependency that is still coming up. A required task that never succeeds
    /// stops the host: running without a schema would corrupt data, which is worse than
    /// failing loudly.
    /// </summary>
    private async Task RunAsync(string name, Func<Task> action, bool required, CancellationToken ct)
    {
        const int maxAttempts = 30;
        var delay = TimeSpan.FromSeconds(1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await action();
                if (attempt > 1) _logger.LogInformation("{Task} succeeded on attempt {Attempt}", name, attempt);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("{Task} not ready (attempt {Attempt}/{Max}): {Message}",
                    name, attempt, maxAttempts, ex.Message);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 10));
            }
            catch (Exception ex)
            {
                if (required)
                {
                    _logger.LogCritical(ex, "{Task} failed; the host cannot start safely", name);
                    _lifetime.StopApplication();
                    throw;
                }

                _logger.LogError(ex, "{Task} failed; continuing without it", name);
                return;
            }
        }
    }
}
