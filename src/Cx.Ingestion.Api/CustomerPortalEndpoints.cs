using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Cx.Application.Abstractions;
using Cx.Application.Ai;
using Cx.Application.Ingestion;
using Cx.Application.Options;
using Cx.Contracts;
using Cx.Contracts.Events;
using Cx.Domain.Entities;
using Cx.Voice.Worker;
using Cx.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Cx.Ingestion.Api;

/// <summary>Customer-only write surface. Tenant and question wording are controlled by the server.</summary>
public static class CustomerPortalEndpoints
{
    public static IEndpointRouteBuilder MapCustomerPortalEndpoints(this IEndpointRouteBuilder app)
    {
        var portal = app.MapGroup("/v1/portal").WithTags("Customer portal")
            .RequireRateLimiting(CxRateLimiting.PortalPolicy);
        MapPortalAuth(portal);
        var customer = portal.MapGroup("").RequireAuthorization(CustomerPortalAccounts.Policy)
            .AddEndpointFilter(async (context, next) =>
            {
                var settings = context.HttpContext.RequestServices
                    .GetRequiredService<IOptions<CustomerPortalOptions>>().Value;
                if (!settings.Enabled) return Results.NotFound();
                if (context.HttpContext.User.FindFirst(CxClaimTypes.TenantId)?.Value != settings.TenantId)
                    return Results.Forbid();
                return await next(context);
            });

        customer.MapGet("/categories", async (ITaxonomyRepository repository, IOptions<AiOptions> ai,
            IOptions<CustomerPortalOptions> options, CancellationToken ct) =>
        {
            if (!options.Value.Enabled) return Results.NotFound();
            var catalog = await TopicCatalog.LoadAsync(repository, options.Value.TenantId, ai.Value.TaxonomyName, ct);
            return Results.Ok(new
            {
                revision = catalog.Revision,
                categories = catalog.Topics.Select(t => new
                {
                    id = t.Code, title = t.Title, path = catalog.TitlePath(t),
                    description = t.Description,
                    questions = PortalQuestionnaire.Questions(catalog.TitlePath(t))
                })
            });
        });

        customer.MapPost("/submissions", async (HttpContext ctx, [FromBody] PortalSubmission body,
            ITaxonomyRepository repository, IOptions<AiOptions> ai, IOptions<CustomerPortalOptions> options,
            IEventIngestionService ingestion, CancellationToken ct) =>
        {
            if (!options.Value.Enabled) return Results.NotFound();
            var catalog = await TopicCatalog.LoadAsync(repository, options.Value.TenantId, ai.Value.TaxonomyName, ct);
            var errors = PortalQuestionnaire.Validate(body, catalog);
            if (errors.Count > 0) return Results.BadRequest(new { error = string.Join(" ", errors) });
            if (body.Revision != catalog.Revision)
                return Results.Conflict(new { error = "Categories have changed; Refresh the page and reply again." });

            var evt = PortalQuestionnaire.ToEvent(body, catalog, options.Value.TenantId,
                ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var outcome = await ingestion.IngestAsync(JsonSerializer.SerializeToNode(evt, CxJson.Options),
                options.Value.TenantId, ct);
            return outcome.Accepted
                ? Results.Accepted(value: new { receipt = body.SubmissionId, status = "accepted" })
                : Results.BadRequest(new { error = outcome.Reason, errors = outcome.Errors });
        });

        customer.MapPost("/voice", SubmitVoiceAsync).DisableAntiforgery();
        customer.MapPortalOperations();
        return app;
    }

    private static void MapPortalAuth(RouteGroupBuilder portal)
    {
        portal.MapPost("/auth/login", async (HttpContext ctx, LoginRequest body,
            CustomerPortalAccounts accounts, IOptions<CustomerPortalOptions> options, CancellationToken ct) =>
        {
            if (!options.Value.Enabled) return Results.NotFound();
            var result = await accounts.ValidateAsync(options.Value.TenantId, body.Username, body.Password, ct);
            if (result.Locked) return Results.Json(new { error = "Entry is blocked for a few minutes; Try later." }, statusCode: 429);
            if (result.Account is null) return Results.Json(new { error = "Username or password is incorrect." }, statusCode: 401);
            await ctx.SignInAsync(CustomerPortalAccounts.Scheme, CustomerPortalAccounts.Principal(result.Account));
            return Results.Ok(PortalUser(result.Account));
        }).AllowAnonymous();
        portal.MapGet("/auth/me", async (HttpContext ctx, CustomerPortalAccounts accounts,
            IOptions<CustomerPortalOptions> options, CancellationToken ct) =>
        {
            if (!options.Value.Enabled) return Results.NotFound();
            if (ctx.User.FindFirst(CxClaimTypes.TenantId)?.Value != options.Value.TenantId) return Results.Forbid();
            var account = await accounts.GetAsync(options.Value.TenantId,
                ctx.User.FindFirst(CustomerPortalAccounts.UsernameClaim)!.Value, ct);
            return account is null ? Results.Unauthorized() : Results.Ok(PortalUser(account));
        }).RequireAuthorization(CustomerPortalAccounts.Policy);
        portal.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CustomerPortalAccounts.Scheme);
            return Results.NoContent();
        }).AllowAnonymous();
    }

    private static object PortalUser(CustomerPortalAccount account) => new
    {
        customer_id = account.CustomerId, username = account.Username,
        display_name = account.DisplayName ?? account.Username
    };

    /// <summary>
    /// A complaint or feedback told by voice instead of typed. The form answers arrive as JSON
    /// beside the audio; the recording then follows the console's voice-message pipeline
    /// (stored and verified, transcribed, analysed), and the transcript is written into the
    /// feedback comment under the request type and topic the customer chose.
    /// Retrying with the same submission id is idempotent.
    /// </summary>
    private static async Task<IResult> SubmitVoiceAsync(HttpRequest request,
        ITaxonomyRepository repository, IOptions<AiOptions> ai, IOptions<CustomerPortalOptions> options,
        IVoiceRepository voice, IFeedbackRepository feedback, RecordingIngestService ingest, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Enabled) return Results.NotFound();
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "The request is not valid." });

        // The request as a whole is capped by the host's portal size guard before it gets here.
        var form = await request.ReadFormAsync(ct);
        var audio = form.Files.GetFile("audio");
        PortalSubmission? body;
        try
        {
            body = JsonSerializer.Deserialize<PortalSubmission>(form["submission"].ToString(), CxJson.Options);
        }
        catch (JsonException)
        {
            body = null;
        }
        if (body is null) return Results.BadRequest(new { error = "The request is not valid." });

        if (audio is null || audio.Length == 0) return Results.BadRequest(new { error = "Voicemail is empty." });
        if (audio.Length > settings.MaxVoiceBytes)
            return Results.Json(new { error = "The voice message is too long; Record shorter." },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        var contentType = (audio.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (!contentType.StartsWith("audio/", StringComparison.Ordinal))
            return Results.BadRequest(new { error = "The sent file is not audio." });

        var catalog = await TopicCatalog.LoadAsync(repository, settings.TenantId, ai.Value.TaxonomyName, ct);
        var errors = PortalQuestionnaire.Validate(body, catalog, hasVoice: true);
        if (errors.Count > 0) return Results.BadRequest(new { error = string.Join(" ", errors) });
        if (body.Revision != catalog.Revision)
            return Results.Conflict(new { error = "Categories have changed; Refresh the page and reply again." });

        var tenantId = settings.TenantId;
        var customerId = request.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)!.Value;
        var callId = $"pv-{PortalQuestionnaire.SubmissionKey(body, tenantId, customerId)}";
        var accepted = Results.Accepted(value: new { receipt = body.SubmissionId, status = "accepted" });

        // A retry after a lost response must not reset a comment that already holds the transcript.
        var existing = await voice.GetCallAsync(tenantId, callId, ct);
        if (existing is not null && (await voice.GetRecordingsForCallAsync(existing.Id, ct))
                .Any(r => RecordingStorageStates.IsAnalyzable(r.StorageState)))
            return accepted;

        var durationMs = long.TryParse(form["duration_ms"], out var parsed) && parsed is > 0 and < 3_600_000
            ? parsed
            : (long?)null;
        var now = DateTimeOffset.UtcNow;

        await voice.UpsertCallAsync(new VoiceCall
        {
            TenantId = tenantId,
            CallId = callId,
            CustomerId = customerId,
            Direction = "inbound",
            Provider = "cx-customer-portal",
            ProviderCallId = callId,
            CorrelationId = callId,
            StartedAt = durationMs is > 0 ? now.AddMilliseconds(-durationMs.Value) : now,
            EndedAt = now,
            DurationMs = durationMs,
            LifecycleState = CallLifecycleStates.Ended
        }, ct);

        await feedback.UpsertAsync(new Feedback
        {
            TenantId = tenantId,
            Provider = "cx-customer-portal",
            ExternalId = callId,
            CustomerId = customerId,
            CallId = callId,
            FeedbackType = body.Kind == "complaint" ? FeedbackTypes.Complaint : FeedbackTypes.Voice,
            Rating = body.Rating,
            CsatScore = body.Kind == "feedback" ? body.Rating : null,
            CesScore = body.Kind == "feedback" ? body.CesScore : null,
            SelectedTopic = body.Category,
            Comment = PortalQuestionnaire.VoiceHeader(body, catalog),
            Channel = "web",
            Status = InboxStatuses.New,
            SubmittedAt = now
        }, ct);

        await using var stream = audio.OpenReadStream();
        var recording = await ingest.IngestFromStreamAsync(new RecordingIngestRequest(
            tenantId, callId, $"{callId}-audio", SourceUri: null, contentType, durationMs,
            Channels: 1, SampleRate: null, Codec: null, Language: "en", audio.Length), stream, ct);

        return recording is null
            ? Results.Problem("Saving the voice message was not completed; Try again.", statusCode: StatusCodes.Status500InternalServerError)
            : accepted;
    }
}

