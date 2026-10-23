using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Options;
using Cx.Contracts.Events;
using Json.Schema;
using Microsoft.Extensions.Options;

namespace Cx.Application.Ingestion;

public sealed record EventValidationResult(bool IsValid, IReadOnlyList<string> Errors, string? RejectReason)
{
    public static readonly EventValidationResult Valid = new(true, Array.Empty<string>(), null);

    public static EventValidationResult Invalid(string reason, params string[] errors) =>
        new(false, errors.Length == 0 ? new[] { reason } : errors, reason);
}

public static class RejectReasons
{
    public const string SchemaInvalid = "schema_invalid";
    public const string UnsupportedSchemaVersion = "unsupported_schema_version";
    public const string MalformedEventName = "malformed_event_name";
    public const string ForbiddenField = "forbidden_field";
    public const string PayloadTooLarge = "payload_too_large";
    public const string TimestampOutOfRange = "timestamp_out_of_range";
}

public interface IEventValidator
{
    EventValidationResult Validate(JsonNode? envelope);
    EventValidationResult Validate(CanonicalEvent evt);
}

/// <summary>
/// Gate for Pipeline A (spec 6). Unknown metadata keys are allowed; missing required fields,
/// unsupported major versions and credential-like fields are rejected to the invalid stream.
/// </summary>
public sealed class EventValidator : IEventValidator
{
    private static readonly JsonSchema Schema = LoadSchema();
    private static readonly EvaluationOptions EvalOptions = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = false
    };

    private readonly IngestionOptions _options;

    public EventValidator(IOptions<IngestionOptions> options) => _options = options.Value;

    public EventValidationResult Validate(JsonNode? envelope)
    {
        if (envelope is not JsonObject obj)
            return EventValidationResult.Invalid(RejectReasons.SchemaInvalid, "event must be a JSON object");

        var schemaVersion = obj["schema_version"]?.GetValue<string>();
        if (!EventSchema.IsSupported(schemaVersion))
            return EventValidationResult.Invalid(RejectReasons.UnsupportedSchemaVersion,
                $"schema_version '{schemaVersion}' is not supported (expected major {EventSchema.SupportedMajor})");

        var evaluation = Schema.Evaluate(obj, EvalOptions);
        if (!evaluation.IsValid)
        {
            var errors = Flatten(evaluation).ToList();
            return new EventValidationResult(false, errors, RejectReasons.SchemaInvalid);
        }

        var eventName = obj["event_name"]?.GetValue<string>();
        if (!EventNames.IsWellFormed(eventName))
            return EventValidationResult.Invalid(RejectReasons.MalformedEventName,
                $"event_name '{eventName}' does not match domain.entity.state and must not carry dynamic values");

        var forbidden = FindForbiddenFields(obj).Take(5).ToList();
        if (forbidden.Count > 0)
            return new EventValidationResult(false,
                forbidden.Select(f => $"forbidden field '{f}' must never be sent in an event payload").ToList(),
                RejectReasons.ForbiddenField);

        var nps = (obj["metadata"] as JsonObject)?["nps_score"];
        if ((eventName == EventNames.FeedbackNpsSubmitted || nps is not null)
            && !NpsScoreValidation.IsValid(nps))
            return EventValidationResult.Invalid(RejectReasons.SchemaInvalid, NpsScoreValidation.Error);

        foreach (var (key, maximum) in new[] { ("csat_score", 5), ("ces_score", 7) })
        {
            var score = (obj["metadata"] as JsonObject)?[key];
            if (score is not null && !ExperienceScoreValidation.IsValid(score, maximum))
                return EventValidationResult.Invalid(RejectReasons.SchemaInvalid, $"{key} must be an integer between 1 and {maximum}");
        }

        var serialized = obj.ToJsonString();
        if (serialized.Length > _options.MaxEventBytes)
            return EventValidationResult.Invalid(RejectReasons.PayloadTooLarge,
                $"event exceeds {_options.MaxEventBytes} bytes");

        return EventValidationResult.Valid;
    }

    public EventValidationResult Validate(CanonicalEvent evt)
    {
        var node = JsonSerializer.SerializeToNode(evt, Cx.Contracts.CxJson.Options);
        return Validate(node);
    }

    /// <summary>
    /// Credentials and secrets must never enter the pipeline (technical design 17).
    /// Matching is by key name anywhere in the document.
    /// </summary>
    private IEnumerable<string> FindForbiddenFields(JsonNode? node, string path = "$")
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (_options.ForbiddenFieldNames.Any(f => key.Equals(f, StringComparison.OrdinalIgnoreCase)))
                        yield return $"{path}.{key}";

                    foreach (var found in FindForbiddenFields(value, $"{path}.{key}"))
                        yield return found;
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    foreach (var found in FindForbiddenFields(arr[i], $"{path}[{i}]"))
                        yield return found;
                }
                break;
        }
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

    private static JsonSchema LoadSchema()
    {
        var assembly = typeof(CanonicalEvent).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("canonical-event.v1.schema.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}
