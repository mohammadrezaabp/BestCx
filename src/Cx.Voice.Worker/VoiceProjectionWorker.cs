using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Options;
using Cx.Contracts;
using Cx.Contracts.Events;
using Cx.Contracts.Messaging;
using Cx.Domain.Entities;
using Cx.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Voice.Worker;

/// <summary>
/// Projects persisted voice events into the voice_call model and starts recording ingest.
/// It consumes cx.persisted.events.v1, never the raw topic: a call row is only ever built
/// from a fact that is already committed (rule R2).
/// </summary>
public sealed class VoiceProjectionWorker : CxConsumerService
{
    public const string ConsumerGroup = "cx-voice-projection";

    private readonly KafkaOptions _kafka;

    public VoiceProjectionWorker(
        IServiceScopeFactory scopeFactory,
        IMessageSubscriber subscriber,
        IOptions<KafkaOptions> kafka,
        ILogger<VoiceProjectionWorker> logger)
        : base(scopeFactory, subscriber, logger)
        => _kafka = kafka.Value;

    protected override SubscriptionOptions Subscription => new()
    {
        Topics = new[] { _kafka.Topics.PersistedEvents },
        GroupId = ConsumerGroup,
        DeadLetterTopic = _kafka.Topics.VoiceDlq,
        MaxAttempts = 4
    };

    protected override async Task HandleAsync(BusMessage message, IServiceProvider services, CancellationToken ct)
    {
        var persisted = Deserialize(message.Value);
        if (persisted?.Envelope is null) return;
        if (persisted.EventCategory != EventCategories.Voice) return;

        var envelope = persisted.Envelope.Deserialize<CanonicalEvent>(CxJson.Options);
        if (envelope?.Voice?.CallId is null) return;

        var voice = services.GetRequiredService<IVoiceRepository>();
        var call = await UpsertCallAsync(voice, envelope, ct);

        switch (envelope.EventName)
        {
            case EventNames.VoiceCallStarted:
                await voice.AdvanceCallStateAsync(call.Id, CallLifecycleStates.Ringing, ct);
                break;

            case EventNames.VoiceCallAnswered:
                await voice.AdvanceCallStateAsync(call.Id, CallLifecycleStates.Answered, ct);
                break;

            case EventNames.VoiceCallEnded:
            case EventNames.VoiceCallDropped:
                await voice.AdvanceCallStateAsync(call.Id, CallLifecycleStates.Ended, ct);
                await MaybeIngestRecordingAsync(services, envelope, ct);
                break;

            case EventNames.VoiceRecordingReady:
                // Emitted by this worker after storage succeeded, or by a provider adapter
                // that already staged the object itself.
                await MaybeIngestRecordingAsync(services, envelope, ct);
                break;
        }
    }

    private static async Task<VoiceCall> UpsertCallAsync(IVoiceRepository voice, CanonicalEvent envelope,
        CancellationToken ct)
    {
        var v = envelope.Voice!;
        var call = new VoiceCall
        {
            TenantId = envelope.TenantId,
            CallId = v.CallId!,
            CustomerId = envelope.Customer?.CustomerId,
            AgentId = v.AgentId,
            QueueId = v.QueueId,
            Direction = v.Direction,
            DurationMs = v.DurationMs,
            EndReason = v.EndReason,
            Provider = v.Provider,
            ProviderCallId = v.ProviderCallId,
            CorrelationId = envelope.Context?.CorrelationId,
            ProductId = envelope.Product?.ProductId,
            LifecycleState = CallLifecycleStates.Discovered
        };

        // Each event contributes the timestamp it actually knows about.
        switch (envelope.EventName)
        {
            case EventNames.VoiceCallStarted:
                call.StartedAt = envelope.OccurredAt;
                break;
            case EventNames.VoiceCallAnswered:
                call.AnsweredAt = envelope.OccurredAt;
                break;
            case EventNames.VoiceCallEnded:
            case EventNames.VoiceCallDropped:
                call.EndedAt = envelope.OccurredAt;
                if (v.DurationMs is > 0)
                    call.StartedAt = envelope.OccurredAt.AddMilliseconds(-v.DurationMs.Value);
                break;
        }

        return await voice.UpsertCallAsync(call, ct);
    }

    /// <summary>
    /// A recording is ingested only when the event actually points at one. The descriptor
    /// travels in event metadata so no provider-specific route is assumed here.
    /// </summary>
    private async Task MaybeIngestRecordingAsync(IServiceProvider services, CanonicalEvent envelope,
        CancellationToken ct)
    {
        var metadata = envelope.Metadata;
        var recordingId = envelope.Voice?.RecordingId ?? Text(metadata, "recording_id");
        var sourceUri = Text(metadata, "recording_url") ?? Text(metadata, "source_uri");

        // A fetch needs somewhere to fetch from. A recording_id on its own announces that
        // audio exists, not where it lives: the provider will follow up with a URL, or the
        // recording arrives through the direct-upload endpoint. Attempting a pull here
        // would fail the ingest job and block the upload that is actually coming.
        if (sourceUri is null) return;

        // Already stored by our own ingest; the ready event is an announcement, not work.
        if (Text(metadata, "object_key") is not null) return;

        var ingest = services.GetRequiredService<RecordingIngestService>();

        await ingest.IngestAsync(new RecordingIngestRequest(
            envelope.TenantId,
            envelope.Voice!.CallId!,
            recordingId ?? envelope.Voice.CallId!,
            sourceUri,
            Text(metadata, "content_type") ?? "audio/wav",
            Number(metadata, "duration_ms") ?? envelope.Voice.DurationMs,
            (int?)Number(metadata, "channels"),
            (int?)Number(metadata, "sample_rate"),
            Text(metadata, "codec"),
            Text(metadata, "language"),
            Number(metadata, "size_bytes")), ct);
    }

    private static PersistedEventMessage? Deserialize(string value)
    {
        try
        {
            return CxJson.Deserialize<PersistedEventMessage>(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject metadata, string key) =>
        metadata.TryGetPropertyValue(key, out var node) && node is JsonValue value &&
        value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static long? Number(JsonObject metadata, string key)
    {
        if (!metadata.TryGetPropertyValue(key, out var node) || node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var number)) return number;
        if (value.TryGetValue<string>(out var text) && long.TryParse(text, out var parsed)) return parsed;
        return null;
    }
}
