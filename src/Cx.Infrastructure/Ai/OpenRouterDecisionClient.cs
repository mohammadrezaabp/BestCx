using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Options;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Ai;

/// <summary>
/// OpenRouter's decisions endpoint (Ai:DecisionsUrl). Decisions models such as typesafe/jev
/// answer typed questions - a choice with probabilities, a score, a yes/no probability -
/// and reject chat completions outright.
/// </summary>
public sealed class OpenRouterDecisionClient : IAiDecisionClient
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;

    public OpenRouterDecisionClient(HttpClient http, IOptions<AiOptions> options)
    {
        _options = options.Value;
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        if (!string.IsNullOrWhiteSpace(_options.HttpReferer))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", _options.HttpReferer);
        if (!string.IsNullOrWhiteSpace(_options.AppTitle))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", _options.AppTitle);
    }

    public bool Enabled => true;

    public async Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken ct = default)
    {
        var questions = new JsonObject();
        foreach (var (name, question) in request.Questions)
        {
            var node = new JsonObject { ["type"] = question.Type, ["instructions"] = question.Instructions };
            if (question.Criteria is not null) node["criteria"] = question.Criteria.DeepClone();
            questions[name] = node;
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["state"] = request.State.DeepClone(),
            ["questions"] = questions
        };

        var stopwatch = Stopwatch.StartNew();
        // Not disposed: the resilience handler may resend this same message on a retry.
        var message = new HttpRequestMessage(HttpMethod.Post, _options.DecisionsUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw OpenRouterCompletionClient.MapError(response.StatusCode, payload, request.Model);

        return Parse(payload, request.Model, stopwatch.ElapsedMilliseconds);
    }

    public static DecisionResponse Parse(string payload, string requestedModel, long latencyMs)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            var text = error.TryGetProperty("message", out var m) ? m.GetString() : "unknown provider error";
            throw new CxException(ErrorCategory.PermanentProvider, $"Decisions model returned an error: {text}");
        }

        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        if (root.TryGetProperty("answers", out var list) && list.ValueKind == JsonValueKind.Object)
        {
            foreach (var answer in list.EnumerateObject())
            {
                var a = answer.Value;
                answers[answer.Name] = new DecisionAnswer(
                    Text(a, "type") ?? string.Empty,
                    Text(a, "choice"),
                    Number(a, "score"),
                    Number(a, "noul"),
                    Number(a, "confidence") is { } c ? Math.Round((decimal)c, 4) : null);
            }
        }

        var model = Text(root, "model") ?? requestedModel;
        var inputTokens = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("input_tokens", out var t) &&
                          t.TryGetInt32(out var tokens) ? tokens : 0;
        return new DecisionResponse(model, answers, inputTokens, latencyMs);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        double.TryParse(v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}

/// <summary>Offline mode: no decisions provider, so decisions go through the chat path (the stub).</summary>
public sealed class DisabledDecisionClient : IAiDecisionClient
{
    public bool Enabled => false;

    public Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken ct = default) =>
        throw new InvalidOperationException("No decisions provider is configured.");
}
