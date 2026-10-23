using System.Text.Json.Nodes;

namespace Cx.Application.Abstractions;

/// <summary>
/// One question to a decisions model. <c>choice</c> criteria map each option key to its
/// description; <c>score</c> criteria are an ordered list of labels; <c>noul</c> is a yes/no
/// probability.
/// </summary>
public sealed class DecisionQuestion
{
    public const string Choice = "choice";
    public const string Score = "score";
    public const string Noul = "noul";

    public required string Type { get; init; }
    public required string Instructions { get; init; }
    public JsonNode? Criteria { get; init; }
}

public sealed class DecisionRequest
{
    public required string Model { get; init; }
    /// <summary>The situation being judged (string, object or array).</summary>
    public required JsonNode State { get; init; }
    public required IReadOnlyDictionary<string, DecisionQuestion> Questions { get; init; }
}

public sealed record DecisionAnswer(
    string Type,
    string? Choice,
    double? Score,
    double? Noul,
    decimal? Confidence);

public sealed record DecisionResponse(
    string Model,
    IReadOnlyDictionary<string, DecisionAnswer> Answers,
    int InputTokens,
    long LatencyMs);

/// <summary>
/// A model that returns typed decisions instead of text (OpenRouter's decisions endpoint,
/// e.g. typesafe/jev). It cannot be reached through chat completions.
/// </summary>
public interface IAiDecisionClient
{
    /// <summary>False when no provider is configured (offline stub mode).</summary>
    bool Enabled { get; }
    Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken ct = default);
}
