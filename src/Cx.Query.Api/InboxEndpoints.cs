using Cx.Application.Abstractions;
using Cx.Application.Ai;
using Cx.Application.Jobs;
using Cx.Application.Options;
using Cx.Application.Query;
using Cx.Contracts.Ai;
using Cx.Contracts.Events;
using Cx.Contracts.Voice;
using Cx.Contracts;
using System.Text.Json.Nodes;
using Cx.Contracts.Security;
using Cx.Domain.Entities;
using Cx.Infrastructure.Security;
using Cx.Infrastructure.Operations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Cx.Query.Api;

/// <summary>Events, feedback, support and global search (spec 24, panel proposal 6 and 15).</summary>
public static class InboxEndpoints
{
    public static IEndpointRouteBuilder MapInboxEndpoints(this IEndpointRouteBuilder app)
    {
        MapEvents(app);
        MapFeedback(app);
        MapSupport(app);
        MapSearch(app);
        return app;
    }

    private static void MapEvents(IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/v1/events")
            .WithTags("Events")
            .RequireAuthorization(CxPermissions.EventRead);

        events.MapGet("/", async (HttpContext ctx, [FromQuery] string? customerId, [FromQuery] string? sessionId,
            [FromQuery] string? eventName, [FromQuery] string? category, [FromQuery] string? productId,
            [FromQuery] string? correlationId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
            [FromQuery] string? cursor, [FromQuery] int? limit,
            IEventRepository repository, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            var take = Math.Clamp(limit ?? 50, 1, 200);

            var rows = await repository.QueryAsync(new EventQuery
            {
                TenantId = user.TenantId,
                CustomerId = customerId,
                SessionId = sessionId,
                EventName = eventName,
                EventCategory = category,
                ProductId = productId,
                CorrelationId = correlationId,
                From = from,
                To = to,
                Cursor = QueryHelpers.ParseCursor(cursor),
                Limit = take
            }, ct);

            var page = Cx.Application.Common.Page<CxEvent>.From(rows, take,
                e => new Cx.Application.Common.Cursor(e.OccurredAt, e.EventId).Encode());

            return Results.Ok(page);
        }).WithName("QueryEvents");

        events.MapGet("/{eventId}", async (HttpContext ctx, string eventId, IEventRepository repository,
            CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            var found = await repository.GetByEventIdAsync(eventId, ct);

            // Tenant isolation is enforced on read, not just on write.
            if (found is null || found.TenantId != user.TenantId)
                return Results.NotFound(new { error = "event not found" });

            // The raw payload is a technical view; only roles that may see PII get it.
            var payload = user.Has(CxPermissions.PiiRead) ? found.Payload : null;

            return Results.Ok(new
            {
                found.EventId,
                found.EventName,
                found.EventCategory,
                found.OccurredAt,
                found.ReceivedAt,
                found.CustomerId,
                found.SessionId,
                found.ProductId,
                found.SourceType,
                found.SourceService,
                found.TraceId,
                found.CorrelationId,
                payload
            });
        }).WithName("GetEvent");
    }

