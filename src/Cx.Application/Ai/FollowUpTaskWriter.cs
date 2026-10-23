using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Application.Options;
using Cx.Application.Privacy;
using Cx.Contracts;
using Cx.Contracts.Ai;
using Cx.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Application.Ai;

/// <summary>A task draft for the technical team, ready to paste into their tracker.</summary>
public sealed record FollowUpTask(
    string Title,
    string Priority,
    string Text,
    string GeneratedBy,
    string? Model);

public interface IFollowUpTaskWriter
{
    Task<FollowUpTask> WriteAsync(string tenantId, Feedback feedback, CancellationToken ct = default);
}

/// <summary>
/// Drafts a technical-team task from a feedback item, its AI analysis and the customer's other
/// recent problems. Writing is the text model's job (Ai:Model); priority is not re-judged here
/// but taken from the severity the decision model already decided. Customer text is redacted
/// before it reaches the model, and if the model is unavailable or answers badly, a template
/// draft from the stored analysis is returned instead, so the operator always gets a text.
/// </summary>
public sealed class FollowUpTaskWriter : IFollowUpTaskWriter
{
    public const string SchemaName = "cx_follow_up_task";

    private const string Schema = """
        {
          "type": "object",
          "required": ["title", "problem", "expected_behavior", "suggested_steps", "acceptance_criteria"],
          "properties": {
            "title": { "type": "string" },
            "problem": { "type": "string" },
            "evidence": { "type": "array", "items": { "type": "string" } },
            "impact": { "type": "string" },
            "expected_behavior": { "type": "string" },
            "suggested_steps": { "type": "array", "items": { "type": "string" } },
            "acceptance_criteria": { "type": "array", "items": { "type": "string" } }
          }
        }
        """;

    private static readonly Dictionary<string, string> PriorityBySeverity = new(StringComparer.OrdinalIgnoreCase)
    {
        [Severities.Critical] = "Urgent",
        [Severities.High] = "High",
        [Severities.Medium] = "Medium",
        [Severities.Low] = "Low"
    };

    private readonly IAiCompletionClient _client;
    private readonly IAiAnalysisRepository _analyses;
    private readonly IFeedbackRepository _feedback;
    private readonly ISupportCaseRepository _supportCases;
    private readonly ICustomerRepository _customers;
    private readonly ITaxonomyRepository _taxonomies;
    private readonly IRedactionService _redaction;
    private readonly AiOptions _ai;
    private readonly ILogger<FollowUpTaskWriter> _logger;

    public FollowUpTaskWriter(IAiCompletionClient client, IAiAnalysisRepository analyses,
        IFeedbackRepository feedback, ISupportCaseRepository supportCases, ICustomerRepository customers,
        ITaxonomyRepository taxonomies, IRedactionService redaction, IOptions<AiOptions> ai,
        ILogger<FollowUpTaskWriter> logger)
    {
        _client = client;
        _analyses = analyses;
        _feedback = feedback;
        _supportCases = supportCases;
        _customers = customers;
        _taxonomies = taxonomies;
        _redaction = redaction;
        _ai = ai.Value;
        _logger = logger;
    }

