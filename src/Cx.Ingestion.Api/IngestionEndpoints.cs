using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Application.Ai;
using Cx.Application.Ingestion;
using Cx.Application.Options;
using Cx.Contracts;
using Cx.Contracts.Events;
using Cx.Contracts.Security;
using Cx.Domain.Entities;
using Cx.Infrastructure.Security;
using Cx.Integrations.Support;
using Cx.Voice.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Ingestion.Api;

/// <summary>
/// The write surface of the platform (spec 9). Every endpoint authenticates, returns 202
/// once the event is on Kafka, and never blocks on database persistence.
/// </summary>
public static class IngestionEndpoints
{
    public static IEndpointRouteBuilder MapIngestionEndpoints(this IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/v1/events")
            .WithTags("Ingestion")
            .RequireAuthorization(CxPermissions.EventIngest)
            .RequireRateLimiting(CxRateLimiting.IngestionPolicy);

        events.MapPost("/", IngestOne)
            .WithName("IngestEvent")
            .WithSummary("Accept a single canonical CX event");

        events.MapPost("/batch", IngestBatch)
            .WithName("IngestEventBatch")
            .WithSummary("Accept a batch of canonical CX events");

        var webhooks = app.MapGroup("/v1/webhooks")
            .WithTags("Integrations")
            .RequireAuthorization(CxPermissions.EventIngest)
            .RequireRateLimiting(CxRateLimiting.IngestionPolicy);

        webhooks.MapPost("/support", (HttpContext ctx, [FromBody] JsonNode body, IServiceProvider sp,
                CancellationToken ct) =>
            HandleAdapterAsync(ctx, body, new SupportTicketAdapter(), sp, ct))
            .WithName("SupportWebhook");

        webhooks.MapPost("/feedback", (HttpContext ctx, [FromBody] JsonNode body, IServiceProvider sp,
                CancellationToken ct) =>
            HandleAdapterAsync(ctx, body, new FeedbackAdapter(), sp, ct))
            .WithName("FeedbackWebhook");

        webhooks.MapPost("/voip", HandleVoipWebhookAsync)
            .WithName("VoipWebhook")
            .WithSummary("VoIP call lifecycle webhook");

        app.MapPost("/v1/voice/recordings", UploadRecordingAsync)
            .WithTags("Ingestion")
            .RequireAuthorization(CxPermissions.EventIngest)
            .WithName("UploadRecording")
            .WithSummary("Direct recording upload; streams to object storage and verifies it")
            .DisableAntiforgery();

        // Operator-facing entry points of the console. They need the feedback permission,
        // not the machine ingest key, and run through the same pipelines as integrations.
        app.MapPost("/v1/feedback", SubmitFeedbackAsync)
            .WithTags("Feedback")
            .RequireAuthorization(CxPermissions.FeedbackManage)
            .RequireRateLimiting(CxRateLimiting.IngestionPolicy)
            .WithName("SubmitFeedback")
            .WithSummary("Record a text feedback by hand; it is stored, then analysed by AI");

        app.MapPost("/v1/complaints", SubmitComplaintAsync)
            .WithTags("Feedback")
            .RequireAuthorization(CxPermissions.Admin)
            .RequireRateLimiting(CxRateLimiting.IngestionPolicy)
            .WithName("SubmitStaffComplaint")
            .WithSummary("Admin records a text complaint for an existing customer and active topic");

        app.MapPost("/v1/voice/feedback", SubmitVoiceFeedbackAsync)
            .WithTags("Feedback")
            .RequireAuthorization(CxPermissions.FeedbackManage)
            .RequireRateLimiting(CxRateLimiting.IngestionPolicy)
            .WithName("SubmitVoiceFeedback")
            .WithSummary("Upload or record a customer voice message; it is transcribed, then analysed")
            .DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> SubmitComplaintAsync(HttpContext context, StaffComplaint body,
        ICustomerRepository customers, ITaxonomyRepository taxonomies, IOptions<AiOptions> ai,
        IEventIngestionService ingestion, IAuditRepository audit, CancellationToken ct)
    {
        var user = context.ToCxUser();
        var catalog = await TopicCatalog.LoadAsync(taxonomies, user.TenantId, ai.Value.TaxonomyName, ct);
        var errors = StaffComplaints.Validate(body, catalog);
        if (errors.Count > 0) return Results.BadRequest(new { error = string.Join(" ", errors) });
        if (body.Revision != catalog.Revision)
            return Results.Conflict(new { error = "Categories have changed; Open the form again." });
        var customer = await customers.GetAsync(user.TenantId, body.CustomerId!, ct);
        if (customer is null || customer.MergedIntoId is not null)
            return Results.NotFound(new { error = "No customer found." });
        var evt = StaffComplaints.ToEvent(body, catalog, user.TenantId, user.Subject);
        var outcome = await ingestion.IngestAsync(JsonSerializer.SerializeToNode(evt, CxJson.Options), user.TenantId, ct);
        if (!outcome.Accepted) return Results.BadRequest(new { error = outcome.Reason, errors = outcome.Errors });
        await audit.WriteAsync(new AuditLog
        {
            TenantId = user.TenantId, ActorId = user.Subject, Action = "complaint.create",
            ResourceType = "cx_event", ResourceId = evt.EventId, IpAddress = user.IpAddress,
            OccurredAt = DateTimeOffset.UtcNow,
            After = CxJson.Serialize(new { body.CustomerId, body.Category })
        }, ct);
        return Results.Accepted($"/v1/events/{outcome.EventId}", new { event_id = outcome.EventId, status = outcome.Status });
    }

    private static async Task<IResult> SubmitFeedbackAsync(HttpContext context, [FromBody] JsonNode? body,
        IServiceProvider services, CancellationToken ct)
    {
        if (body is not JsonObject item)
            return Results.BadRequest(new { error = "body must be a JSON object" });

        // The console marks its own submissions; everything else is the feedback adapter's contract.
        item["provider"] ??= "cx-console";
        item["channel"] ??= "manual";

        var adapter = new FeedbackAdapter();
        var payload = item.ToJsonString();
        var errors = adapter.Validate(payload);
        if (errors.Count > 0)
            return Results.BadRequest(new { error = "Enter the feedback text or customer rating.", errors });

        var events = adapter.MapToCanonicalEvents(payload, new Dictionary<string, string>());
        return await PublishAsync(context, events, services, "cx-console", ct);
    }

    /// <summary>
    /// A customer voice message, recorded in the browser or uploaded as a file. Unlike a
    /// call from the VoIP connector there is no call to attach to, so one is created here;
    /// the feedback row links to it and receives the transcript once transcription finishes.
    /// Retrying with the same feedbackId is idempotent.
    /// </summary>
    private static async Task<IResult> SubmitVoiceFeedbackAsync(
        HttpContext context,
        [FromQuery] string? customerId,
        [FromQuery] string? feedbackId,
        [FromQuery] long? durationMs,
        [FromQuery] string? productId,
        [FromQuery] string? channel,
        [FromQuery] string? language,
        IVoiceRepository voice,
        IFeedbackRepository feedback,
        RecordingIngestService ingest,
        CancellationToken ct)
    {
        var user = context.ToCxUser();
        var contentType = (context.Request.ContentType ?? "application/octet-stream").Split(';')[0].Trim();

        if (!contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = $"File type «{contentType}» It is not audio." });
        }

