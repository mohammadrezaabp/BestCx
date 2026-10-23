using System.Text.Json;
using Cx.Application.Abstractions;
using Cx.Application.Options;
using Cx.Application.Privacy;
using Cx.Contracts;
using Cx.Contracts.Ai;
using Cx.Contracts.Events;
using Cx.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Cx.Application.Ai;

public interface IContextBuilder
{
    Task<AnalysisContext> BuildEventContextAsync(AiAnalysisRequest request, CancellationToken ct = default);
    Task<AnalysisContext> BuildVoiceContextAsync(AiAnalysisRequest request, CancellationToken ct = default);
}

/// <summary>
/// Assembles the model input (spec 20 / technical design 12.3).
/// AI never sees a lone event: customer history, open cases and recent feedback come with it.
/// </summary>
public sealed class ContextBuilder : IContextBuilder
{
    private readonly IEventRepository _events;
    private readonly ISupportCaseRepository _supportCases;
    private readonly IFeedbackRepository _feedback;
    private readonly IVoiceRepository _voice;
    private readonly ICustomerRepository _customers;
    private readonly IRedactionService _redaction;
    private readonly AiOptions _ai;

    public ContextBuilder(
        IEventRepository events,
        ISupportCaseRepository supportCases,
        IFeedbackRepository feedback,
        IVoiceRepository voice,
        ICustomerRepository customers,
        IRedactionService redaction,
        IOptions<AiOptions> ai)
    {
        _events = events;
        _supportCases = supportCases;
        _feedback = feedback;
        _voice = voice;
        _customers = customers;
        _redaction = redaction;
        _ai = ai.Value;
    }

    public async Task<AnalysisContext> BuildEventContextAsync(AiAnalysisRequest request, CancellationToken ct = default)
    {
        var context = NewContext(request);
        var current = await _events.GetByEventIdAsync(request.EntityId, ct);
        if (current is not null)
        {
            context.CurrentEvent = ToContextEvent(current, includeText: true);
            context.CustomerId ??= current.CustomerId;
        }

        await AddCustomerHistoryAsync(context, current?.OccurredAt, ct);
        Truncate(context);
        return context;
    }

    public async Task<AnalysisContext> BuildVoiceContextAsync(AiAnalysisRequest request, CancellationToken ct = default)
    {
        var context = NewContext(request);
        context.AnalysisType = AnalysisTypes.VoiceAnalysis;

        var call = await _voice.GetCallAsync(request.TenantId, request.EntityId, ct);
        if (call is null)
            throw new InvalidOperationException($"voice call '{request.EntityId}' not found");

        context.CustomerId ??= call.CustomerId;
        context.Call = new ContextCall
        {
            CallId = call.CallId,
            Direction = call.Direction,
            AgentId = call.AgentId,
            QueueId = call.QueueId,
            DurationMs = call.DurationMs,
            EndReason = call.EndReason,
            StartedAt = call.StartedAt
        };

        var recordings = await _voice.GetRecordingsForCallAsync(call.Id, ct);
        var ready = recordings.FirstOrDefault(r => RecordingStorageStates.IsAnalyzable(r.StorageState));
        if (ready is not null)
        {
            var transcript = await _voice.GetLatestTranscriptAsync(ready.Id, ct);
            if (transcript is not null)
            {
                // Prefer the stored redacted copy; re-redact defensively if it is missing so a
                // configuration gap can never leak raw PII to the provider.
                var text = transcript.RedactedText ?? _redaction.Redact(transcript.TranscriptText).RedactedText;
                context.Transcript = Clip(text, _ai.Context.MaxTranscriptChars, out var clipped);
                context.Truncated |= clipped;

                var segments = await _voice.GetSegmentsAsync(transcript.Id, ct);
                context.TranscriptSegments = segments
                    .OrderBy(s => s.SegmentIndex)
                    .Select(s => new ContextTranscriptSegment
                    {
                        Speaker = s.Speaker ?? Cx.Contracts.Voice.SpeakerLabels.Unknown,
                        StartMs = s.StartMs,
                        EndMs = s.EndMs,
                        Text = s.RedactedText ?? _redaction.Redact(s.Text).RedactedText
                    })
                    .ToList();
            }
        }

        await AddCustomerHistoryAsync(context, call.StartedAt, ct);
        Truncate(context);
        return context;
    }

    private AnalysisContext NewContext(AiAnalysisRequest request) => new()
    {
        TenantId = request.TenantId,
        AnalysisType = request.AnalysisType,
        EntityType = request.EntityType,
        EntityId = request.EntityId,
        CustomerId = request.CustomerId
    };

