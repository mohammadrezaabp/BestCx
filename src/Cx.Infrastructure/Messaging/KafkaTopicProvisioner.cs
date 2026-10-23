using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Cx.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Messaging;

/// <summary>
/// Creates the platform topics if they are missing. Retention and partition counts stay
/// configurable (spec 37.7); in a managed cluster this is turned off and topics are
/// provisioned by the platform team instead.
/// </summary>
public sealed class KafkaTopicProvisioner
{
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaTopicProvisioner> _logger;

    public KafkaTopicProvisioner(IOptions<KafkaOptions> options, ILogger<KafkaTopicProvisioner> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureTopicsAsync(CancellationToken ct = default)
    {
        if (!_options.AutoCreateTopics) return;

        var config = new AdminClientConfig
        {
            BootstrapServers = _options.BootstrapServers,
            ClientId = $"{_options.ClientId}-admin"
        };
        KafkaSecurity.Apply(config, _options);

        using var admin = new AdminClientBuilder(config).Build();

        var metadata = admin.GetMetadata(TimeSpan.FromSeconds(20));
        var existing = metadata.Topics.Select(t => t.Topic).ToHashSet(StringComparer.Ordinal);

        var missing = _options.Topics.All()
            .Distinct(StringComparer.Ordinal)
            .Where(t => !existing.Contains(t))
            .Select(t => new TopicSpecification
            {
                Name = t,
                NumPartitions = _options.DefaultPartitions,
                ReplicationFactor = _options.DefaultReplicationFactor
            })
            .ToList();

        if (missing.Count == 0)
        {
            _logger.LogInformation("All {Count} Kafka topics already exist", _options.Topics.All().Count());
            return;
        }

        try
        {
            await admin.CreateTopicsAsync(missing, new CreateTopicsOptions { RequestTimeout = TimeSpan.FromSeconds(30) });
            _logger.LogInformation("Created Kafka topics: {Topics}", string.Join(", ", missing.Select(t => t.Name)));
        }
        catch (CreateTopicsException ex)
        {
            foreach (var result in ex.Results.Where(r => r.Error.Code != ErrorCode.TopicAlreadyExists))
                _logger.LogError("Failed to create topic {Topic}: {Reason}", result.Topic, result.Error.Reason);

            // Another instance of the same image winning the race is the expected case.
            if (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
                _logger.LogInformation("Topics were created concurrently by another instance");
        }
    }

    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        var config = new AdminClientConfig { BootstrapServers = _options.BootstrapServers };
        KafkaSecurity.Apply(config, _options);

        try
        {
            using var admin = new AdminClientBuilder(config).Build();
            var metadata = await Task.Run(() => admin.GetMetadata(TimeSpan.FromSeconds(5)), ct);
            return metadata.Brokers.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Kafka is not reachable at {Servers}", _options.BootstrapServers);
            return false;
        }
    }
}