    public async Task<FollowUpTask> WriteAsync(string tenantId, Feedback feedback, CancellationToken ct = default)
    {
        var (entityType, entityId) = feedback.CallId is not null
            ? (AnalysisEntityTypes.VoiceCall, feedback.CallId)
            : (AnalysisEntityTypes.CxEvent, feedback.EventId);
        var stored = entityId is null ? null : await _analyses.GetLatestForEntityAsync(tenantId, entityType, entityId, ct);
        var analysis = ParseAnalysis(stored?.Result);

        var topics = await TopicCatalog.LoadAsync(_taxonomies, tenantId, _ai.TaxonomyName, ct);
        var topic = topics.Find(analysis?.Subcategory) ?? topics.Find(analysis?.Category);
        var topicTitle = topic is null ? null : topics.TitlePath(topic);

        var customer = feedback.CustomerId is null ? null : await _customers.GetAsync(tenantId, feedback.CustomerId, ct);
        IReadOnlyList<Feedback> history = feedback.CustomerId is null
            ? Array.Empty<Feedback>()
            : (await _feedback.GetRecentForCustomerAsync(tenantId, feedback.CustomerId, 6, ct))
                .Where(f => f.Id != feedback.Id && !string.IsNullOrWhiteSpace(f.Comment)).Take(5).ToList();
        var openCases = feedback.CustomerId is null
            ? Array.Empty<SupportCase>()
            : await _supportCases.GetOpenForCustomerAsync(tenantId, feedback.CustomerId, 3, ct);

        var priority = PriorityBySeverity.GetValueOrDefault(analysis?.Severity ?? string.Empty, "Medium");
        var facts = new TaskFacts(feedback, analysis, topicTitle, customer?.DisplayName ?? feedback.CustomerId,
            customer?.Segment, priority, history.Count + openCases.Count);

        var evidence = new JsonObject
        {
            ["feedback"] = new JsonObject
            {
                ["text"] = Redact(feedback.Comment),
                ["rating"] = feedback.Rating ?? feedback.CsatScore ?? feedback.NpsScore,
                ["channel"] = feedback.Channel,
                ["kind"] = feedback.CallId is null ? "text" : "voice message",
                ["submitted_at"] = feedback.SubmittedAt.ToString("yyyy-MM-dd")
            },
            ["analysis"] = analysis is null ? null : new JsonObject
            {
                ["topic"] = topicTitle ?? analysis.Category,
                ["severity"] = analysis.Severity,
                ["summary"] = analysis.Summary,
                ["action_items"] = new JsonArray(analysis.ActionItems.Select(a => (JsonNode?)a).ToArray()),
                ["mentioned_products"] = new JsonArray(analysis.MentionedProducts.Select(a => (JsonNode?)a).ToArray())
            },
            ["customer_segment"] = customer?.Segment,
            ["earlier_feedback_from_same_customer"] = new JsonArray(history
                .Select(f => (JsonNode?)new JsonObject
                {
                    ["date"] = f.SubmittedAt.ToString("yyyy-MM-dd"),
                    ["text"] = Clip(Redact(f.Comment), 500)
                }).ToArray()),
            ["open_support_cases"] = new JsonArray(openCases
                .Select(c => (JsonNode?)new JsonObject
                {
                    ["subject"] = Redact(c.Subject),
                    ["status"] = c.Status,
                    ["opened_at"] = c.OpenedAt.ToString("yyyy-MM-dd")
                }).ToArray())
        };

        try
        {
            var completion = await _client.CompleteJsonAsync(new AiCompletionRequest
            {
                SystemPrompt = SystemPrompt,
                UserPrompt = $"""
                    CUSTOMER PROBLEM (JSON):
                    {evidence.ToJsonString(CxJson.Indented)}

                    Write the task now. Return only the JSON object.
                    """,
                JsonSchema = Schema,
                JsonSchemaName = SchemaName,
                Temperature = 0.2m,
                MaxTokens = 2500
            }, ct);

            var draft = ParseDraft(completion.Content);
            if (draft is not null)
                return new FollowUpTask(draft.Title, priority, Render(draft, facts), "ai", completion.Model);

            // The excerpt is model output written from redacted text, the same text the operator sees.
            _logger.LogWarning(
                "Follow-up task from {Model} was not usable ({Length} chars: {Excerpt}); returning the template draft",
                completion.Model, completion.Content.Length, Clip(completion.Content, 300));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Follow-up task generation failed for feedback {FeedbackId}; returning the template draft",
                feedback.Id);
        }

