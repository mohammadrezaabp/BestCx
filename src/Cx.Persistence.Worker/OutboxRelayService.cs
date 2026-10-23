using Cx.Application.Abstractions;
using Cx.Application.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cx.Persistence.Worker;

public sealed class OutboxRelayOptions
{
    public const string Section = "OutboxRelay";

    /// <summary>
    /// Turn this off when Debezium owns publication. The reference deployment ships the
    /// Debezium outbox-router config in deploy/debezium, but defaults to this in-process
    /// relay so the single image runs the full flow with one less moving part.
    /// </summary>
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 200;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan IdleInterval { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Publishes committed outbox rows to Kafka.
/// Equivalent in effect to the Debezium outbox event router: it reads only committed rows,
/// so a rolled-back transaction can never be published, and it marks rows published only
/// after the broker acks. A crash between publish and mark causes redelivery, which
/// downstream consumers absorb (rule R4).
/// </summary>
public sealed class OutboxRelayService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMessagePublisher _publisher;
    private readonly OutboxRelayOptions _options;
    private readonly ILogger<OutboxRelayService> _logger;

    public OutboxRelayService(
        IServiceScopeFactory scopeFactory,
        IMessagePublisher publisher,
        Microsoft.Extensions.Options.IOptions<OutboxRelayOptions> options,
        ILogger<OutboxRelayService> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Outbox relay disabled; Debezium is expected to publish cx_outbox");
            return;
        }

        _logger.LogInformation("Outbox relay started (batch {Batch}, poll {Poll})",
            _options.BatchSize, _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;

            try
            {
                published = await PumpAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox relay iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            // Back off when idle, run flat out while there is a backlog.
            await Task.Delay(published > 0 ? _options.PollInterval : _options.IdleInterval, stoppingToken);
        }
    }

    private async Task<int> PumpAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();

        var batch = await outbox.FetchUnpublishedAsync(_options.BatchSize, ct);
        if (batch.Count == 0) return 0;

        var publishedIds = new List<Guid>(batch.Count);

        foreach (var row in batch)
        {
            try
            {
                await _publisher.PublishAsync(row.Topic, row.PartitionKey, row.Payload,
                    new Dictionary<string, string>
                    {
                        ["aggregate_type"] = row.AggregateType,
                        ["aggregate_id"] = row.AggregateId,
                        ["event_type"] = row.EventType,
                        ["outbox_id"] = row.Id.ToString()
                    }, ct);

                publishedIds.Add(row.Id);
            }
            catch (Exception ex)
            {
                // Stop at the first failure: preserving per-key ordering matters more than
                // draining the batch, and the rows stay unpublished for the next pass.
                _logger.LogError(ex, "Failed to publish outbox row {OutboxId} to {Topic}", row.Id, row.Topic);
                break;
            }
        }

        if (publishedIds.Count > 0)
        {
            await outbox.MarkPublishedAsync(publishedIds, ct);
            CxMetrics.OutboxPublished.Add(publishedIds.Count);
        }

        return publishedIds.Count;
    }
}