        if (context.Request.ContentLength is 0)
            return Results.BadRequest(new { error = "The audio file is empty." });

        var key = string.IsNullOrWhiteSpace(feedbackId) ? Ulid.NewUlid().ToString() : feedbackId.Trim();
        if (key.Length > 100 || key.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            return Results.BadRequest(new { error = "feedbackId is invalid" });

        var callId = $"vf-{key}";
        var customer = string.IsNullOrWhiteSpace(customerId) ? null : customerId.Trim();
        var now = DateTimeOffset.UtcNow;

        await voice.UpsertCallAsync(new VoiceCall
        {
            TenantId = user.TenantId,
            CallId = callId,
            CustomerId = customer,
            Direction = "inbound",
            Provider = "cx-console",
            ProviderCallId = callId,
            CorrelationId = callId,
            ProductId = string.IsNullOrWhiteSpace(productId) ? null : productId.Trim(),
            StartedAt = durationMs is > 0 ? now.AddMilliseconds(-durationMs.Value) : now,
            EndedAt = now,
            DurationMs = durationMs is > 0 ? durationMs : null,
            LifecycleState = CallLifecycleStates.Ended
        }, ct);

        var feedbackRowId = await feedback.UpsertAsync(new Feedback
        {
            TenantId = user.TenantId,
            Provider = "voice",
            ExternalId = callId,
            CallId = callId,
            CustomerId = customer,
            ProductId = string.IsNullOrWhiteSpace(productId) ? null : productId.Trim(),
            FeedbackType = FeedbackTypes.Voice,
            Channel = string.IsNullOrWhiteSpace(channel) ? "voice" : channel.Trim(),
            Status = InboxStatuses.New,
            SubmittedAt = now
        }, ct);

        var recording = await ingest.IngestFromStreamAsync(new RecordingIngestRequest(
            user.TenantId,
            callId,
            $"{callId}-audio",
            SourceUri: null,
            contentType,
            durationMs,
            Channels: 1,
            SampleRate: null,
            Codec: null,
            string.IsNullOrWhiteSpace(language) ? "en" : language,
            context.Request.ContentLength), context.Request.Body, ct);

        if (recording is null)
            return Results.Problem("Saving the audio file was not completed.", statusCode: StatusCodes.Status500InternalServerError);

        return Results.Accepted($"/v1/feedback/{feedbackRowId}", new
        {
            feedback_id = feedbackRowId,
            call_id = callId,
            recording_id = recording.Id,
            size_bytes = recording.SizeBytes,
            storage_state = recording.StorageState
        });
    }

