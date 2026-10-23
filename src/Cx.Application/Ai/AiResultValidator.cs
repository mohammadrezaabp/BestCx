using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Contracts;
using Cx.Contracts.Ai;
using Json.Schema;

namespace Cx.Application.Ai;

public sealed record AiValidationResult(bool IsValid, AiAnalysisResult? Value, IReadOnlyList<string> Errors);

public interface IAiResultValidator
{
    /// <summary>Validates raw model output against the v1 result schema before anything is persisted (spec 21).</summary>
    AiValidationResult Validate(string? rawOutput);

    string SchemaJson { get; }
}

public sealed class AiResultValidator : IAiResultValidator
{
    private static readonly string RawSchema = LoadSchemaText();
    private static readonly JsonSchema Schema = JsonSchema.FromText(RawSchema);
    private static readonly EvaluationOptions EvalOptions = new() { OutputFormat = OutputFormat.List };

    public string SchemaJson => RawSchema;

    public AiValidationResult Validate(string? rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return new AiValidationResult(false, null, new[] { "model returned an empty response" });

        var json = ExtractJson(rawOutput);
        if (json is null)
            return new AiValidationResult(false, null, new[] { "no JSON object found in model response" });

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return new AiValidationResult(false, null, new[] { $"model response is not valid JSON: {ex.Message}" });
        }

        if (node is not JsonObject obj)
            return new AiValidationResult(false, null, new[] { "model response must be a JSON object" });

        Coerce(obj);

        var evaluation = Schema.Evaluate(obj, EvalOptions);
        if (!evaluation.IsValid)
            return new AiValidationResult(false, null, Flatten(evaluation).Take(10).ToList());

        try
        {
            var value = obj.Deserialize<AiAnalysisResult>(CxJson.Options);
            return value is null
                ? new AiValidationResult(false, null, new[] { "model response could not be bound to the result contract" })
                : new AiValidationResult(true, value, Array.Empty<string>());
        }
        catch (JsonException ex)
        {
            return new AiValidationResult(false, null, new[] { ex.Message });
        }
    }

    /// <summary>
    /// Models often wrap JSON in prose or a fenced block. Pull out the first balanced object
    /// rather than failing the job over formatting.
    /// </summary>
    internal static string? ExtractJson(string raw)
    {
        var text = raw.Trim();

        var fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = text.IndexOf('\n', fence);
            var end = text.IndexOf("```", fence + 3, StringComparison.Ordinal);
            if (start > 0 && end > start)
                text = text[(start + 1)..end].Trim();
        }

        var open = text.IndexOf('{');
        if (open < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text[open..(i + 1)];
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Normalizes harmless model drift (casing, missing optional arrays, out-of-range confidence)
    /// so a good answer is not rejected over shape. Unknown enum values still fail validation.
    /// </summary>
    private static void Coerce(JsonObject obj)
    {
        LowerString(obj, "sentiment");
        LowerString(obj, "severity");
        LowerString(obj, "resolution_status");

        obj["analysis_version"] ??= "1.0";
        obj["action_items"] ??= new JsonArray();
        obj["mentioned_products"] ??= new JsonArray();

        if (obj["confidence"] is JsonValue confidence)
        {
            if (confidence.TryGetValue<double>(out var value))
            {
                if (value > 1 && value <= 100) obj["confidence"] = Math.Round(value / 100d, 4);
                else if (value > 1) obj["confidence"] = 1d;
                else if (value < 0) obj["confidence"] = 0d;
            }
            else if (confidence.TryGetValue<string>(out var text) && double.TryParse(text, out var parsed))
            {
                obj["confidence"] = Math.Clamp(parsed > 1 ? parsed / 100d : parsed, 0d, 1d);
            }
        }

        // Empty strings are not valid for required non-nullable fields; treat them as absent
        // so the error names the missing field instead of a length violation.
        foreach (var key in new[] { "subcategory", "journey_stage", "call_reason" })
        {
            if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s))
                obj[key] = null;
        }

        if (obj["key_moments"] is JsonArray moments)
        {
            foreach (var moment in moments.OfType<JsonObject>())
                LowerString(moment, "type");
        }
    }

    private static void LowerString(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && text is not null)
            obj[key] = text.Trim().ToLowerInvariant().Replace(' ', '_');
    }

    private static IEnumerable<string> Flatten(EvaluationResults results)
    {
        if (results.HasErrors && results.Errors is not null)
        {
            foreach (var (keyword, message) in results.Errors)
                yield return $"{results.InstanceLocation}: {keyword} {message}";
        }

        foreach (var child in results.Details)
        {
            foreach (var message in Flatten(child))
                yield return message;
        }
    }

    private static string LoadSchemaText()
    {
        var assembly = typeof(AiAnalysisResult).Assembly;
        var name = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("ai-analysis-result.v1.schema.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