    private static void MapFeedback(IEndpointRouteBuilder app)
    {
        var feedback = app.MapGroup("/v1/feedback")
            .WithTags("Feedback")
            .RequireAuthorization(CxPermissions.CustomerRead);

        feedback.MapGet("/", async (HttpContext ctx, [FromQuery] string? customerId, [FromQuery] string? productId,
            [FromQuery] string? status, [FromQuery] bool? reviewed, [FromQuery] string? q,
            [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
            [FromQuery] string? cursor, [FromQuery] int? limit,
            IFeedbackRepository repository, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            return Results.Ok(await repository.QueryAsync(new InboxQuery
            {
                TenantId = user.TenantId,
                CustomerId = customerId,
                ProductId = productId,
                Status = status,
                Reviewed = reviewed,
                Search = q,
                From = from,
                To = to,
                Cursor = QueryHelpers.ParseCursor(cursor),
                Limit = limit ?? 50
            }, ct));
        }).WithName("QueryFeedback");

        feedback.MapGet("/grid", async (HttpContext ctx, [FromQuery] string? q, [FromQuery] string? status,
            [FromQuery] string? sentiment, [FromQuery] string? severity, [FromQuery] string? category,
            [FromQuery] string? subcategory, [FromQuery] string? channel, [FromQuery] string? kind, [FromQuery] string? inbox,
            [FromQuery] string? customerId, [FromQuery] string? analysis, [FromQuery] DateTimeOffset? from,
            [FromQuery] DateTimeOffset? to, [FromQuery] int? page, [FromQuery] int? pageSize,
            IFeedbackRepository repository, ITaxonomyRepository taxonomies, IOptions<AiOptions> ai,
            CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();

            if (inbox is not (null or "feedback" or "complaints"))
                return Results.BadRequest(new { error = "The selected fund is not valid." });

            // Analyses store the deepest topic chosen, so a subtopic filter covers its whole branch.
            IReadOnlyList<string>? subcategories = null;
            if (!string.IsNullOrWhiteSpace(subcategory))
            {
                var taxonomy = await taxonomies.EnsureAsync(user.TenantId, ai.Value.TaxonomyName, ct);
                subcategories = TopicBranch(await taxonomies.ListTopicsAsync(taxonomy.Id, ct), subcategory);
            }

            return Results.Ok(await repository.QueryGridAsync(new FeedbackGridQuery
            {
                TenantId = user.TenantId,
                Search = q,
                Status = status,
                Sentiment = sentiment,
                Severity = severity,
                Category = category,
                Subcategories = subcategories,
                Channel = channel,
                Inbox = inbox,
                Kind = kind,
                CustomerId = customerId,
                AnalysisState = analysis,
                From = from,
                To = to,
                Page = page ?? 1,
                PageSize = pageSize ?? 20,
                PromptVersion = ai.Value.PromptVersion
            }, ct));
        }).WithName("FeedbackGrid");

        feedback.MapGet("/{id:guid}", async (HttpContext ctx, Guid id, IFeedbackRepository repository,
            IAiAnalysisRepository analyses, ICustomerRepository customers, IEventRepository events, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            var found = await repository.GetAsync(user.TenantId, id, ct);
            if (found is null) return Results.NotFound(new { error = "feedback not found" });

            // A voice message is analysed as its call; a text feedback as the event that carried it.
            var (entityType, entityId) = AnalysisTarget(found);
            var analysis = entityId is null
                ? null
                : await analyses.GetLatestForEntityAsync(user.TenantId, entityType, entityId, ct);

            var customer = found.CustomerId is null
                ? null
                : await customers.GetAsync(user.TenantId, found.CustomerId, ct);

            object? submission = null;
            if (found.Provider is "cx-customer-portal" or "cx-console" && found.EventId is not null)
            {
                var stored = await events.GetByEventIdAsync(found.EventId, ct);
                if (stored?.TenantId == user.TenantId)
                {
                    var metadata = JsonNode.Parse(stored.Payload)?["metadata"];
                    submission = new
                    {
                        receipt = metadata?["submission_id"]?.GetValue<string>()
                            ?? (Guid.TryParse(found.ExternalId, out var receiptId) ? receiptId.ToString() : found.ExternalId),
                        category_title = metadata?["category_title"]?.GetValue<string>(),
                        name = user.Has(CxPermissions.PiiRead) ? metadata?["contact_name"]?.GetValue<string>() : null,
                        contact = user.Has(CxPermissions.PiiRead) ? metadata?["contact"]?.GetValue<string>() : null
                    };
                }
            }
            return Results.Ok(new { feedback = found, analysis, customer_name = customer?.DisplayName, submission });
        }).WithName("GetFeedback");

        feedback.MapPost("/{id:guid}/reanalyze", ReanalyzeAsync).WithName("ReanalyzeFeedback");

        // A draft task for the technical team, written from this feedback and the customer's
        // other recent problems. Nothing is stored: the operator edits and copies it.
        feedback.MapPost("/{id:guid}/follow-up-task", async (HttpContext ctx, Guid id,
            IFeedbackRepository repository, IFollowUpTaskWriter writer, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            if (!user.Has(CxPermissions.FeedbackManage)) return Results.Forbid();

            var found = await repository.GetAsync(user.TenantId, id, ct);
            if (found is null) return Results.NotFound(new { error = "feedback not found" });

            return Results.Ok(await writer.WriteAsync(user.TenantId, found, ct));
        }).WithName("DraftFollowUpTask");


    }

    private static (string EntityType, string? EntityId) AnalysisTarget(Feedback feedback) =>
        feedback.CallId is not null
            ? (AnalysisEntityTypes.VoiceCall, feedback.CallId)
            : (AnalysisEntityTypes.CxEvent, feedback.EventId);

    /// <summary>
    /// Runs the analysis again as a new version (the previous one is kept). For a voice
    /// message without a transcript yet - e.g. transcription failed - it restarts from
    /// transcription, which then triggers the analysis on its own.
    /// </summary>
    /// <summary>The topic's code and every code beneath it, inactive ones included (history still carries them).</summary>
    internal static IReadOnlyList<string> TopicBranch(IReadOnlyList<Topic> topics, string code)
    {
        var root = topics.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));
        if (root is null) return new[] { code };