    private static async Task<IResult> IngestOne(HttpContext context, [FromBody] JsonNode? body,
        IEventIngestionService ingestion, CancellationToken ct)
    {
        var user = context.ToCxUser();
        var outcome = await ingestion.IngestAsync(body, user.TenantId, ct);

        if (!outcome.Accepted)
        {
            return Results.BadRequest(new
            {
                event_id = outcome.EventId,
                status = outcome.Status,
                reason = outcome.Reason,
                errors = outcome.Errors
            });
        }

        return Results.Accepted($"/v1/events/{outcome.EventId}",
            new { event_id = outcome.EventId, status = outcome.Status });
    }

    private static async Task<IResult> IngestBatch(HttpContext context, [FromBody] JsonNode? body,
        IEventIngestionService ingestion, IOptions<IngestionOptions> options, CancellationToken ct)
    {
        var items = body switch
        {
            JsonArray array => array.ToList(),
            JsonObject obj when obj["events"] is JsonArray nested => nested.ToList(),
            _ => null
        };

        if (items is null)
            return Results.BadRequest(new { error = "body must be a JSON array or {\"events\": [...]}" });

        if (items.Count > options.Value.MaxBatchSize)
        {
            return Results.BadRequest(new
            {
                error = $"batch of {items.Count} exceeds the configured maximum of {options.Value.MaxBatchSize}"
            });
        }

        var user = context.ToCxUser();
        var results = await ingestion.IngestBatchAsync(items, user.TenantId, ct);

        // 202 with per-item results: partial success is the normal case for a batch (spec 9).
        return Results.Accepted("/v1/events", new
        {
            accepted = results.Count(r => r.Accepted),
            rejected = results.Count(r => !r.Accepted),
            results = results.Select(r => new
            {
                event_id = r.EventId,
                status = r.Status,
                reason = r.Reason,
                errors = r.Errors
            })
        });
    }

