using Cx.Application.Ingestion;
using Cx.Application.Options;
using Cx.Contracts.Events;
using Cx.Ingestion.Api;
using Cx.Integrations.Support;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cx.ContractTests;

/// <summary>
/// A contract test per external adapter (spec 32). Each one proves the adapter emits
/// events the ingestion validator accepts - an adapter that produces events the platform
/// rejects is the failure mode these are here to catch.
/// </summary>
public abstract class AdapterContractTestBase
{
    protected static readonly EventValidator Validator = new(Options.Create(new IngestionOptions()));

    protected static void AssertIngestible(IReadOnlyList<CanonicalEvent> events)
    {
        events.Should().NotBeEmpty();

        foreach (var evt in events)
        {
            var node = System.Text.Json.JsonSerializer.SerializeToNode(evt, Cx.Contracts.CxJson.Options);
            var result = Validator.Validate(node);

            result.IsValid.Should().BeTrue(
                $"'{evt.EventName}' must satisfy the canonical schema: {string.Join("; ", result.Errors)}");

            EventNames.IsWellFormed(evt.EventName).Should().BeTrue();
            evt.EventId.Should().NotBeNullOrWhiteSpace("idempotency depends on a stable event id");
            evt.TenantId.Should().NotBeNullOrWhiteSpace();
        }
    }
}

public class SupportAdapterContractTests : AdapterContractTestBase
{
    private readonly SupportTicketAdapter _adapter = new();

    private const string Payload = """
        {
          "ticket_id": "TKT-1001",
          "action": "created",
          "customer_id": "c_1",
          "product_id": "payments",
          "subject": "Port error",
          "description": "Payment is not made",
          "status": "open",
          "priority": "high",
          "created_at": "2026-09-20T10:00:00Z"
        }
        """;

    [Fact]
    public void Produces_ingestible_events()
    {
        AssertIngestible(_adapter.MapToCanonicalEvents(Payload, new Dictionary<string, string>()));
    }

    [Fact]
    public void Maps_the_lifecycle_action_to_the_catalog()
    {
        foreach (var (action, expected) in new[]
                 {
                     ("created", EventNames.SupportTicketCreated),
                     ("assigned", EventNames.SupportTicketAssigned),
                     ("replied", EventNames.SupportTicketReplied),
                     ("escalated", EventNames.SupportTicketEscalated),
                     ("resolved", EventNames.SupportTicketResolved),
                     ("reopened", EventNames.SupportTicketReopened)
                 })
        {
            var events = _adapter.MapToCanonicalEvents(
                Payload.Replace("\"action\": \"created\"", $"\"action\": \"{action}\""),
                new Dictionary<string, string>());

            events.Single().EventName.Should().Be(expected);
        }
    }

    [Fact]
    public void Keeps_the_provider_id_for_idempotency()
    {
        var events = _adapter.MapToCanonicalEvents(Payload, new Dictionary<string, string>());

        events.Single().Metadata["external_id"]!.GetValue<string>().Should().Be("TKT-1001");
    }

    [Fact]
    public void Rejects_a_payload_without_a_ticket_id()
    {
        _adapter.Validate("""{"subject":"no id"}""").Should().NotBeEmpty();
    }

    [Fact]
    public void Reports_malformed_json_instead_of_throwing()
    {
        _adapter.Validate("not json").Should().ContainSingle();
    }
}

public class FeedbackAdapterContractTests : AdapterContractTestBase
{
    private readonly FeedbackAdapter _adapter = new();

    [Theory]
    [InlineData("nps")]
    [InlineData("nps_score")]
    public void Accepts_all_nps_scores_without_a_comment(string key)
    {
        for (var score = 0; score <= 10; score++)
        {
            var payload = $"{{\"feedback_id\":\"nps-{score}\",\"{key}\":{score}}}";
            _adapter.Validate(payload).Should().BeEmpty();
            var events = _adapter.MapToCanonicalEvents(payload, new Dictionary<string, string>());
            events.Single().Metadata["nps_score"]!.GetValue<int>().Should().Be(score);
            events.Single().EventName.Should().Be(EventNames.FeedbackNpsSubmitted);
            AssertIngestible(events);
        }
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("11")]
    [InlineData("6.5")]
    [InlineData("null")]
    [InlineData("\"9\"")]
    [InlineData("true")]
    [InlineData("{}")]
    public void Rejects_invalid_nps_even_with_a_comment(string score)
    {
        foreach (var key in new[] { "nps", "nps_score" })
            _adapter.Validate($"{{\"{key}\":{score},\"comment\":\"test\"}}")
                .Should().NotBeEmpty();
    }

