using System.Collections.Concurrent;
using System.Threading.Channels;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Microsoft.Extensions.Logging;

namespace Cx.Infrastructure.Messaging;

/// <summary>
/// In-process bus with the same at-least-once and DLQ semantics as Kafka.
/// It exists so the pipeline can be exercised in unit and integration tests without a broker;
/// it is not a production transport and keeps no data across a restart.
/// </summary>
public sealed class InMemoryBus : IMessagePublisher, IMessageSubscriber
{
    private readonly ConcurrentDictionary<string, Channel<BusMessage>> _topics = new();
    private readonly ConcurrentDictionary<string, long> _offsets = new();
    private readonly ILogger<InMemoryBus> _logger;

    public InMemoryBus(ILogger<InMemoryBus> logger) => _logger = logger;

    public Task PublishAsync(string topic, string key, string value,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        var offset = _offsets.AddOrUpdate(topic, 0, (_, current) => current + 1);
        var message = new BusMessage(topic, key, value,
            headers ?? new Dictionary<string, string>(), offset);

        return Channel(topic).Writer.WriteAsync(message, ct).AsTask();
    }

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task SubscribeAsync(SubscriptionOptions options, Func<BusMessage, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        var readers = options.Topics.Select(t => ReadTopicAsync(t, options, handler, ct));
        await Task.WhenAll(readers);
    }

    private async Task ReadTopicAsync(string topic, SubscriptionOptions options,
        Func<BusMessage, CancellationToken, Task> handler, CancellationToken ct)
    {
        var reader = Channel(topic).Reader;

        try
        {
            await foreach (var message in reader.ReadAllAsync(ct))
            {
                var delay = options.InitialBackoff;
                Exception? lastError = null;
                var handled = false;

                for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
                {
                    try
                    {
                        await handler(message, ct);
                        handled = true;
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        if (ex is CxException { IsRetryable: false } || attempt == options.MaxAttempts) break;
                        await Task.Delay(delay, ct);
                        delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2,
                            options.MaxBackoff.TotalMilliseconds));
                    }
                }

                if (!handled && options.DeadLetterTopic is not null)
                {
                    await PublishAsync(options.DeadLetterTopic, message.Key, message.Value, message.Headers, ct);
                    _logger.LogError(lastError, "[{Group}] {Topic} -> {Dlq}", options.GroupId, topic,
                        options.DeadLetterTopic);
                }
                else if (!handled)
                {
                    _logger.LogError(lastError, "[{Group}] dropped message from {Topic}", options.GroupId, topic);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    private Channel<BusMessage> Channel(string topic) =>
        _topics.GetOrAdd(topic, _ => System.Threading.Channels.Channel.CreateUnbounded<BusMessage>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false }));
}