public sealed class CustomerPortalOptions
{
    public bool Enabled { get; set; } = true;
    public string TenantId { get; set; } = "bestcx";
    /// <summary>About five minutes of the 16 kHz mono WAV the portal records.</summary>
    public long MaxVoiceBytes { get; set; } = 10L * 1024 * 1024;
}

public sealed record PortalQuestion(string Id, string Label, string Type, bool Required);
public sealed record PortalSubmission(Guid SubmissionId, string? Kind, string? Category, int Revision,
    string? Subject, string? Comment, int? Rating, int? NpsScore,
    Dictionary<string, string>? Answers, int? CesScore = null);

public static class PortalQuestionnaire
{
    public static IReadOnlyList<PortalQuestion> Questions(string title) => new[]
    {
        new PortalQuestion("satisfaction", $"From his experience in «{title}» How satisfied are you?", "rating", true),
        new PortalQuestion("effort", $"Doing work or receiving services in «{title}» How easy was it?", "effort", true),
        new PortalQuestion("experience", $"Your experience in «{title}» How was it?", "text", true),
        new PortalQuestion("improvement", $"What a change in «{title}» Does it make your experience better?", "text", true)
    };

    /// <param name="hasVoice">A voice message replaces the written description; its transcript becomes the comment.</param>
    public static IReadOnlyList<string> Validate(PortalSubmission body, TopicCatalog catalog, bool hasVoice = false)
    {
        var errors = new List<string>();
        if (body.SubmissionId == Guid.Empty) errors.Add("The submission ID is not valid.");
        if (body.Kind is not ("complaint" or "feedback" or "voc")) errors.Add("The request type is not valid.");
        if (!catalog.IsKnown(body.Category)) errors.Add("Select an active category.");
        if (body.Subject?.Length > 160 || body.Comment?.Length > 4000)
            errors.Add("The length of one of the responses exceeds the allowed limit.");
        if (body.Rating is < 1 or > 5 || body.NpsScore is < 0 or > 10) errors.Add("Points are not valid.");
        if (body.CesScore is < 1 or > 7) errors.Add("Choose an ease score from 1 to 7.");
        if (body.Kind == "complaint" && string.IsNullOrWhiteSpace(body.Subject)) errors.Add("Enter the title of the complaint.");
        if (hasVoice && body.Kind == "voc") errors.Add("You must answer the voice of the customer questionnaire in writing.");
        if (body.Kind != "voc" && !hasVoice && string.IsNullOrWhiteSpace(body.Comment)) errors.Add("Write a description of your experience.");
        if (body.Kind == "voc")
        {
            if (body.NpsScore is null) errors.Add("Select the recommendation score.");
            var answers = body.Answers ?? new();
            foreach (var question in Questions("Topic"))
            {
                var answer = answers.GetValueOrDefault(question.Id);
                if (string.IsNullOrWhiteSpace(answer) || answer.Length > 2000)
                    errors.Add("Answer all the questions (Maximum 2000 characters for each answer).");
                if (question.Type == "rating" && (!int.TryParse(answer, out var rating) || rating is < 1 or > 5))
                    errors.Add("Choose a satisfaction score from 1 to 5.");
                if (question.Type == "effort" && (!int.TryParse(answer, out var effort) || effort is < 1 or > 7))
                    errors.Add("Choose an ease score from 1 to 7.");
            }
            if (answers.Keys.Any(k => !Questions("Topic").Any(q => q.Id == k))) errors.Add("The question is unknown.");
        }
        return errors;
    }