    [Fact]
    public void Rejects_conflicting_nps_aliases()
    {
        _adapter.Validate("""{"nps":0,"nps_score":10}""").Should().NotBeEmpty();
    }

    [Fact]
    public void Produces_ingestible_events()
    {
        AssertIngestible(_adapter.MapToCanonicalEvents(
            """{"feedback_id":"fb-1","customer_id":"c_1","rating":2,"comment":"It is slow"}""",
            new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("""{"feedback_id":"f1","nps":9}""", "feedback.nps.submitted")]
    [InlineData("""{"feedback_id":"f2","csat":4}""", "feedback.csat.submitted")]
    [InlineData("""{"feedback_id":"f3","rating":5}""", "feedback.rating.submitted")]
    [InlineData("""{"feedback_id":"f4","comment":"Well"}""", "feedback.submitted")]
    public void Picks_the_event_name_from_the_score_type(string payload, string expected)
    {
        _adapter.MapToCanonicalEvents(payload, new Dictionary<string, string>())
            .Single().EventName.Should().Be(expected);
    }

    [Fact]
    public void Requires_a_score_or_a_comment()
    {
        _adapter.Validate("""{"feedback_id":"f5"}""").Should().NotBeEmpty();
    }
}

public class VoipAdapterContractTests : AdapterContractTestBase
{
    private readonly VoipWebhookAdapter _adapter = new();

    private const string Payload = """
        {
          "call_id": "call_9",
          "event": "ended",
          "customer_id": "c_1",
          "agent_id": "agent_2",
          "direction": "inbound",
          "duration": 721,
          "end_reason": "completed",
          "recording_url": "https://voip.example/rec/call_9.wav",
          "content_type": "audio/wav",
          "channels": 2,
          "timestamp": "2026-09-20T10:00:00Z"
        }
        """;

    [Fact]
    public void Produces_ingestible_events()
    {
        AssertIngestible(_adapter.MapToCanonicalEvents(Payload, new Dictionary<string, string>()));
    }

    [Fact]
    public void References_the_recording_instead_of_embedding_audio()
    {
        var evt = _adapter.MapToCanonicalEvents(Payload, new Dictionary<string, string>()).Single();

        evt.Metadata["recording_url"]!.GetValue<string>().Should().StartWith("https://");
        // Rule R5: audio bytes never travel through the event pipeline.
        Cx.Contracts.CxJson.Serialize(evt).Length.Should().BeLessThan(4000);
    }

    [Fact]
    public void Normalizes_duration_reported_in_seconds()
    {
        var evt = _adapter.MapToCanonicalEvents(Payload, new Dictionary<string, string>()).Single();

        evt.Voice!.DurationMs.Should().Be(721_000);
    }

    [Fact]
    public void Marks_voice_events_as_restricted_pii()
    {
        var evt = _adapter.MapToCanonicalEvents(Payload, new Dictionary<string, string>()).Single();

        evt.Privacy!.ContainsPii.Should().BeTrue();
        evt.Privacy.Classification.Should().Be("restricted");
    }

    [Theory]
    [InlineData("started", "voice.call.started")]
    [InlineData("answered", "voice.call.answered")]
    [InlineData("ended", "voice.call.ended")]
    [InlineData("no_answer", "voice.call.dropped")]
    [InlineData("recording_ready", "voice.recording.ready")]
    public void Maps_provider_call_states(string action, string expected)
    {
        var events = _adapter.MapToCanonicalEvents(
            Payload.Replace("\"event\": \"ended\"", $"\"event\": \"{action}\""),
            new Dictionary<string, string>());

        events.Single().EventName.Should().Be(expected);
    }

    [Fact]
    public void Requires_a_call_id()
    {
        _adapter.Validate("""{"event":"ended"}""").Should().NotBeEmpty();
    }
}
