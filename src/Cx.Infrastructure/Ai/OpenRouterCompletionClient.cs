using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Ai;

/// <summary>
/// OpenRouter chat-completions client.
/// OpenRouter exposes an OpenAI-compatible surface, so the same implementation also works
/// against OpenAI or any compatible gateway by changing BaseUrl - the provider choice is
/// configuration, not code (spec 41.6).
/// </summary>
public sealed class OpenRouterCompletionClient : IAiCompletionClient
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;
    private readonly ILogger<OpenRouterCompletionClient> _logger;

    public OpenRouterCompletionClient(HttpClient http, IOptions<AiOptions> options,
        ILogger<OpenRouterCompletionClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = http;

        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        // OpenRouter attribution headers - optional, and never carry customer data.
        if (!string.IsNullOrWhiteSpace(_options.HttpReferer))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", _options.HttpReferer);
        if (!string.IsNullOrWhiteSpace(_options.AppTitle))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", _options.AppTitle);
    }

    public string Provider => _options.Provider;
    public string DefaultModel => _options.Model;

    public async Task<AiCompletion> CompleteJsonAsync(AiCompletionRequest request, CancellationToken ct = default)
    {
        var model = request.ModelOverride ?? _options.Model;
        var stopwatch = Stopwatch.StartNew();

        var body = BuildRequest(model, request, structuredOutput: request.JsonSchema is not null);

        var response = await SendAsync(body, ct);

        // Not every model on OpenRouter supports json_schema response_format. If the router
        // rejects it, fall back to prompt-enforced JSON rather than failing the job.
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity &&
            request.JsonSchema is not null)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Model {Model} rejected structured output; retrying with json_object. Detail: {Detail}",
                model, Truncate(detail, 400));
            response.Dispose();
            body = BuildRequest(model, request, structuredOutput: false);
            response = await SendAsync(body, ct);
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw MapError(response.StatusCode, payload, model);

            stopwatch.Stop();
            var completion = Parse(payload, model, stopwatch.ElapsedMilliseconds);

            // OpenRouter answers from the fallback model without an error when the primary
            // fails; say so, or a broken primary goes unnoticed while costs and quality shift.
            if (!completion.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Requested model {Model} but {Served} answered (fallback)",
                    model, completion.Model);
            }

            return completion;
        }
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) return false;

        try
        {
            using var response = await _http.GetAsync("models", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AI provider health check failed");
            return false;
        }
    }

    private Task<HttpResponseMessage> SendAsync(JsonObject body, CancellationToken ct)
    {
        // Deliberately not disposed here: the resilience handler may retry this same
        // request, and disposing it (and its content) would fail every retry with
        // ObjectDisposedException. HttpClient owns the message for the call's lifetime.
        var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        return _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
    }

    private JsonObject BuildRequest(string model, AiCompletionRequest request, bool structuredOutput)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = (double)request.Temperature,
            ["max_tokens"] = request.MaxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt })
        };

        if (structuredOutput && request.JsonSchema is not null)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.JsonSchemaName,
                    ["strict"] = false,
                    ["schema"] = ProviderSchema(request.JsonSchema)
                }
            };
        }
        else
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        var fallback = request.FallbackModelOverride ?? _options.FallbackModel;
        if (!string.IsNullOrWhiteSpace(fallback) && fallback != model)
        {
            // OpenRouter routes to the next model when the first is unavailable.
            body["models"] = new JsonArray(model, fallback);
        }

        return body;
    }

    /// <summary>
    /// The structural part of our schema, in the subset every structured-output provider
    /// accepts. Gemini rejects the whole request (400 INVALID_ARGUMENT) for "$schema",
    /// union types like ["string","null"], and length/range keywords. Those constraints
    /// are not lost: AiResultValidator checks the full schema on every response.
    /// </summary>
    public static JsonNode ProviderSchema(string schemaJson)
    {
        var node = JsonNode.Parse(schemaJson)!;
        Simplify(node);
        return node;

        static void Simplify(JsonNode? current)
        {
            if (current is JsonObject obj)
            {
                foreach (var keyword in UnsupportedKeywords) obj.Remove(keyword);

                if (obj["type"] is JsonArray types)
                {
                    var concrete = types.Select(t => t?.GetValue<string>()).FirstOrDefault(t => t != "null");
                    obj["type"] = concrete ?? "string";
                }

                foreach (var (_, child) in obj.ToList()) Simplify(child);
            }
            else if (current is JsonArray array)
            {
                foreach (var child in array) Simplify(child);
            }
        }
    }

    private static readonly string[] UnsupportedKeywords =
    {
        "$schema", "$id", "title", "minLength", "maxLength", "minItems", "maxItems", "minimum", "maximum"
    };

    private static AiCompletion Parse(string payload, string requestedModel, long latencyMs)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : "unknown provider error";
            throw new CxException(ErrorCategory.PermanentProvider, $"AI provider returned an error: {message}");
        }

        var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                      ?? string.Empty;

        var servedModel = root.TryGetProperty("model", out var modelProperty)
            ? modelProperty.GetString() ?? requestedModel
            : requestedModel;

        var promptTokens = 0;
        var completionTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            promptTokens = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
            completionTokens = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0;
        }

        // The served model id is the real version identifier for governance (spec 22);
        // OpenRouter may route "…:floor" or a dated variant behind the requested name.
        var modelName = servedModel.Split('@')[0];
        var modelVersion = servedModel == modelName ? null : servedModel;

        return new AiCompletion(content, "openrouter", modelName, modelVersion ?? servedModel,
            promptTokens, completionTokens, latencyMs);
    }

    internal static CxException MapError(HttpStatusCode status, string payload, string model)
    {
        var detail = Truncate(payload, 500);

        return status switch
        {
            HttpStatusCode.TooManyRequests =>
                new CxException(ErrorCategory.RateLimit, $"AI provider rate limited model '{model}': {detail}"),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new CxException(ErrorCategory.Auth, $"AI provider rejected the API key: {detail}"),
            HttpStatusCode.PaymentRequired =>
                new CxException(ErrorCategory.PermanentProvider, $"AI provider credit exhausted: {detail}"),
            >= HttpStatusCode.InternalServerError =>
                new CxException(ErrorCategory.Provider5xx, $"AI provider error {(int)status}: {detail}"),
            _ => new CxException(ErrorCategory.PermanentProvider, $"AI provider error {(int)status}: {detail}")
        };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
