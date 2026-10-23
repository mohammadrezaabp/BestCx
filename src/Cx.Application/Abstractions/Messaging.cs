namespace Cx.Application.Abstractions;

public sealed record BusMessage(
    string Topic,
    string Key,
    string Value,
    IReadOnlyDictionary<string, string> Headers,
    long Offset = 0,
    int Partition = 0)
{
    public static BusMessage Create(string topic, string key, string value, IReadOnlyDictionary<string, string>? headers = null) =>
        new(topic, key, value, headers ?? new Dictionary<string, string>());
}

public interface IMessagePublisher
{
    Task PublishAsync(string topic, string key, string value, IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default);

    /// <summary>Flush outstanding deliveries; used on graceful shutdown.</summary>
    Task FlushAsync(CancellationToken ct = default);
}

public sealed class SubscriptionOptions
{
    public required IReadOnlyList<string> Topics { get; init; }
    public required string GroupId { get; init; }
    /// <summary>Terminal failures are routed here instead of blocking the partition (spec 31).</summary>
    public string? DeadLetterTopic { get; init; }
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);
}

public interface IMessageSubscriber
{
    /// <summary>
    /// Runs the consume loop until cancellation. The handler must be idempotent:
    /// delivery is at-least-once (rule R4).
    /// </summary>
    Task SubscribeAsync(SubscriptionOptions options, Func<BusMessage, CancellationToken, Task> handler,
        CancellationToken ct);
}
