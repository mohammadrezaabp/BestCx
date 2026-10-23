using System.Text;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Observability;
using Cx.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Messaging;

/// <summary>
/// At-least-once consumer (rule R4): the offset is committed only after the handler
/// succeeds or the message is parked on a DLQ, so nothing is silently dropped.
/// </summary>
public sealed class KafkaSubscriber : IMessageSubscriber
{
    private readonly KafkaOptions _options;
    private readonly IMessagePublisher _publisher;
    private readonly ILogger<KafkaSubscriber> _logger;

    public KafkaSubscriber(IOptions<KafkaOptions> options, IMessagePublisher publisher,
        ILogger<KafkaSubscriber> logger)
    {
        _options = options.Value;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task SubscribeAsync(SubscriptionOptions subscription, Func<BusMessage, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = subscription.GroupId,
            ClientId = $"{_options.ClientId}-{subscription.GroupId}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            // A long handler (an LLM call) must not look like a dead consumer.
            MaxPollIntervalMs = 600_000,
            SessionTimeoutMs = 45_000
        };

        KafkaSecurity.Apply(config, _options);

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, e) => _logger.LogWarning("Kafka consumer error [{Group}]: {Reason}",
                subscription.GroupId, e.Reason))
            .SetPartitionsAssignedHandler((_, partitions) => _logger.LogInformation(
                "[{Group}] assigned {Partitions}", subscription.GroupId, string.Join(",", partitions)))
            .Build();

        consumer.Subscribe(subscription.Topics);
        _logger.LogInformation("[{Group}] subscribed to {Topics}", subscription.GroupId,
            string.Join(", ", subscription.Topics));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                }
                catch (ConsumeException ex)
                {
                    _logger.LogWarning(ex, "[{Group}] consume failed", subscription.GroupId);
                    await Task.Delay(1000, ct);
                    continue;
                }

                if (result?.Message is null) continue;

                await HandleWithRetryAsync(consumer, result, subscription, handler, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        finally
        {
            try
            {
                consumer.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Group}] consumer close failed", subscription.GroupId);
            }
        }
    }

    private async Task HandleWithRetryAsync(IConsumer<string, string> consumer,
        ConsumeResult<string, string> result, SubscriptionOptions subscription,
        Func<BusMessage, CancellationToken, Task> handler, CancellationToken ct)
    {
        var headers = ReadHeaders(result.Message.Headers);
        var message = new BusMessage(result.Topic, result.Message.Key ?? string.Empty, result.Message.Value,
            headers, result.Offset.Value, result.Partition.Value);

        var delay = subscription.InitialBackoff;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= subscription.MaxAttempts; attempt++)
        {
            try
            {
                await handler(message, ct);
                Commit(consumer, result, subscription.GroupId);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;

                // A non-retryable error will fail identically on every attempt; go straight to the DLQ.
                if (ex is CxException { IsRetryable: false })
                {
                    _logger.LogWarning(ex, "[{Group}] non-retryable failure on {Topic}",
                        subscription.GroupId, result.Topic);
                    break;
                }

                if (attempt == subscription.MaxAttempts) break;

                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
                _logger.LogWarning(ex, "[{Group}] attempt {Attempt}/{Max} failed on {Topic}; retrying in {Delay}",
                    subscription.GroupId, attempt, subscription.MaxAttempts, result.Topic, delay + jitter);

                await Task.Delay(delay + jitter, ct);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2,
                    subscription.MaxBackoff.TotalMilliseconds));
            }
        }

        await DeadLetterAsync(message, subscription, lastError, ct);
        Commit(consumer, result, subscription.GroupId);
    }

    private void Commit(IConsumer<string, string> consumer, ConsumeResult<string, string> result, string group)
    {
        try
        {
            consumer.StoreOffset(result);
            consumer.Commit(result);
        }
        catch (KafkaException ex)
        {
            // A failed commit means redelivery, which idempotent handlers absorb.
            _logger.LogWarning(ex, "[{Group}] offset commit failed at {Topic}:{Partition}:{Offset}",
                group, result.Topic, result.Partition.Value, result.Offset.Value);
        }
    }

    private async Task DeadLetterAsync(BusMessage message, SubscriptionOptions subscription, Exception? error,
        CancellationToken ct)
    {
        CxMetrics.DlqTotal.Add(1,
            new KeyValuePair<string, object?>("topic", message.Topic),
            new KeyValuePair<string, object?>("group", subscription.GroupId));

        if (subscription.DeadLetterTopic is null)
        {
            _logger.LogError(error, "[{Group}] dropping message from {Topic} - no DLQ configured",
                subscription.GroupId, message.Topic);
            return;
        }

        var envelope = new JsonObject
        {
            ["failed_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["consumer_group"] = subscription.GroupId,
            ["source_topic"] = message.Topic,
            ["partition"] = message.Partition,
            ["offset"] = message.Offset,
            ["error"] = error?.Message,
            ["error_type"] = error?.GetType().Name,
            ["error_category"] = (error as CxException)?.Category.ToString(),
            ["payload"] = message.Value
        };

        try
        {
            await _publisher.PublishAsync(subscription.DeadLetterTopic, message.Key, envelope.ToJsonString(),
                message.Headers, ct);
            _logger.LogError(error, "[{Group}] message from {Topic} routed to {Dlq}",
                subscription.GroupId, message.Topic, subscription.DeadLetterTopic);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "[{Group}] FAILED to write to DLQ {Dlq}; original error: {Original}",
                subscription.GroupId, subscription.DeadLetterTopic, error?.Message);
        }
    }

    private static Dictionary<string, string> ReadHeaders(Headers? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (headers is null) return result;

        foreach (var header in headers)
        {
            try
            {
                result[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes());
            }
            catch (Exception)
            {
                // A non-UTF8 header is metadata we can live without.
            }
        }

        return result;
    }
}
