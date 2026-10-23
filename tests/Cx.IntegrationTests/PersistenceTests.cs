using Cx.Application.Abstractions;
using Cx.Contracts;
using Cx.Contracts.Events;
using Cx.Contracts.Messaging;
using Cx.Domain.Entities;
using Cx.Infrastructure.Database.Repositories;
using Cx.Persistence.Worker;
using FluentAssertions;
using Xunit;

namespace Cx.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class PersistenceTests
{
    private readonly PostgresFixture _fixture;

    public PersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    private EventRepository Events => new(_fixture.DataSource);
    private OutboxRepository Outbox => new(_fixture.DataSource);

    [SkippableFact]
    public async Task Customer_insights_reads_latest_timestamp_without_scalar_cast_errors()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        var customerId = $"insights-{Guid.NewGuid():N}";
        var latest = new DateTimeOffset(2026, 9, 21, 10, 30, 0, TimeSpan.Zero);
        foreach (var occurredAt in new[] { latest.AddHours(-2), latest })
        {
            var id = $"evt-{Guid.NewGuid():N}";
            var evt = NewEvent(id, customerId);
            evt.OccurredAt = occurredAt;
            await Events.PersistWithOutboxAsync(evt, OutboxFor(id));
        }

        var repository = new InsightsRepository(_fixture.DataSource);
        var insights = await repository.GetCustomerInsightsAsync("bestcx", customerId);
        insights.LastInteractionAt.Should().Be(latest);
        (await repository.GetCustomerInsightsAsync("another-tenant", customerId))
            .LastInteractionAt.Should().BeNull();
    }

    [SkippableFact]
    public async Task Customer_insights_returns_null_timestamp_when_no_events_exist()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        var repository = new InsightsRepository(_fixture.DataSource);
        var insights = await repository.GetCustomerInsightsAsync("bestcx", $"missing-{Guid.NewGuid():N}");
        insights.LastInteractionAt.Should().BeNull();
    }

    private static CxEvent NewEvent(string eventId, string customerId = "c_int_1") => new()
    {
        EventId = eventId,
        TenantId = "bestcx",
        CustomerId = customerId,
        EventName = EventNames.ApiRequestFailed,
        EventCategory = EventCategories.Api,
        SchemaVersion = "1.0",
        OccurredAt = DateTimeOffset.UtcNow,
        ReceivedAt = DateTimeOffset.UtcNow,
        SourceType = "backend",
        SourceService = "payment-service",
        ProductId = "payments",
        CorrelationId = "corr-int-1",
        Payload = """{"event_id":"x","event_name":"api.request.failed"}"""
    };

    private static Func<long, OutboxEvent> OutboxFor(string eventId) => recordId => new OutboxEvent
    {
        Id = DeterministicGuid.From("persisted:" + eventId),
        AggregateType = "cx_event",
        AggregateId = eventId,
        EventType = EventNames.ApiRequestFailed,
        Topic = CxTopics.PersistedEvents,
        PartitionKey = "bestcx:c_int_1",
        Payload = CxJson.Serialize(new PersistedEventMessage { EventId = eventId, SourceRecordId = recordId })
    };

    [SkippableFact]
    public async Task Persists_event_and_outbox_in_one_transaction()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);

        var eventId = $"evt-{Guid.NewGuid():N}";

        var result = await Events.PersistWithOutboxAsync(NewEvent(eventId), OutboxFor(eventId));

        result.WasDuplicate.Should().BeFalse();
        result.RecordId.Should().BeGreaterThan(0);

        (await Events.GetByEventIdAsync(eventId)).Should().NotBeNull();

        var pending = await Outbox.FetchUnpublishedAsync(100);
        pending.Should().Contain(o => o.AggregateId == eventId);
    }

    [SkippableFact]
    public async Task Duplicate_delivery_does_not_duplicate_business_state()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);

        var eventId = $"evt-{Guid.NewGuid():N}";

        var first = await Events.PersistWithOutboxAsync(NewEvent(eventId), OutboxFor(eventId));
        var second = await Events.PersistWithOutboxAsync(NewEvent(eventId), OutboxFor(eventId));
        var third = await Events.PersistWithOutboxAsync(NewEvent(eventId), OutboxFor(eventId));

        first.WasDuplicate.Should().BeFalse();
        second.WasDuplicate.Should().BeTrue();
        third.WasDuplicate.Should().BeTrue();

        // Same row, and critically only ONE outbox publication.
        second.RecordId.Should().Be(first.RecordId);

        var outboxRows = (await Outbox.FetchUnpublishedAsync(500)).Count(o => o.AggregateId == eventId);
        outboxRows.Should().BeLessThanOrEqualTo(1);
    }

    [SkippableFact]
    public async Task Outbox_rows_are_published_once()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);

        var eventId = $"evt-{Guid.NewGuid():N}";
        await Events.PersistWithOutboxAsync(NewEvent(eventId), OutboxFor(eventId));

        var batch = await Outbox.FetchUnpublishedAsync(500);
        var row = batch.Single(o => o.AggregateId == eventId);

        await Outbox.MarkPublishedAsync(new[] { row.Id });

        var afterPublish = await Outbox.FetchUnpublishedAsync(500);
        afterPublish.Should().NotContain(o => o.AggregateId == eventId);
    }

    [SkippableFact]
    public async Task Customer_timeline_query_is_ordered_and_pageable()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);

        var customerId = $"c-{Guid.NewGuid():N}";
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-30);

        for (var i = 0; i < 5; i++)
        {
            var id = $"evt-{Guid.NewGuid():N}";
            var evt = NewEvent(id, customerId);
            evt.OccurredAt = baseTime.AddMinutes(i);
            await Events.PersistWithOutboxAsync(evt, OutboxFor(id));
        }

        var page = await Events.QueryAsync(new EventQuery { CustomerId = customerId, Limit = 3 });

        page.Should().HaveCount(4, "the repository fetches limit+1 to detect another page");
        page.Select(e => e.OccurredAt).Should().BeInDescendingOrder();

        var recent = await Events.GetRecentForCustomerAsync("bestcx", customerId, 2);
        recent.Should().HaveCount(2);
    }

    [SkippableFact]
    public async Task Processing_job_key_is_claimed_once()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);

        var jobs = new ProcessingJobRepository(_fixture.DataSource);
        var jobKey = $"rec-{Guid.NewGuid():N}:transcribe:stt-v4";

        (await jobs.TryClaimAsync("bestcx", jobKey, ProcessingJobTypes.Transcribe)).Should().BeTrue();
        (await jobs.TryClaimAsync("bestcx", jobKey, ProcessingJobTypes.Transcribe)).Should().BeFalse();

        await jobs.CompleteAsync(jobKey, "result-1");

        // A completed job stays completed: a redelivery must not redo the work...
        (await jobs.TryClaimAsync("bestcx", jobKey, ProcessingJobTypes.Transcribe)).Should().BeFalse();
        // ...but an explicit reprocess may force it.
        (await jobs.TryClaimAsync("bestcx", jobKey, ProcessingJobTypes.Transcribe, force: true)).Should().BeTrue();
    }

    [SkippableFact]
    public async Task A_retryable_failure_can_be_claimed_again()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);

        var jobs = new ProcessingJobRepository(_fixture.DataSource);
        var jobKey = $"rec-{Guid.NewGuid():N}:analyze:v1";

        await jobs.TryClaimAsync("bestcx", jobKey, ProcessingJobTypes.Analyze);
        await jobs.FailAsync(jobKey, "provider timeout", Cx.Application.Common.ErrorCategory.Provider5xx, true);

        (await jobs.TryClaimAsync("bestcx", jobKey, ProcessingJobTypes.Analyze)).Should().BeTrue();

        await jobs.FailAsync(jobKey, "bad request", Cx.Application.Common.ErrorCategory.Validation, false);

        // A final failure is terminal; it needs an operator, not another automatic retry.
        (await jobs.TryClaimAsync("bestcx", jobKey, ProcessingJobTypes.Analyze)).Should().BeFalse();
    }
}
