using System.Text;
using Confluent.Kafka;
using Cx.Application.Abstractions;
using Cx.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Messaging;

/// <summary>
/// Idempotent producer with acks=all (technical design 8.3), so a retry inside the client
/// cannot duplicate a message on the partition.
/// </summary>
public sealed class KafkaPublisher : IMessagePublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaPublisher> _logger;

    public KafkaPublisher(IOptions<KafkaOptions> options, ILogger<KafkaPublisher> logger)
    {
        _logger = logger;
        var o = options.Value;

        var config = new ProducerConfig
        {
            BootstrapServers = o.BootstrapServers,
            ClientId = o.ClientId,
            EnableIdempotence = o.EnableIdempotence,
            Acks = ParseAcks(o.Acks),
            MessageTimeoutMs = o.MessageTimeoutMs,
            MessageSendMaxRetries = 10,
            RetryBackoffMs = 200,
            CompressionType = CompressionType.Snappy,
            LingerMs = 5
        };

        KafkaSecurity.Apply(config, o);
        _producer = new ProducerBuilder<string, string>(config)
            .SetErrorHandler((_, e) => _logger.LogWarning("Kafka producer error: {Reason}", e.Reason))
            .Build();
    }

    public async Task PublishAsync(string topic, string key, string value,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        var message = new Message<string, string> { Key = key, Value = value };

        if (headers is { Count: > 0 })
        {
            message.Headers = new Headers();
            foreach (var (name, headerValue) in headers)
                message.Headers.Add(name, Encoding.UTF8.GetBytes(headerValue));
        }

        var result = await _producer.ProduceAsync(topic, message, ct);

        if (result.Status == PersistenceStatus.NotPersisted)
            throw new InvalidOperationException($"Kafka did not persist the message on topic '{topic}'");
    }

    public Task FlushAsync(CancellationToken ct = default)
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try
        {
            _producer.Flush(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka producer flush failed during shutdown");
        }

        _producer.Dispose();
    }

    private static Acks ParseAcks(string value) => value.ToLowerInvariant() switch
    {
        "all" or "-1" => Acks.All,
        "1" => Acks.Leader,
        "0" => Acks.None,
        _ => Acks.All
    };
}

internal static class KafkaSecurity
{
    /// <summary>SASL/TLS settings are configuration-only so production can enable Kafka auth.</summary>
    public static void Apply(ClientConfig config, KafkaOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SecurityProtocol) &&
            Enum.TryParse<SecurityProtocol>(options.SecurityProtocol, true, out var protocol))
            config.SecurityProtocol = protocol;

        if (!string.IsNullOrWhiteSpace(options.SaslMechanism) &&
            Enum.TryParse<SaslMechanism>(options.SaslMechanism, true, out var mechanism))
            config.SaslMechanism = mechanism;

        if (!string.IsNullOrWhiteSpace(options.SaslUsername)) config.SaslUsername = options.SaslUsername;
        if (!string.IsNullOrWhiteSpace(options.SaslPassword)) config.SaslPassword = options.SaslPassword;
    }
}