        var template = TemplateDraft(facts);
        return new FollowUpTask(template.Title, priority, Render(template, facts), "template", null);
    }

    private const string SystemPrompt = """
        You write engineering tasks for the technical team of a English-language business,
        from customer feedback that the customer-experience team is following up.

        Write everything in English (en-US), for engineers: concrete, neutral, no blame, no emotion.
        - title: at most 90 characters, names the technical problem, not the customer.
        - problem: 2-4 sentences: what goes wrong for the customer and where in the product.
        - evidence: short bullet facts taken from the input (what the customer said or did, ratings,
          repeated reports, open cases). Never invent error codes, numbers, versions or dates.
        - impact: who is affected and how badly, from the input only.
        - expected_behavior: what should happen instead.
        - suggested_steps: investigation/fix steps for engineers (logs to check, flows to reproduce).
        - acceptance_criteria: verifiable conditions for closing the task.
        Text marked [REDACTED:...] is removed personal data: never guess it and never ask for it.
        Respond with a single JSON object and nothing else, with every field, starting with "title":
        {"title": "...", "problem": "...", "evidence": ["..."], "impact": "...", "expected_behavior": "...",
         "suggested_steps": ["..."], "acceptance_criteria": ["..."]}
        """;

    private sealed record TaskFacts(
        Feedback Feedback,
        AiAnalysisResult? Analysis,
        string? TopicTitle,
        string? CustomerName,
        string? Segment,
        string Priority,
        int RelatedCount);

    private sealed record Draft(
        string Title,
        string Problem,
        IReadOnlyList<string> Evidence,
        string? Impact,
        string ExpectedBehavior,
        IReadOnlyList<string> SuggestedSteps,
        IReadOnlyList<string> AcceptanceCriteria);

    private static Draft? ParseDraft(string raw)
    {
        var json = AiResultValidator.ExtractJson(raw);
        if (json is null) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return null;
            string? Text(string key) => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) &&
                                        !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
            List<string> List(string key) => obj[key] is JsonArray a
                ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList()
                : new List<string>();

            var problem = Text("problem");
            if (problem is null) return null;
            // Models sometimes drop the title even when the schema requires it; the problem's
            // first sentence is a better title than throwing the whole draft away.
            var title = Text("title") ?? FirstSentence(problem);

            return new Draft(Clip(title, 120)!, problem, List("evidence"), Text("impact"),
                Text("expected_behavior") ?? "—", List("suggested_steps"), List("acceptance_criteria"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Built from the stored analysis alone, for when the text model cannot help.</summary>
    private Draft TemplateDraft(TaskFacts facts)
    {
        var a = facts.Analysis;
        var subject = facts.TopicTitle ?? (a is null ? "Customer feedback" : a.Category);
        var evidence = new List<string>();
        if (!string.IsNullOrWhiteSpace(facts.Feedback.Comment))
            evidence.Add($"Customer text: «{Clip(Redact(facts.Feedback.Comment), 400)}»");
        if ((facts.Feedback.Rating ?? facts.Feedback.CsatScore ?? facts.Feedback.NpsScore) is { } rating)
            evidence.Add($"Customer points: {rating}");
        if (facts.RelatedCount > 0)
            evidence.Add($"{facts.RelatedCount} Another feedback or support request has been filed from this customer.");

        return new Draft(
            $"Investigating the problem «{subject}» Reported by the customer",
            a?.Summary ?? "The customer has reported a problem that requires technical investigation.",
            evidence,
            null,
            "The reported problem is resolved and the customer can perform the process without errors.",
            a?.ActionItems.Count > 0
                ? a.ActionItems
                : (IReadOnlyList<string>)new[] { "Reproducing the problem based on the customer’s explanation and checking the logs" },
            new[] { "The reproduction problem and its cause have been identified.", "After the fix, the client scenario should run without errors." });
    }

    private static string Render(Draft d, TaskFacts f)
    {
        var text = new StringBuilder();
        text.AppendLine($"Title: {d.Title}");
        text.AppendLine($"Priority: {f.Priority}");
        if (f.TopicTitle is not null) text.AppendLine($"Topic: {f.TopicTitle}");
        text.AppendLine();
        text.AppendLine("Description of the problem:");
        text.AppendLine(d.Problem);
        if (d.Evidence.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Evidence:");
            foreach (var e in d.Evidence) text.AppendLine($"- {e}");
        }
        if (!string.IsNullOrWhiteSpace(d.Impact))
        {
            text.AppendLine();
            text.AppendLine("The effect on the client:");
            text.AppendLine(d.Impact);
        }
        text.AppendLine();
        text.AppendLine("Expected behavior:");
        text.AppendLine(d.ExpectedBehavior);
        if (d.SuggestedSteps.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Suggested actions:");
            for (var i = 0; i < d.SuggestedSteps.Count; i++) text.AppendLine($"{i + 1}. {d.SuggestedSteps[i]}");
        }
        if (d.AcceptanceCriteria.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Acceptance criteria:");
            foreach (var c in d.AcceptanceCriteria) text.AppendLine($"- [ ] {c}");
        }
        text.AppendLine();
        text.AppendLine("Reference information:");
        text.AppendLine($"- Feedback ID: {f.Feedback.Id}");
        if (f.CustomerName is not null)
            text.AppendLine($"- Customer: {f.CustomerName}{(f.Segment is null ? "" : $" ({f.Segment})")}");
        text.AppendLine($"- Registration date: {f.Feedback.SubmittedAt:yyyy-MM-dd}");
        if (f.Feedback.CallId is not null) text.AppendLine($"- Call: {f.Feedback.CallId}");
        return text.ToString().TrimEnd();
    }

    private static AiAnalysisResult? ParseAnalysis(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<AiAnalysisResult>(json, CxJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly char[] SentenceEnds = { '.', ';', '!', '?', '\n' };

    private static string FirstSentence(string text)
    {
        var end = text.IndexOfAny(SentenceEnds);
        return (end > 10 ? text[..end] : text).Trim();
    }

    private string? Redact(string? text) => text is null ? null : _redaction.Redact(text).RedactedText;

    private static string? Clip(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max] + "…";
}
