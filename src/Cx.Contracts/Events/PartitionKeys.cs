namespace Cx.Contracts.Events;

public static class PartitionKeys
{
    public const string Unknown = "unknown";

    /// <summary>tenant_id:customer_id - never null so Kafka partitioning stays deterministic.</summary>
    public static string ForCustomer(string? tenantId, string? customerId) =>
        $"{Norm(tenantId)}:{Norm(customerId)}";

    public static string ForCall(string? callId) => Norm(callId);

    public static string ForEntity(string? entityId) => Norm(entityId);

    private static string Norm(string? value) => string.IsNullOrWhiteSpace(value) ? Unknown : value.Trim();
}
