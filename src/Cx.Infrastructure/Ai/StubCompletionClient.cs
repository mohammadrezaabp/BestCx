using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cx.Application.Abstractions;
using Cx.Application.Ai;
using Cx.Contracts.Ai;

namespace Cx.Infrastructure.Ai;

/// <summary>
/// Deterministic offline stand-in for the LLM. Used when Ai:Enabled is false or no API key
/// is configured, so the full pipeline (orchestrator to review queue) can be exercised and
/// tested without calling a provider. It is heuristic, not intelligent, and always reports
/// low confidence so its output lands in the human review queue.
/// </summary>
public sealed class StubCompletionClient : IAiCompletionClient
{
    public string Provider => "stub";
    public string DefaultModel => "cx-heuristic-stub";

    public Task<AiCompletion> CompleteJsonAsync(AiCompletionRequest request, CancellationToken ct = default)
    {
        var text = request.UserPrompt.ToLowerInvariant();

        var negativeSignals = new[]
        {
            "failed", "error", "timeout", "unresolved", "complaint", "cannot", "broken",
            "Error", "Problem", "Interruption", "It does not work", "Complaint", "Dissatisfied"
        };
        var positiveSignals = new[] { "resolved", "succeeded", "thanks", "Thank you", "Excellent", "Solved" };

        var negatives = negativeSignals.Count(s => text.Contains(s, StringComparison.Ordinal));
        var positives = positiveSignals.Count(s => text.Contains(s, StringComparison.Ordinal));

        var sentiment = (negatives, positives) switch
        {
            (0, 0) => Sentiments.Unknown,
            var (n, p) when n > p => Sentiments.Negative,
            var (n, p) when p > n => Sentiments.Positive,
            _ => Sentiments.Mixed
        };

        if (request.JsonSchemaName == DecisionMaker.SchemaName)
            return Task.FromResult(Decide(request, text, sentiment, negatives, positives));

        var category = text switch
        {
            _ when text.Contains("auth") || text.Contains("login") || text.Contains("Verification") => "authentication",
            _ when text.Contains("payment") || text.Contains("Payment") => "payment",
            _ when text.Contains("invoice") || text.Contains("billing") || text.Contains("Billing") => "billing",
            _ when text.Contains("api") || text.Contains("webhook") => "integration",
            _ when text.Contains("timeout") || text.Contains("slow") || text.Contains("Slow down") => "performance",
            _ => "other"
        };

        var isVoice = request.SystemPrompt.Contains("recorded phone call", StringComparison.Ordinal);

        var result = new JsonObject
        {
            ["analysis_version"] = "1.0",
            ["sentiment"] = sentiment,
            ["intent"] = negatives > 0 ? "technical_support" : "information_request",
            ["category"] = category,
            ["subcategory"] = null,
            ["severity"] = negatives >= 3 ? Severities.High : negatives > 0 ? Severities.Medium : Severities.Low,
            ["journey_stage"] = null,
            ["resolution_status"] = positives > negatives
                ? ResolutionStatuses.Resolved
                : ResolutionStatuses.Unknown,
            ["summary"] = "Offline analysis (stub): This result was produced without calling the model and should be checked by an expert.",
            ["action_items"] = new JsonArray("Manual review by an expert CX"),
            ["mentioned_products"] = new JsonArray(),
            // Deliberately below the default review threshold: stub output must never be
            // mistaken for a real classification.
            ["confidence"] = 0.2
        };

        if (isVoice)
        {
            result["call_reason"] = "Unknown (stub)";
            result["follow_up_required"] = true;
            result["key_moments"] = new JsonArray();
        }

        return Task.FromResult(new AiCompletion(result.ToJsonString(), Provider, DefaultModel, "stub-1",
            0, 0, 1));
    }

    /// <summary>
    /// Picks the topic whose title or example phrases occur most often in the interaction.
    /// Word matching is exactly what the real decision prompt forbids; it is good enough to
    /// keep an offline demo populated, and the low confidence sends it all to review anyway.
    /// </summary>
    private AiCompletion Decide(AiCompletionRequest request, string text, string sentiment, int negatives, int positives)
    {
        var interaction = text[..Math.Max(0, text.IndexOf("make the decisions now", StringComparison.Ordinal))];
        string? best = null;
        var bestScore = 0;
        foreach (Match line in TopicLine.Matches(request.SystemPrompt))
        {
            var score = line.Groups["words"].Value
                .Split(new[] { ' ', '—', '(', ')', ';', ':', ',', ',', '›' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 3 && w != "examples")
                .Count(w => interaction.Contains(w.ToLowerInvariant(), StringComparison.Ordinal));
            if (score > bestScore)
            {
                bestScore = score;
                best = line.Groups["code"].Value;
            }
        }

        var decision = new JsonObject
        {
            ["topic"] = best ?? DecisionMaker.NoTopic,
            ["sentiment"] = sentiment,
            ["intent"] = negatives > 0 ? "technical_support" : "information_request",
            ["severity"] = negatives >= 3 ? Severities.High : negatives > 0 ? Severities.Medium : Severities.Low,
            ["resolution_status"] = positives > negatives ? ResolutionStatuses.Resolved : ResolutionStatuses.Unknown,
            ["journey_stage"] = null,
            ["follow_up_required"] = null,
            ["confidence"] = 0.2,
            ["reason"] = "Offline decision (stub) Based on matching words; It should be checked by an expert."
        };
        return new AiCompletion(decision.ToJsonString(), Provider, DefaultModel, "stub-1", 0, 0, 1);
    }

    private static readonly Regex TopicLine =
        new(@"^\s*- \[(?<code>[^\]]+)\] (?<words>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
}
