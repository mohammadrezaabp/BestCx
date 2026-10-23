using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cx.Infrastructure.Operations;

public sealed class OperationsWorker(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<OperationsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var started = lifetime.ApplicationStarted.Register(() => ready.TrySetResult());
        using var stopped = stoppingToken.Register(() => ready.TrySetCanceled(stoppingToken));
        await ready.Task;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<OperationsStore>().TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Operational CX processing failed; the next tick will retry"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
