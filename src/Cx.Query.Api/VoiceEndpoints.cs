using Cx.Application.Abstractions;
using Cx.Application.Jobs;
using Cx.Application.Options;
using Cx.Contracts;
using Cx.Contracts.Ai;
using Cx.Contracts.Events;
using Cx.Contracts.Security;
using Cx.Contracts.Voice;
using Cx.Domain.Entities;
using Cx.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Cx.Query.Api;

/// <summary>
/// Voice Review workspace API (spec 24, 26).
/// Audio is never proxied as bytes: the client gets a short-lived signed URL, and every
/// grant is audited (voice design 19).
/// </summary>
public static class VoiceEndpoints
{
    public static IEndpointRouteBuilder MapVoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var voice = app.MapGroup("/v1/voice")
            .WithTags("Voice")
            .RequireAuthorization(CxPermissions.CustomerRead);

        voice.MapGet("/calls", async (HttpContext ctx, [FromQuery] string? customerId, [FromQuery] string? agentId,
            [FromQuery] string? direction, [FromQuery] string? productId, [FromQuery] string? state,
            [FromQuery] string? q, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
            [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] string? inbox,
            IVoiceRepository repository, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            if (inbox is not (null or "feedback" or "complaints")) return Results.BadRequest(new { error = "The contacts section is not valid." });
            return Results.Ok(await repository.QueryCallsAsync(new VoiceCallQuery
            {
                TenantId = user.TenantId,
                Inbox = inbox ?? "feedback",
                CustomerId = customerId,
                AgentId = agentId,
                Direction = direction,
                ProductId = productId,
                LifecycleState = state,
                Search = q,
                From = from,
                To = to,
                Cursor = QueryHelpers.ParseCursor(cursor),
                Limit = limit ?? 50
            }, ct));
        }).WithName("QueryVoiceCalls");

        voice.MapGet("/calls/{callId}", async (HttpContext ctx, string callId, IVoiceRepository repository,
            IAiAnalysisRepository analyses, CancellationToken ct) =>
        {
            var user = ctx.ToCxUser();
            var call = await repository.GetCallAsync(user.TenantId, callId, ct);
            if (call is null) return Results.NotFound(new { error = "call not found" });

            var recordings = await repository.GetRecordingsForCallAsync(call.Id, ct);
            var analysis = await analyses.GetLatestForEntityAsync(user.TenantId,
                AnalysisEntityTypes.VoiceCall, callId, ct);

            return Results.Ok(new
            {
                call,
                recordings = recordings.Select(r => new
                {
                    r.Id,
                    r.StorageState,
                    r.ContentType,
                    r.DurationMs,
                    r.SizeBytes,
                    r.Channels,
                    r.Language,
                    r.ChecksumSha256,
                    r.CreatedAt,
                    r.VerifiedAt
                }),
                analysis
            });
        }).WithName("GetVoiceCall");

        voice.MapGet("/calls/{callId}/transcript", GetTranscriptAsync)
            .WithName("GetVoiceTranscript");

        voice.MapGet("/calls/{callId}/audio", GetAudioUrlAsync)
            .WithName("GetVoiceAudioUrl");

        voice.MapPost("/calls/{callId}/reprocess", ReprocessAsync)
            .WithName("ReprocessVoiceCall");

        voice.MapPatch("/transcripts/{transcriptId:guid}/segments/{segmentIndex:int}", EditSegmentAsync)
            .WithName("EditTranscriptSegment");