    private static async Task<IResult> HandleAdapterAsync(HttpContext context, JsonNode body,
        IExternalCxAdapter adapter, IServiceProvider services, CancellationToken ct)
    {
        var payload = body.ToJsonString();
        var errors = adapter.Validate(payload);
        if (errors.Count > 0)
            return Results.BadRequest(new { provider = adapter.Provider, errors });

        var headers = context.Request.Headers
            .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        var events = adapter.MapToCanonicalEvents(payload, headers);
        return await PublishAsync(context, events, services, adapter.Provider, ct);
    }

    private static async Task<IResult> HandleVoipWebhookAsync(HttpContext context, [FromBody] JsonNode body,
        IServiceProvider services, CancellationToken ct)
    {
        // The VoIP payload is mapped to canonical voice events; the recording itself is
        // never inlined - only a reference the voice worker will fetch (rule R5).
        var adapter = new VoipWebhookAdapter();
        var payload = body.ToJsonString();

        var errors = adapter.Validate(payload);
        if (errors.Count > 0)
            return Results.BadRequest(new { provider = adapter.Provider, errors });

        var headers = context.Request.Headers
            .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        var events = adapter.MapToCanonicalEvents(payload, headers);
        return await PublishAsync(context, events, services, adapter.Provider, ct);
    }

    private static async Task<IResult> PublishAsync(HttpContext context, IReadOnlyList<CanonicalEvent> events,
        IServiceProvider services, string provider, CancellationToken ct)
    {
        if (events.Count == 0)
            return Results.Accepted(value: new { provider, accepted = 0, results = Array.Empty<object>() });

        var ingestion = services.GetRequiredService<IEventIngestionService>();
        var checkpoints = services.GetRequiredService<IIntegrationCheckpointRepository>();
        var user = context.ToCxUser();

        var nodes = events
            .Select(e => (JsonNode?)JsonSerializer.SerializeToNode(e, CxJson.Options))
            .ToList();

        var results = await ingestion.IngestBatchAsync(nodes, user.TenantId, ct);
        var accepted = results.Count(r => r.Accepted);

        await checkpoints.SaveCursorAsync(user.TenantId, provider, "webhook", null, accepted, ct);

        return Results.Accepted(value: new
        {
            provider,
            accepted,
            rejected = results.Count - accepted,
            results = results.Select(r => new { event_id = r.EventId, status = r.Status, reason = r.Reason })
        });
    }

    /// <summary>
    /// Streams an uploaded recording into object storage. The request body is read as a
    /// stream, so a long call never lands in memory or on the web tier's disk.
    /// </summary>
    private static async Task<IResult> UploadRecordingAsync(
        HttpContext context,
        [FromQuery] string callId,
        [FromQuery] string? recordingId,
        [FromQuery] long? durationMs,
        [FromQuery] int? channels,
        [FromQuery] string? language,
        RecordingIngestService ingest,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callId))
            return Results.BadRequest(new { error = "callId is required" });

        var user = context.ToCxUser();
        var contentType = context.Request.ContentType ?? "application/octet-stream";

        if (!contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) &&
            !contentType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = $"unsupported content type '{contentType}'" });
        }

        var request = new RecordingIngestRequest(
            user.TenantId,
            callId,
            recordingId ?? Ulid.NewUlid().ToString(),
            SourceUri: null,
            contentType.Split(';')[0].Trim(),
            durationMs,
            channels,
            SampleRate: null,
            Codec: null,
            language,
            context.Request.ContentLength);

        var recording = await ingest.IngestFromStreamAsync(request, context.Request.Body, ct);

        if (recording is null)
            return Results.Problem("recording ingest did not complete", statusCode: StatusCodes.Status500InternalServerError);

        return Results.Accepted($"/v1/voice/calls/{callId}", new
        {
            recording_id = recording.Id,
            object_key = recording.ObjectKey,
            size_bytes = recording.SizeBytes,
            checksum_sha256 = recording.ChecksumSha256,
            storage_state = recording.StorageState
        });
    }
}
