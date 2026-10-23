using Cx.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cx.Infrastructure.Messaging;

/// <summary>
/// Base class for every pipeline consumer. Handles subscription lifecycle, per-message
/// DI scoping and restart-on-failure so each worker only implements its handler.
/// </summary>
public abstract class CxConsumerService : BackgroundService
{
    protected CxConsumerService(IServiceScopeFactory scopeFactory, IMessageSubscriber subscriber, ILogger logger)
    {
        ScopeFactory = scopeFactory;
        Subscriber = subscriber;
        Logger = logger;
    }

    protected IServiceScopeFactory ScopeFactory { get; }
    protected IMessageSubscriber Subscriber { get; }
    protected ILogger Logger { get; }

    protected abstract SubscriptionOptions Subscription { get; }

    /// <summary>Runs inside a fresh DI scope per message. Must be idempotent (rule R4).</summary>
    protected abstract Task HandleAsync(BusMessage message, IServiceProvider services, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish start-up (migrations, topic creation) before consuming.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Subscriber.SubscribeAsync(Subscription, async (message, ct) =>
                {
                    await using var scope = ScopeFactory.CreateAsyncScope();
                    await HandleAsync(message, scope.ServiceProvider, ct);
                }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A broker outage must not kill the worker permanently.
                Logger.LogError(ex, "[{Group}] consumer loop crashed; restarting in 5s", Subscription.GroupId);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        Logger.LogInformation("[{Group}] consumer stopped", Subscription.GroupId);
    }
}
