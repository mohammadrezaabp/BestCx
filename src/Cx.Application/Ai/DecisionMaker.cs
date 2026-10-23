using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Application.Options;
using Cx.Contracts;
using Cx.Contracts.Ai;
using Cx.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Application.Ai;

/// <summary>
/// Every judgement about one interaction, made by the decision model (Ai:DecisionModel).
/// <see cref="Topic"/> is null when nothing in the topic tree fits; any other field is
/// null when the model returned a value outside the contract.
/// </summary>
public sealed record AnalysisDecision
{
    public Topic? Topic { get; init; }
    public string? Sentiment { get; init; }
    public string? Intent { get; init; }
    public string? Severity { get; init; }
    public string? ResolutionStatus { get; init; }
    public string? JourneyStage { get; init; }
    public bool? FollowUpRequired { get; init; }
    public decimal Confidence { get; init; }
    public string? Reason { get; init; }
    public string? Model { get; init; }
}

public interface IDecisionMaker
{
    Task<AnalysisDecision> DecideAsync(AnalysisContext context, TopicCatalog topics, CancellationToken ct = default);
}

/// <summary>
/// The AI's decisions are made in their own call, by the decision model, before the main
/// analysis. The text model then only writes (summary, action items) for decisions that
/// are already fixed, and <see cref="Apply"/> makes those decisions authoritative on the
/// stored result whatever the text model echoed back.
/// </summary>
public sealed class DecisionMaker : IDecisionMaker
{
    public const string SchemaName = "cx_analysis_decision";
    public const string NoTopic = "none";

    public const string Schema = """
        {
          "type": "object",
          "required": ["topic", "sentiment", "intent", "severity", "resolution_status", "confidence"],
          "additionalProperties": false,
          "properties": {
            "topic": { "type": "string" },
            "sentiment": { "type": "string", "enum": ["positive", "neutral", "negative", "mixed", "unknown"] },
            "intent": { "type": "string" },
            "severity": { "type": "string", "enum": ["low", "medium", "high", "critical"] },
            "resolution_status": { "type": "string", "enum": ["resolved", "partially_resolved", "unresolved", "unknown"] },
            "journey_stage": { "type": ["string", "null"] },
            "follow_up_required": { "type": ["boolean", "null"] },
            "confidence": { "type": "number" },
            "reason": { "type": "string" }
          }
        }
        """;

    private readonly IAiDecisionClient _decisions;
    private readonly IAiCompletionClient _client;
    private readonly AiOptions _ai;
    private readonly ILogger<DecisionMaker> _logger;

    public DecisionMaker(IAiDecisionClient decisions, IAiCompletionClient client, IOptions<AiOptions> ai,
        ILogger<DecisionMaker> logger)
    {
        _decisions = decisions;
        _client = client;
        _ai = ai.Value;
        _logger = logger;
    }

    /// <summary>
    /// The decisions model (Ai:DecisionModel, on the decisions endpoint) decides first. If it
    /// fails, the fallback chat model (Ai:DecisionFallbackModel) makes the same decisions from
    /// an equivalent prompt; the decision's Model says which one it was.
    /// </summary>
    public async Task<AnalysisDecision> DecideAsync(AnalysisContext context, TopicCatalog topics,
        CancellationToken ct = default)
    {
        var isVoice = context.AnalysisType == AnalysisTypes.VoiceAnalysis;
        AnalysisDecision decision;

        if (_decisions.Enabled && !string.IsNullOrWhiteSpace(_ai.DecisionModel))
        {
            try
            {
                var response = await _decisions.DecideAsync(BuildDecisionRequest(_ai.DecisionModel, context, topics, isVoice), ct);
                decision = FromAnswers(response, topics, isVoice);
                return Logged(decision, context, topics);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(_ai.DecisionFallbackModel))
            {
                _logger.LogWarning(ex, "Decision model {Model} failed for {EntityType}:{EntityId}; deciding with {Fallback} (fallback)",
                    _ai.DecisionModel, context.EntityType, context.EntityId, _ai.DecisionFallbackModel);
            }
        }

        var completion = await _client.CompleteJsonAsync(new AiCompletionRequest
        {
            SystemPrompt = BuildSystemPrompt(topics, isVoice),
            UserPrompt = BuildUserPrompt(context),
            JsonSchema = Schema,
            JsonSchemaName = SchemaName,
            Temperature = 0m,
            MaxTokens = _ai.DecisionMaxTokens,
            ModelOverride = string.IsNullOrWhiteSpace(_ai.DecisionFallbackModel) ? null : _ai.DecisionFallbackModel
        }, ct);

        decision = Parse(completion.Content, topics) with { Model = completion.Model };
        return Logged(decision, context, topics);
    }

