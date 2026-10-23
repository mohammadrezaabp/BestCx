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

namespace Cx.Integrations.Support;

/// <summary>
/// Projects persisted feedback and support events into the tables the Unified Inbox reads
/// (panel proposal 6). Consumes the persisted topic, so the inbox can never show a row for
/// an event that was rolled back.
/// </summary>
public sealed class InboxProjectionWorker : CxConsumerService
{
    public const string ConsumerGroup = "cx-inbox-projection";

    private readonly KafkaOptions _kafka;

    public InboxProjectionWorker(
        IServiceScopeFactory scopeFactory,
        IMessageSubscriber subscriber,
        IOptions<KafkaOptions> kafka,
        ILogger<InboxProjectionWorker> logger)
        : base(scopeFactory, subscriber, logger)
        => _kafka = kafka.Value;

    protected override SubscriptionOptions Subscription => new()
    {
        Topics = new[] { _kafka.Topics.PersistedEvents },
        GroupId = ConsumerGroup,
        DeadLetterTopic = _kafka.Topics.PersistenceDlq,
        MaxAttempts = 4
    };

    protected override async Task HandleAsync(BusMessage message, IServiceProvider services, CancellationToken ct)
    {
        PersistedEventMessage? persisted;
        try
        {
            persisted = CxJson.Deserialize<PersistedEventMessage>(message.Value);
        }
        catch (JsonException ex)
        {
            throw new CxException(ErrorCategory.Schema, "malformed persisted event", ex);
        }

        if (persisted?.Envelope is null) return;

        var envelope = persisted.Envelope.Deserialize<CanonicalEvent>(CxJson.Options);
        if (envelope is null) return;

        switch (envelope.EventCategory)
        {
            case EventCategories.Feedback:
                await ProjectFeedbackAsync(services, envelope, ct);
                break;
            case EventCategories.Support:
                await ProjectSupportAsync(services, envelope, ct);
                break;
        }
    }

    private static async Task ProjectFeedbackAsync(IServiceProvider services, CanonicalEvent envelope,
        CancellationToken ct)
    {
        var repository = services.GetRequiredService<IFeedbackRepository>();
        var metadata = envelope.Metadata;

        var feedbackType = envelope.EventName switch
        {
            EventNames.FeedbackNpsSubmitted => FeedbackTypes.Nps,
            EventNames.FeedbackCsatSubmitted => FeedbackTypes.Csat,
            EventNames.FeedbackRatingSubmitted => FeedbackTypes.Rating,
            _ => FeedbackTypes.Generic
        };
        if (envelope.Source.Service is "cx-customer-portal" or "cx-console")
            feedbackType = Text(metadata, "submission_kind") switch
            {
                "complaint" => FeedbackTypes.Complaint,
                "voc" => FeedbackTypes.VoiceOfCustomer,
                _ => feedbackType
            };

        await repository.UpsertAsync(new Feedback
        {
            TenantId = envelope.TenantId,
            EventId = envelope.EventId,
            Provider = envelope.Source.Service,
            // The provider's own id keeps a replayed webhook from creating a duplicate row.
            ExternalId = Text(metadata, "external_id") ?? envelope.EventId,
            CustomerId = envelope.Customer?.CustomerId,
            ProductId = envelope.Product?.ProductId,
            FeedbackType = feedbackType,
            Rating = Number(metadata, "rating"),
            NpsScore = Number(metadata, "nps_score"),
            CsatScore = Number(metadata, "csat_score"),
            CesScore = Number(metadata, "ces_score"),
            SelectedTopic = Text(metadata, "selected_category"),
            Comment = Text(metadata, "comment"),
            Channel = envelope.Source.Channel,
            Status = InboxStatuses.New,
            SubmittedAt = envelope.OccurredAt
        }, ct);
    }

    private static async Task ProjectSupportAsync(IServiceProvider services, CanonicalEvent envelope,
        CancellationToken ct)
    {
        var repository = services.GetRequiredService<ISupportCaseRepository>();
        var metadata = envelope.Metadata;
        var externalId = Text(metadata, "external_id") ?? envelope.Context?.CorrelationId;

        if (externalId is null) return;

        var resolved = envelope.EventName == EventNames.SupportTicketResolved;
        var reopened = envelope.EventName == EventNames.SupportTicketReopened;

        await repository.UpsertAsync(new SupportCase
        {
            TenantId = envelope.TenantId,
            Provider = envelope.Source.Service,
            ExternalId = externalId,
            CustomerId = envelope.Customer?.CustomerId,
            ProductId = envelope.Product?.ProductId,
            Subject = Text(metadata, "subject"),
            Description = Text(metadata, "description"),
            Status = Text(metadata, "status") ?? (resolved ? "resolved" : reopened ? "open" : "open"),
            Priority = Text(metadata, "priority"),
            Channel = envelope.Source.Channel,
            AssigneeId = Text(metadata, "assignee_id"),
            Escalated = envelope.EventName == EventNames.SupportTicketEscalated,
            CorrelationId = externalId,
            OpenedAt = envelope.OccurredAt,
            FirstResponseAt = envelope.EventName == EventNames.SupportTicketReplied ? envelope.OccurredAt : null,
            // Reopening must clear the resolution, otherwise the case looks closed forever.
            ResolvedAt = resolved ? envelope.OccurredAt : null
        }, ct);
    }

    private static string? Text(JsonObject metadata, string key) =>
        metadata.TryGetPropertyValue(key, out var node) && node is JsonValue value &&
        value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static int? Number(JsonObject metadata, string key)
    {
        if (!metadata.TryGetPropertyValue(key, out var node) || node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed)) return parsed;
        return null;
    }
}