        return app;
    }

    private static async Task<IResult> GetTranscriptAsync(HttpContext ctx, string callId,
        IVoiceRepository repository, CancellationToken ct)
    {
        var user = ctx.ToCxUser();
        if (!user.Has(CxPermissions.VoiceTranscriptRead))
            return Results.Forbid();

        var call = await repository.GetCallAsync(user.TenantId, callId, ct);
        if (call is null) return Results.NotFound(new { error = "call not found" });

        var recordings = await repository.GetRecordingsForCallAsync(call.Id, ct);
        var recording = recordings.FirstOrDefault(r => RecordingStorageStates.IsAnalyzable(r.StorageState));
        if (recording is null) return Results.NotFound(new { error = "no READY recording for this call" });

        var transcript = await repository.GetLatestTranscriptAsync(recording.Id, ct);
        if (transcript is null) return Results.NotFound(new { error = "transcript not available yet" });

        var segments = await repository.GetSegmentsAsync(transcript.Id, ct);

        // Raw text requires the PII permission; everyone else sees the redacted copy.
        var canSeeRaw = user.Has(CxPermissions.PiiRead);

        return Results.Ok(new
        {
            transcript_id = transcript.Id,
            recording_id = recording.Id,
            transcript.Language,
            transcript.Engine,
            transcript.Model,
            transcript.ModelVersion,
            transcript.AverageConfidence,
            transcript.CreatedAt,
            supersedes = transcript.SupersedesTranscriptId,
            redacted = !canSeeRaw,
            text = canSeeRaw ? transcript.TranscriptText : transcript.RedactedText,
            segments = segments.Select(s => new TranscriptSegmentDto
            {
                Index = s.SegmentIndex,
                Speaker = s.Speaker ?? SpeakerLabels.Unknown,
                SpeakerRef = s.SpeakerRef,
                StartMs = s.StartMs,
                EndMs = s.EndMs,
                Text = canSeeRaw ? s.Text : s.RedactedText ?? s.Text,
                Confidence = s.Confidence
            })
        });
    }

    /// <summary>
    /// Returns a short-lived signed URL. Listening is a separate permission from reading
    /// customer data, and download is separate again (voice design 19).
    /// </summary>
    private static async Task<IResult> GetAudioUrlAsync(HttpContext ctx, string callId,
        [FromQuery] Guid? recordingId, IVoiceRepository repository, IObjectStorage storage,
        IAuditRepository audit, IOptions<ObjectStorageOptions> storageOptions, CancellationToken ct)
    {
        var user = ctx.ToCxUser();
        if (!user.Has(CxPermissions.VoiceListen))
            return Results.Forbid();

        var call = await repository.GetCallAsync(user.TenantId, callId, ct);
        if (call is null) return Results.NotFound(new { error = "call not found" });

        var recordings = await repository.GetRecordingsForCallAsync(call.Id, ct);
        var recording = recordingId is null
            ? recordings.FirstOrDefault(r => RecordingStorageStates.IsAnalyzable(r.StorageState))
            : recordings.FirstOrDefault(r => r.Id == recordingId);

        if (recording is null) return Results.NotFound(new { error = "recording not found" });

        if (!RecordingStorageStates.IsAnalyzable(recording.StorageState))
            return Results.Conflict(new { error = $"recording is in state {recording.StorageState}" });

        var ttl = storageOptions.Value.SignedUrlTtl;
        var url = await storage.CreateSignedUrlAsync(recording.ObjectKey, ttl, ct);

        await audit.WriteAsync(new AuditLog
        {
            TenantId = user.TenantId,
            ActorId = user.Subject,
            Action = AuditActions.AudioAccess,
            ResourceType = "voice_recording",
            ResourceId = recording.Id.ToString(),
            After = CxJson.Serialize(new { call_id = callId, ttl_seconds = (int)ttl.TotalSeconds }),
            IpAddress = user.IpAddress,
            OccurredAt = DateTimeOffset.UtcNow
        }, ct);

        return Results.Ok(new
        {
            url,
            expires_at = DateTimeOffset.UtcNow.Add(ttl),
            content_type = recording.ContentType,
            duration_ms = recording.DurationMs,
            can_download = user.Has(CxPermissions.Export)
        });
    }

    /// <summary>Reprocess creates a NEW version; it never overwrites the previous one (spec 17).</summary>
    private static async Task<IResult> ReprocessAsync(HttpContext ctx, string callId,
        [FromBody] ReprocessRequest? body, IVoiceRepository repository, IMessagePublisher publisher,
        IAiAnalysisRepository analyses, ISpeechToTextEngine stt, IAuditRepository audit,
        IOptions<KafkaOptions> kafka, IOptions<AiOptions> ai, IOptions<VoiceOptions> voiceOptions,
        CancellationToken ct)
    {
        var user = ctx.ToCxUser();
        if (!user.Has(CxPermissions.AiReprocess))
            return Results.Forbid();

        var call = await repository.GetCallAsync(user.TenantId, callId, ct);
        if (call is null) return Results.NotFound(new { error = "call not found" });

        var recordings = await repository.GetRecordingsForCallAsync(call.Id, ct);
        var recording = recordings.FirstOrDefault(r => RecordingStorageStates.IsAnalyzable(r.StorageState));
        if (recording is null) return Results.Conflict(new { error = "no READY recording to reprocess" });

        var scope = body?.Scope ?? ReprocessScopes.Analysis;
        var promptVersion = body?.PromptVersion ?? ai.Value.PromptVersion;

        if (scope is ReprocessScopes.Transcript or ReprocessScopes.All)
        {
            await publisher.PublishAsync(kafka.Value.Topics.VoiceTranscriptionRequest,
                PartitionKeys.ForCall(callId), CxJson.Serialize(new TranscriptionRequest
                {
                    // A fresh key per reprocess: this is deliberately a new job, not a retry.
                    JobKey = $"{JobKeys.Transcribe(recording.Id, stt.ModelVersion ?? stt.Model)}:{Ulid.NewUlid()}",
                    TenantId = user.TenantId,
                    CallId = callId,
                    RecordingId = recording.Id,
                    ObjectKey = recording.ObjectKey,
                    Language = recording.Language ?? voiceOptions.Value.DefaultLanguage,
                    Engine = stt.Engine,
                    Model = stt.Model,
                    ModelVersion = stt.ModelVersion,
                    Force = true
                }), null, ct);
        }

        if (scope is ReprocessScopes.Analysis or ReprocessScopes.All)
        {
            var previous = await analyses.GetLatestForEntityAsync(user.TenantId,
                AnalysisEntityTypes.VoiceCall, callId, ct);

            await publisher.PublishAsync(kafka.Value.Topics.AiAnalysisRequest,
                PartitionKeys.ForEntity(callId), CxJson.Serialize(new AiAnalysisRequest
                {
                    JobKey = $"{JobKeys.Analyze(AnalysisEntityTypes.VoiceCall, callId, promptVersion)}:{Ulid.NewUlid()}",
                    TenantId = user.TenantId,
                    EntityType = AnalysisEntityTypes.VoiceCall,
                    EntityId = callId,
                    AnalysisType = AnalysisTypes.VoiceAnalysis,
                    CustomerId = call.CustomerId,
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
            ResourceType = "voice_call",
            ResourceId = callId,
            After = CxJson.Serialize(new { scope, prompt_version = promptVersion }),
            Reason = body?.Reason,
            IpAddress = user.IpAddress,
            OccurredAt = DateTimeOffset.UtcNow
        }, ct);

        return Results.Accepted($"/v1/voice/calls/{callId}", new
        {
            call_id = callId,
            scope,
            status = "queued",
            note = "a new version will be created; the previous result is retained"
        });
    }

    private static async Task<IResult> EditSegmentAsync(HttpContext ctx, Guid transcriptId, int segmentIndex,
        [FromBody] SegmentEditRequest body, IVoiceRepository repository, IAuditRepository audit,
        CancellationToken ct)
    {
        var user = ctx.ToCxUser();
        if (!user.Has(CxPermissions.VoiceTranscriptEdit))
            return Results.Forbid();

        if (string.IsNullOrWhiteSpace(body.Text))
            return Results.BadRequest(new { error = "text is required" });

        var transcript = await repository.GetTranscriptAsync(transcriptId, ct);
        if (transcript is null || transcript.TenantId != user.TenantId)
            return Results.NotFound(new { error = "transcript not found" });

        var segments = await repository.GetSegmentsAsync(transcriptId, ct);
        var before = segments.FirstOrDefault(s => s.SegmentIndex == segmentIndex);
        if (before is null) return Results.NotFound(new { error = "segment not found" });

        await repository.UpdateSegmentTextAsync(transcriptId, segmentIndex, body.Text, ct);

        await audit.WriteAsync(new AuditLog
        {
            TenantId = user.TenantId,
            ActorId = user.Subject,
            Action = AuditActions.TranscriptEdit,
            ResourceType = "voice_transcript_segment",
            ResourceId = $"{transcriptId}:{segmentIndex}",
            Before = CxJson.Serialize(new { text = before.Text }),
            After = CxJson.Serialize(new { text = body.Text }),
            Reason = body.Reason,
            IpAddress = user.IpAddress,
            OccurredAt = DateTimeOffset.UtcNow
        }, ct);

        return Results.Ok(new { transcript_id = transcriptId, segment_index = segmentIndex, text = body.Text });
    }
}

public static class ReprocessScopes
{
    public const string Transcript = "transcript";
    public const string Analysis = "analysis";
    public const string All = "all";
}

public sealed record ReprocessRequest(string? Scope, string? PromptVersion, string? Reason);

public sealed record SegmentEditRequest(string Text, string? Reason);