    private AnalysisDecision Logged(AnalysisDecision decision, AnalysisContext context, TopicCatalog topics)
    {
        if (!topics.IsEmpty && decision.Topic is null)
        {
            _logger.LogInformation("Decision model {Model} found no fitting topic for {EntityType}:{EntityId} ({Reason})",
                decision.Model, context.EntityType, context.EntityId, decision.Reason);
        }
        return decision;
    }

    /// <summary>
    /// The decisions-endpoint form of the judgements. Each option's criteria text is what the
    /// model weighs, so topics carry their full path, description and example phrases.
    /// </summary>
    public static DecisionRequest BuildDecisionRequest(string model, AnalysisContext context, TopicCatalog topics,
        bool isVoice)
    {
        var questions = new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal);

        if (!topics.IsEmpty)
        {
            var options = new JsonObject();
            foreach (var topic in topics.Topics)
            {
                var text = new System.Text.StringBuilder(topics.TitlePath(topic));
                if (!string.IsNullOrWhiteSpace(topic.Description)) text.Append(" — ").Append(topic.Description.Trim());
                var examples = topic.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
                if (examples.Count > 0) text.Append(" (Example: ").Append(string.Join("; ", examples)).Append(')');
                options[topic.Code] = text.ToString();
            }
            options[NoTopic] = "None of the topics above matches what the customer is talking about.";

            questions["topic"] = new DecisionQuestion
            {
                Type = DecisionQuestion.Choice,
                Instructions = "Which topic is the customer mainly talking about in current_event (or the call transcript)? " +
                               "Choose the most specific topic whose description fits; choose a broader parent topic " +
                               "only when just the general area is clear. Judge by meaning, not shared words. " +
                               $"Choose '{NoTopic}' when nothing fits.",
                Criteria = options
            };
        }

        questions["sentiment"] = Choice(
            "How does the customer feel, judged from what they say and what happened to them?",
            (Sentiments.Positive, "satisfied, thankful or pleased"),
            (Sentiments.Neutral, "factual, neither pleased nor unhappy"),
            (Sentiments.Negative, "unhappy, frustrated, complaining or let down"),
            (Sentiments.Mixed, "clearly both positive and negative"),
            (Sentiments.Unknown, "there is not enough customer text to tell"));

        questions["intent"] = Choice("Why did the customer reach out?",
            ("technical_support", "something does not work and they need help"),
            ("billing_inquiry", "a question about charges, invoices, payments or refunds"),
            ("complaint", "they are complaining about the service, a person or an outcome"),
            ("feature_request", "they ask for something the product does not do yet"),
            ("cancellation", "they want to cancel, leave or close their account"),
            ("information_request", "they want information or instructions"),
            ("praise", "they are thanking or praising"),
            ("unknown", "the reason cannot be determined"));

        questions["severity"] = Choice(
            "How severe is the customer's problem? A problem repeated in the customer history raises severity.",
            (Severities.Critical, "service unavailable, data loss, a security issue or money lost"),
            (Severities.High, "a core flow is blocked with no workaround"),
            (Severities.Medium, "a degraded experience, but a workaround exists"),
            (Severities.Low, "cosmetic, informational or a general question"));

        questions["resolution_status"] = Choice(
            isVoice ? "What is the state of the customer's issue at the END of the call?"
                    : "What is the state of the customer's issue?",
            (ResolutionStatuses.Resolved, "the issue was solved"),
            (ResolutionStatuses.PartiallyResolved, "partly solved, something is still open"),
            (ResolutionStatuses.Unresolved, "the issue is still open"),
            (ResolutionStatuses.Unknown, "cannot be determined"));

        questions["journey_stage"] = Choice("Where is the customer in their journey with the business?",
            ("awareness", "learning about the product before becoming a customer"),
            ("onboarding", "signing up, verification or first setup"),
            ("activation", "first real use of the product"),
            ("usage", "regular day-to-day use"),
            ("support", "mainly seeking help with an issue"),
            ("renewal", "renewing, extending or upgrading"),
            ("churn_risk", "considering leaving or cancelling"),
            (UnclearStage, "cannot be determined"));

        if (isVoice)
        {
            questions["follow_up_required"] = new DecisionQuestion
            {
                Type = DecisionQuestion.Noul,
                Instructions = "Does this call need a follow-up: it ended without a confirmed resolution, the agent " +
                               "promised something that still has to happen, or the customer asked to be contacted?",
                Criteria = new JsonObject { ["true"] = "a follow-up is needed", ["false"] = "no follow-up is needed" }
            };
        }