    private async Task AddCustomerHistoryAsync(AnalysisContext context, DateTimeOffset? anchor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.CustomerId)) return;

        var since = (anchor ?? DateTimeOffset.UtcNow) - _ai.Context.LookbackWindow;
        var history = await _events.GetRecentForCustomerAsync(
            context.TenantId, context.CustomerId, _ai.Context.PreviousEventCount, since, ct);

        context.PreviousEvents = history
            .Where(e => context.CurrentEvent is null || e.EventId != context.CurrentEvent.EventId)
            .OrderBy(e => e.OccurredAt)
            .Select(e => ToContextEvent(e, includeText: false))
            .ToList();

        var identity = await _customers.GetAsync(context.TenantId, context.CustomerId, ct);
        context.CustomerSegment = identity?.Segment;

        if (_ai.Context.IncludeOpenSupportCase)
        {
            var cases = await _supportCases.GetOpenForCustomerAsync(context.TenantId, context.CustomerId, 3, ct);
            context.OpenSupportCases = cases.Select(c => new ContextSupportCase
            {
                ExternalId = c.ExternalId,
                Subject = Redact(c.Subject),
                Description = Clip(Redact(c.Description), 2000, out _),
                Status = c.Status,
                Priority = c.Priority,
                OpenedAt = c.OpenedAt
            }).ToList();
        }

        if (_ai.Context.IncludeLatestFeedback)
        {
            var feedback = await _feedback.GetRecentForCustomerAsync(context.TenantId, context.CustomerId, 3, ct);
            context.LatestFeedback = feedback.Select(f => new ContextFeedback
            {
                Type = f.FeedbackType,
                Rating = f.Rating ?? f.CsatScore ?? f.NpsScore,
                Comment = Clip(Redact(f.Comment), 1000, out _),
                SubmittedAt = f.SubmittedAt
            }).ToList();
        }
    }

    private void Truncate(AnalysisContext context)
    {
        var size = CxJson.Serialize(context).Length;
        while (size > _ai.Context.MaxContextChars && context.PreviousEvents.Count > 0)
        {
            // Drop oldest history first: the current interaction is the part that must survive.
            context.PreviousEvents.RemoveAt(0);
            context.Truncated = true;
            size = CxJson.Serialize(context).Length;
        }
    }

    private ContextEvent ToContextEvent(CxEvent e, bool includeText)
    {
        var ctx = new ContextEvent
        {
            EventId = e.EventId,
            EventName = e.EventName,
            OccurredAt = e.OccurredAt,
            ProductId = e.ProductId
        };

        try
        {
            var envelope = JsonSerializer.Deserialize<CanonicalEvent>(e.Payload, CxJson.Options);
            ctx.Result = envelope?.Interaction?.Result;
            ctx.ErrorCode = envelope?.Interaction?.ErrorCode;
            ctx.Channel = envelope?.Source?.Channel;

            if (envelope?.Metadata is { Count: > 0 } metadata)
            {
                if (includeText)
                {
                    // The interaction being analysed is sent whole: the customer's words are
                    // the evidence, and clipping them inside a metadata blob lost most of it.
                    ctx.Text = Clip(Redact(CustomerText(metadata)), MaxCurrentTextChars, out _);
                    ctx.Rating = MetadataNumber(metadata, "rating") ?? MetadataNumber(metadata, "csat_score")
                                 ?? MetadataNumber(metadata, "nps_score");
                }

                // History only needs a short, redacted projection.
                ctx.Detail = Clip(Redact(MetadataDetail(envelope!)), 400, out _);
            }
        }
        catch (JsonException)
        {
            // A malformed stored payload must not take down analysis of the rest of the timeline.
            ctx.Detail = null;
        }

        return ctx;
    }

    private const int MaxCurrentTextChars = 12_000;

    internal static string MetadataDetail(CanonicalEvent envelope)
    {
        var metadata = envelope.Metadata;
        if (envelope.Source?.Service == "cx-customer-portal")
        {
            // Contact details exist for staff follow-up, not model input. The original event stays immutable.
            metadata = metadata.DeepClone().AsObject();
            metadata.Remove("contact_name");
            metadata.Remove("contact");
        }
        return metadata.ToJsonString(CxJson.Options);
    }

    /// <summary>Free text a customer or agent wrote, across the adapters' field names.</summary>
    private static string? CustomerText(System.Text.Json.Nodes.JsonObject metadata)
    {
        var parts = new[] { "subject", "comment", "description", "message", "text" }
            .Select(key => metadata.TryGetPropertyValue(key, out var node) && node is System.Text.Json.Nodes.JsonValue v &&
                           v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null)
            .Where(s => s is not null)
            .Distinct()
            .ToList();
        return parts.Count == 0 ? null : string.Join('\n', parts);
    }

    private static int? MetadataNumber(System.Text.Json.Nodes.JsonObject metadata, string key)
    {
        if (!metadata.TryGetPropertyValue(key, out var node) || node is not System.Text.Json.Nodes.JsonValue value)
            return null;
        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : null;
    }

    private string? Redact(string? text) => text is null ? null : _redaction.Redact(text).RedactedText;

    private static string? Clip(string? text, int max, out bool clipped)
    {
        clipped = false;
        if (text is null || text.Length <= max) return text;
        clipped = true;
        return text[..max] + "…";
    }
}