        var branch = new List<Topic> { root };
        for (var i = 0; i < branch.Count; i++)
            branch.AddRange(topics.Where(t => t.ParentId == branch[i].Id && !branch.Contains(t)));
        return branch.Select(t => t.Code).ToList();
    }

    private static async Task<IResult> ReanalyzeAsync(HttpContext ctx, Guid id, IFeedbackRepository repository,
        IAiAnalysisRepository analyses, IVoiceRepository voice, ISpeechToTextEngine stt,
        IMessagePublisher publisher, IAuditRepository audit, IOptions<KafkaOptions> kafka,
        IOptions<AiOptions> ai, IOptions<VoiceOptions> voiceOptions, CancellationToken ct)
    {
        var user = ctx.ToCxUser();
        if (!user.Has(CxPermissions.AiReprocess)) return Results.Forbid();

        var found = await repository.GetAsync(user.TenantId, id, ct);
        if (found is null) return Results.NotFound(new { error = "feedback not found" });

        var (entityType, entityId) = AnalysisTarget(found);
        if (entityId is null)
            return Results.Conflict(new { error = "This feedback has not yet been finalized; Try again later." });

        var scope = "analysis";
        if (found.CallId is not null)
        {
            var call = await voice.GetCallAsync(user.TenantId, found.CallId, ct);
            var recordings = call is null
                ? Array.Empty<VoiceRecording>()
                : await voice.GetRecordingsForCallAsync(call.Id, ct);
            var ready = recordings.FirstOrDefault(r => RecordingStorageStates.IsAnalyzable(r.StorageState));
            if (ready is null)
                return Results.Conflict(new { error = "The audio file of this feedback is not ready." });

            if (await voice.GetLatestTranscriptAsync(ready.Id, ct) is null)
            {
                scope = "transcript";
                await publisher.PublishAsync(kafka.Value.Topics.VoiceTranscriptionRequest,
                    PartitionKeys.ForCall(found.CallId), CxJson.Serialize(new TranscriptionRequest
                    {
                        JobKey = $"{JobKeys.Transcribe(ready.Id, stt.ModelVersion ?? stt.Model)}:{Ulid.NewUlid()}",
                        TenantId = user.TenantId,
                        CallId = found.CallId,
                        RecordingId = ready.Id,
                        ObjectKey = ready.ObjectKey,
                        Language = ready.Language ?? voiceOptions.Value.DefaultLanguage,
                        Engine = stt.Engine,
                        Model = stt.Model,
                        ModelVersion = stt.ModelVersion,
                        Force = true
                    }), null, ct);
            }
        }

        if (scope == "analysis")
        {
            var previous = await analyses.GetLatestForEntityAsync(user.TenantId, entityType, entityId, ct);
            var promptVersion = ai.Value.PromptVersion;
            await publisher.PublishAsync(kafka.Value.Topics.AiAnalysisRequest,
                PartitionKeys.ForEntity(entityId), CxJson.Serialize(new AiAnalysisRequest
                {
                    // A fresh key: this is deliberately a new job, not a retry of the old one.
                    JobKey = $"{JobKeys.Analyze(entityType, entityId, promptVersion)}:{Ulid.NewUlid()}",
                    TenantId = user.TenantId,
                    EntityType = entityType,
                    EntityId = entityId,
                    AnalysisType = found.CallId is not null ? AnalysisTypes.VoiceAnalysis : AnalysisTypes.EventAnalysis,
                    CustomerId = found.CustomerId,
                    PromptVersion = promptVersion,
                    Force = true,
                    SupersedesAnalysisId = previous?.Id
                }), null, ct);
        }

        await audit.WriteAsync(new AuditLog
        {
            TenantId = user.TenantId,
            ActorId = user.Subject,
            Action = AuditActions.Reprocess,
            ResourceType = "feedback",
            ResourceId = id.ToString(),
            After = CxJson.Serialize(new { scope, entity_type = entityType, entity_id = entityId }),
            IpAddress = user.IpAddress,
            OccurredAt = DateTimeOffset.UtcNow
        }, ct);

        return Results.Accepted($"/v1/feedback/{id}", new { status = "queued", scope });
    }

    private static void MapSupport(IEndpointRouteBuilder app)
    {
        var support = app.MapGroup("/v1/support")
            .WithTags("Support")
            .RequireAuthorization(CxPermissions.SupportRead);

        support.MapGet("/cases", async (HttpContext ctx, [FromQuery] string? customerId,
            [FromQuery] string? productId, [FromQuery] string? status, [FromQuery] string? q,
            [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
            [FromQuery] string? cursor, [FromQuery] int? limit,
            ISupportCaseRepository repository, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            return Results.Ok(await repository.QueryAsync(new InboxQuery
            {
                TenantId = user.TenantId,
                CustomerId = customerId,
                ProductId = productId,
                Status = status,
                Search = q,
                From = from,
                To = to,
                Cursor = QueryHelpers.ParseCursor(cursor),
                Limit = limit ?? 50
            }, ct));
        }).WithName("QuerySupportCases");

        support.MapGet("/cases/{id:guid}", async (HttpContext ctx, Guid id, ISupportCaseRepository repository,
            CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            var found = await repository.GetAsync(user.TenantId, id, ct);
            return found is null
                ? Results.NotFound(new { error = "support case not found" })
                : Results.Ok(found);
        }).WithName("GetSupportCase");
    }

    private static void MapSearch(IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/search", async (HttpContext ctx, [FromQuery] string q, [FromQuery] int? limit,
            IInsightsRepository insights, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "q is required" });

            var hits = await insights.GlobalSearchAsync(user.TenantId, q, limit ?? 25, ct);

            // Transcript hits are only listed for roles allowed to read transcripts.
            var filtered = user.Has(CxPermissions.VoiceTranscriptRead)
                ? hits
                : hits.Where(h => h.Kind != "transcript").ToList();

            return Results.Ok(new { query = q, hits = filtered });
        })
        .WithTags("Search")
        .RequireAuthorization(CxPermissions.CustomerRead)
        .WithName("GlobalSearch");
    }
}