    /// <summary>
    /// The comment of a voice submission before its transcript arrives: the same request-type
    /// and topic lines as a written one, then the marker the transcript is written after.
    /// </summary>
    public static string VoiceHeader(PortalSubmission body, TopicCatalog catalog)
    {
        var title = catalog.TitlePath(catalog.Find(body.Category)!);
        var header = $"Type of request: {(body.Kind == "complaint" ? "Complaint" : "Feedback")} (Voice message)\nSelected topic: {title}\n";
        if (!string.IsNullOrWhiteSpace(body.Subject)) header += $"Title: {body.Subject.Trim()}\n";
        if (body.Rating is not null) header += $"Satisfaction rating: {body.Rating}/5\n";
        if (body.Kind == "feedback" && body.CesScore is not null) header += $"Ease of doing work score: {body.CesScore}/7\n";
        if (!string.IsNullOrWhiteSpace(body.Comment)) header += $"Written explanation: {body.Comment.Trim()}\n";
        return header + VoiceFeedbackComment.TranscriptMarker;
    }

    public static string SubmissionKey(PortalSubmission body, string tenantId, string customerId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\n{customerId}\n{body.SubmissionId:N}")))[..32].ToLowerInvariant();

    public static CanonicalEvent ToEvent(PortalSubmission body, TopicCatalog catalog, string tenantId, string customerId)
    {
        var topic = catalog.Find(body.Category)!;
        var title = catalog.TitlePath(topic);
        var comment = $"Type of request: {body.Kind switch { "complaint" => "Complaint", "voc" => "Voice of customer / Questionnaire", _ => "Feedback" }}\nSelected topic: {title}\n";
        if (!string.IsNullOrWhiteSpace(body.Subject)) comment += $"Title: {body.Subject.Trim()}\n";
        if (body.Kind == "voc")
            comment += string.Join("\n", Questions(title).Select(q => $"{q.Label}\n{body.Answers![q.Id].Trim()}"))
                + $"\nRecommendation score: {body.NpsScore}/10";
        if (!string.IsNullOrWhiteSpace(body.Comment)) comment += "\n" + body.Comment.Trim();

        return new CanonicalEvent
        {
            EventId = $"portal-{SubmissionKey(body, tenantId, customerId)}", TenantId = tenantId,
            EventName = EventNames.FeedbackSubmitted, EventCategory = EventCategories.Feedback,
            OccurredAt = DateTimeOffset.UtcNow,
            // Identity comes from the authenticated session, never the submission body.
            Customer = new CustomerRef { CustomerId = customerId },
            Source = new SourceRef { Type = "web", Service = "cx-customer-portal", Channel = "web" },
            Privacy = new PrivacyRef { ContainsPii = true, Classification = "confidential" },
            Metadata = new JsonObject
            {
                ["external_id"] = SubmissionKey(body, tenantId, customerId), ["comment"] = comment,
                ["submission_id"] = body.SubmissionId.ToString(),
                ["submission_kind"] = body.Kind, ["selected_category"] = topic.Code,
                ["category_title"] = title, ["taxonomy_revision"] = catalog.Revision,
                ["rating"] = body.Kind == "voc" ? int.Parse(body.Answers!["satisfaction"]) : body.Rating,
                ["csat_score"] = body.Kind == "voc" ? int.Parse(body.Answers!["satisfaction"])
                    : body.Kind == "feedback" ? body.Rating : null,
                ["ces_score"] = body.Kind == "voc" ? int.Parse(body.Answers!["effort"])
                    : body.Kind == "feedback" ? body.CesScore : null,
                ["nps_score"] = body.NpsScore,
                ["questionnaire"] = body.Kind == "voc" ? JsonSerializer.SerializeToNode(
                    Questions(title).Select(q => new { q.Id, q.Label, answer = body.Answers![q.Id].Trim() }), CxJson.Options) : null
            }
        };
    }
}