        return new DecisionRequest
        {
            Model = model,
            State = JsonSerializer.SerializeToNode(context, CxJson.Options)!,
            Questions = questions
        };
    }

    private const string UnclearStage = "unclear";

    /// <summary>
    /// Maps typed answers onto a decision. Confidence is the least certain of the judgements
    /// the review policy acts on (topic, sentiment, severity).
    /// </summary>
    public static AnalysisDecision FromAnswers(DecisionResponse response, TopicCatalog topics, bool isVoice)
    {
        var answers = response.Answers;
        string? Chosen(string name) => answers.TryGetValue(name, out var a) ? a.Choice : null;

        var code = Chosen("topic");
        var topic = topics.Find(code);
        var stage = Chosen("journey_stage");

        var confidences = new[] { "topic", "sentiment", "severity" }
            .Where(name => name != "topic" || !topics.IsEmpty)
            .Select(name => answers.TryGetValue(name, out var a) ? a.Confidence ?? 0m : 0m)
            .ToList();

        bool? followUp = isVoice && answers.TryGetValue("follow_up_required", out var f) && f.Noul is { } p
            ? p >= 0.5
            : null;

        return new AnalysisDecision
        {
            Topic = topic,
            Sentiment = OneOf(Chosen("sentiment"), Sentiments.All),
            Intent = OneOf(Chosen("intent"), Intents.All),
            Severity = OneOf(Chosen("severity"), Severities.All),
            ResolutionStatus = OneOf(Chosen("resolution_status"), ResolutionStatuses.All),
            JourneyStage = stage == UnclearStage ? null : OneOf(stage, JourneyStages.All),
            FollowUpRequired = followUp,
            Confidence = confidences.Count == 0 ? 0m : confidences.Min(),
            Reason = topic is null && !topics.IsEmpty
                ? code is null || code == NoTopic ? "no topic fits" : $"unknown topic code '{code}'"
                : null,
            Model = response.Model
        };
    }

    private static DecisionQuestion Choice(string instructions, params (string Key, string Meaning)[] options)
    {
        var criteria = new JsonObject();
        foreach (var (key, meaning) in options) criteria[key] = meaning;
        return new DecisionQuestion { Type = DecisionQuestion.Choice, Instructions = instructions, Criteria = criteria };
    }

    public static string BuildSystemPrompt(TopicCatalog topics, bool isVoice) => $$"""
        You are the decision engine of a customer-experience platform for an English-language business. Read one customer interaction and make the judgements below.
        `current_event.text` holds the customer's own words and `current_event.rating` their score
        (1-5, or 0-10 for NPS). Decide primarily from them; customer history, open cases and earlier
        feedback are background that can raise severity for a repeated problem.

        1. topic - ONE code from this tree, maintained by the customer-experience team. Each line reads
           `[code] title — what belongs there (examples: ...)`; indentation shows nesting.
        {{(topics.IsEmpty ? "   (no topics defined: answer \"" + NoTopic + "\")" : topics.Render())}}
           - Choose the most specific topic whose description matches what the customer is actually
             talking about; choose a parent when only the general area is clear.
           - Judge by meaning, not shared words: an example phrase in the text is a hint, not a decision.
           - With several problems, choose the one the customer mainly raises.
           - If nothing fits, answer "{{NoTopic}}". Never force a match and never invent a code.
        2. sentiment - positive | neutral | negative | mixed | unknown. From what the customer says and
           what happened to them, not from guesses about tone.
        3. intent - one of: {{string.Join(", ", Intents.All)}}.
        4. severity - critical = service unavailable / data loss / security or money lost;
           high = a core flow is blocked with no workaround; medium = degraded with a workaround;
           low = cosmetic or informational.
        5. resolution_status - resolved | partially_resolved | unresolved | unknown{{(isVoice ? ", as at the END of the call" : string.Empty)}}.
        6. journey_stage - one of: {{string.Join(", ", JourneyStages.All)}}, or null when unclear.
        {{(isVoice
            ? "7. follow_up_required - true when the call ended without a confirmed resolution, the agent promised\n   something that still has to happen, or the customer asks to be contacted."
            : "7. follow_up_required - null.")}}

        Base every judgement on evidence in the context. Text marked [REDACTED:...] is removed PII.
        `confidence` is your calibrated certainty in [0,1] across these judgements; report low
        confidence honestly - low-confidence results go to a human reviewer, which is right when the
        context is thin or contradictory. `reason` is one short English sentence.

        Respond with a single JSON object and nothing else:
        {"topic": "<code or {{NoTopic}}>", "sentiment": "...", "intent": "...", "severity": "...",
         "resolution_status": "...", "journey_stage": null, "follow_up_required": null,
         "confidence": 0.0, "reason": "string"}
        """;

    public static string BuildUserPrompt(AnalysisContext context) => $"""
        INTERACTION CONTEXT (JSON):
        {JsonSerializer.Serialize(context, CxJson.Indented)}

        Make the decisions now. Return only the JSON object.
        """;

    /// <summary>Values outside the contract are dropped, never guessed; an unknown topic is "no topic".</summary>
    public static AnalysisDecision Parse(string? raw, TopicCatalog topics)
    {
        var json = raw is null ? null : AiResultValidator.ExtractJson(raw);
        JsonObject? obj = null;
        try
        {
            if (json is not null) obj = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            obj = null;
        }

        if (obj is null) return new AnalysisDecision { Reason = "decision model returned no JSON object" };

        var code = Text(obj, "topic");
        var reason = Text(obj, "reason");
        var topic = topics.Find(code);
        if (topic is null && code is not null && !code.Equals(NoTopic, StringComparison.OrdinalIgnoreCase))
            reason = $"unknown topic code '{code}'";

        return new AnalysisDecision
        {
            Topic = topic,
            Sentiment = OneOf(Text(obj, "sentiment")?.ToLowerInvariant(), Sentiments.All),
            Intent = OneOf(Text(obj, "intent")?.ToLowerInvariant(), Intents.All),
            Severity = OneOf(Text(obj, "severity")?.ToLowerInvariant(), Severities.All),
            ResolutionStatus = OneOf(Text(obj, "resolution_status")?.ToLowerInvariant(), ResolutionStatuses.All),
            JourneyStage = OneOf(Text(obj, "journey_stage")?.ToLowerInvariant(), JourneyStages.All),
            FollowUpRequired = obj["follow_up_required"] is JsonValue f && f.TryGetValue<bool>(out var b) ? b : null,
            Confidence = ReadConfidence(obj["confidence"]),
            Reason = reason
        };
    }

    /// <summary>
    /// Makes the decisions authoritative on the result. The analysis is only as certain as
    /// its least certain part, so a doubtful decision sends the result to human review.
    /// </summary>
    public static void Apply(AiAnalysisResult result, AnalysisDecision decision, TopicCatalog topics)
    {
        if (!topics.IsEmpty)
        {
            if (decision.Topic is null)
            {
                result.Category = TopicCodes.Unclassified;
                result.Subcategory = null;
            }
            else
            {
                (result.Category, result.Subcategory) = topics.Classification(decision.Topic);
            }
        }

        if (decision.Sentiment is not null) result.Sentiment = decision.Sentiment;
        if (decision.Intent is not null) result.Intent = decision.Intent;
        if (decision.Severity is not null) result.Severity = decision.Severity;
        if (decision.ResolutionStatus is not null) result.ResolutionStatus = decision.ResolutionStatus;
        if (decision.JourneyStage is not null) result.JourneyStage = decision.JourneyStage;
        if (decision.FollowUpRequired is not null && result.FollowUpRequired is not null)
            result.FollowUpRequired = decision.FollowUpRequired;

        result.Confidence = Math.Min(result.Confidence, decision.Confidence);
    }

    /// <summary>What the text model is told was decided, so its summary agrees with it.</summary>
    public static ContextDecision ToContext(AnalysisDecision decision, TopicCatalog topics)
    {
        var context = new ContextDecision
        {
            Sentiment = decision.Sentiment,
            Intent = decision.Intent,
            Severity = decision.Severity,
            ResolutionStatus = decision.ResolutionStatus,
            JourneyStage = decision.JourneyStage,
            FollowUpRequired = decision.FollowUpRequired
        };

        if (topics.IsEmpty) return context;

        if (decision.Topic is null)
        {
            context.Category = TopicCodes.Unclassified;
            context.TopicTitle = "Unknown";
        }
        else
        {
            (context.Category, context.Subcategory) = topics.Classification(decision.Topic);
            context.TopicTitle = topics.TitlePath(decision.Topic);
        }

        return context;
    }

    private static string? Text(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static string? OneOf(string? value, IEnumerable<string> allowed) =>
        value is not null && allowed.Contains(value) ? value : null;

    private static decimal ReadConfidence(JsonNode? node)
    {
        double value = 0;
        if (node is JsonValue v && !v.TryGetValue(out value) && v.TryGetValue<string>(out var text))
            double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        if (value > 1 && value <= 100) value /= 100;
        return (decimal)Math.Round(Math.Clamp(value, 0, 1), 4);
    }
}
