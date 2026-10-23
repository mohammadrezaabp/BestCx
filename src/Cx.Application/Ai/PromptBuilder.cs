using Cx.Contracts;
using Cx.Contracts.Ai;

namespace Cx.Application.Ai;

public interface IPromptBuilder
{
    string PromptVersion { get; }
    string BuildSystemPrompt(AnalysisContext context);
    string BuildUserPrompt(AnalysisContext context);
    string BuildRepairPrompt(string rawOutput, IReadOnlyList<string> errors);
}

/// <summary>
/// Prompt text is versioned and stored with every result (spec 22), so a classification
/// can always be traced back to the exact instructions that produced it.
/// </summary>
public sealed class PromptBuilder : IPromptBuilder
{
    private readonly string _promptVersion;

    public PromptBuilder(string promptVersion) => _promptVersion = promptVersion;

    public string PromptVersion => _promptVersion;

    public string BuildSystemPrompt(AnalysisContext context)
    {
        var isVoice = context.AnalysisType == AnalysisTypes.VoiceAnalysis;

        return $$"""
        You are the analysis engine of the BestCx Customer Experience platform.
        You classify customer feedback and interactions for an English-language business.

        Your task: read the interaction context and return ONE JSON object describing it.
        For feedback, `current_event.text` holds the customer's own words and `current_event.rating`
        their score (1-5, or 0-10 for NPS). Base the classification primarily on them; customer history,
        open cases and earlier feedback are background that can raise severity for repeated problems.

        Hard rules:
        - Respond with a single JSON object and nothing else. No prose, no markdown fence.
        - The judgements were already made by the decision model and are given in `decision`
          (topic, sentiment, intent, severity, resolution, journey stage, follow-up).
          Copy every non-null `decision` field into the matching output field unchanged, and write
          the summary and action items so they agree with them. Do not re-decide.
          For a field `decision` leaves null: category "{{TopicCodes.Unclassified}}";
          intent one of {{string.Join(", ", Intents.All)}}; journey_stage one of
          {{string.Join(", ", JourneyStages.All)}} or null.
        - Base every judgement on evidence in the context. Never invent facts, names, amounts or promises.
        - `summary` is written in English (en-US), 1-3 sentences, factual, no speculation about emotions.
        - `action_items` are concrete next steps for the CX or support team, in English.
        - Sentiment is derived from what the customer says and what happened to them,
          not from guesses about tone of voice.
        - `confidence` is your own calibrated certainty in [0,1]. Report low confidence honestly:
          low-confidence results are routed to a human reviewer, which is the desired outcome
          when the context is thin or contradictory.
        - Text marked [REDACTED:...] is intentionally removed PII. Do not guess what it was.
        - severity: critical = service unavailable / data loss / security or money lost;
          high = a core flow is blocked with no workaround; medium = degraded with workaround;
          low = cosmetic or informational.
        {{(isVoice ? VoiceRules : string.Empty)}}
        Return JSON matching exactly this shape:
        {{ShapeHint(isVoice)}}
        """;
    }

    private const string VoiceRules = """

        This is a recorded phone call or a voice message left by the customer. Additionally:
        - `call_reason`: why the customer called, in English, one short phrase.
        - `follow_up_required`: true when the call ended without a confirmed resolution,
          the agent promised something that still has to happen, or the customer asks to be contacted.
        - `key_moments`: up to 8 moments with real start_ms/end_ms taken from the transcript
          segment timings supplied in the context. Never invent timestamps; if segment timings
          are missing, return an empty array.
        - `resolution_status` reflects the state at the END of the call.

        """;

    private static string ShapeHint(bool isVoice)
    {
        var baseShape = """
        {
          "sentiment": "positive|neutral|negative|mixed|unknown",
          "intent": "string",
          "category": "string",
          "subcategory": "string|null",
          "severity": "low|medium|high|critical",
          "journey_stage": "string|null",
          "resolution_status": "resolved|partially_resolved|unresolved|unknown",
          "summary": "string (English)",
          "action_items": ["string"],
          "mentioned_products": ["string"],
          "confidence": 0.0
        """;

        var voiceShape = """
        ,
          "call_reason": "string",
          "follow_up_required": true,
          "key_moments": [
            {"start_ms": 0, "end_ms": 0, "type": "problem_statement|complaint|resolution|commitment|escalation|other", "summary": "string"}
          ]
        """;

        return baseShape + (isVoice ? voiceShape : string.Empty) + "\n}";
    }

    public string BuildUserPrompt(AnalysisContext context)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(context, CxJson.Indented);
        return $"""
        INTERACTION CONTEXT (JSON):
        {payload}

        Classify this interaction now. Return only the JSON object.
        """;
    }

    public string BuildRepairPrompt(string rawOutput, IReadOnlyList<string> errors) =>
        $"""
        Your previous response failed schema validation.

        Errors:
        - {string.Join("\n- ", errors)}

        Your previous response:
        {rawOutput}

        Return a corrected single JSON object that fixes exactly these errors.
        Keep every value you already decided on unless the error says it is invalid.
        Output only JSON.
        """;
}
