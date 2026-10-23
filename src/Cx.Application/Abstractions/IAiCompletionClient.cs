namespace Cx.Application.Abstractions;

public sealed class AiCompletionRequest
{
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }
    /// <summary>When set, the provider is asked for structured output matching this schema.</summary>
    public string? JsonSchema { get; init; }
    public string JsonSchemaName { get; init; } = "cx_analysis_result";
    public decimal Temperature { get; init; } = 0.1m;
    public int MaxTokens { get; init; } = 1200;
    public string? ModelOverride { get; init; }
    /// <summary>Model OpenRouter switches to when the requested one is unavailable; null means Ai:FallbackModel.</summary>
    public string? FallbackModelOverride { get; init; }
}

public sealed record AiCompletion(
    string Content,
    string Provider,
    string Model,
    string? ModelVersion,
    int PromptTokens,
    int CompletionTokens,
    long LatencyMs);

/// <summary>
/// LLM provider behind an interface (spec 37.9). The default implementation targets
/// OpenRouter; swapping providers is a configuration change, not a code change.
/// </summary>
public interface IAiCompletionClient
{
    string Provider { get; }
    string DefaultModel { get; }
    Task<AiCompletion> CompleteJsonAsync(AiCompletionRequest request, CancellationToken ct = default);
    Task<bool> HealthCheckAsync(CancellationToken ct = default);
}
