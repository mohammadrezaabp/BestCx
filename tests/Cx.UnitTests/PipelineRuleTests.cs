using Cx.Application.Ingestion;
using Cx.Application.Jobs;
using Cx.Application.Options;
using Cx.Application.Privacy;
using Cx.Application.Voice;
using Cx.Contracts.Events;
using Cx.Domain.Entities;
using Cx.Persistence.Worker;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cx.UnitTests;

public class VoiceStateMachineTests
{
    [Fact]
    public void Only_ready_recordings_are_analyzable()
    {
        RecordingStorageStates.IsAnalyzable(RecordingStorageStates.Ready).Should().BeTrue();

        foreach (var state in new[]
                 {
                     RecordingStorageStates.Discovered, RecordingStorageStates.Staging,
                     RecordingStorageStates.Stored, RecordingStorageStates.Verified,
                     RecordingStorageStates.Failed
                 })
        {
            RecordingStorageStates.IsAnalyzable(state).Should().BeFalse($"{state} is not verified audio");
        }
    }

    [Theory]
    [InlineData("DISCOVERED", "STAGING", true)]
    [InlineData("STAGING", "STORED", true)]
    [InlineData("STORED", "VERIFIED", true)]
    [InlineData("VERIFIED", "READY", true)]
    [InlineData("STAGING", "STAGING", true)]   // redelivery re-asserts the same state
    [InlineData("READY", "STORED", false)]     // never moves backwards
    [InlineData("READY", "FAILED", false)]     // verified audio cannot be un-verified
    [InlineData("FAILED", "STAGING", true)]    // a failed ingest may be retried
    public void Recording_transitions_follow_the_state_machine(string from, string to, bool allowed)
    {
        RecordingStorageStates.CanTransition(from, to).Should().Be(allowed);
    }

    [Fact]
    public void Call_lifecycle_never_moves_backwards()
    {
        CallLifecycleStates.CanTransition(CallLifecycleStates.Ended, CallLifecycleStates.Transcribed)
            .Should().BeTrue();
        CallLifecycleStates.CanTransition(CallLifecycleStates.Analyzed, CallLifecycleStates.Ringing)
            .Should().BeFalse();
        // Out-of-order delivery re-asserting the current state must be harmless.
        CallLifecycleStates.CanTransition(CallLifecycleStates.Answered, CallLifecycleStates.Answered)
            .Should().BeTrue();
    }
}

public class RedactionTests
{
    private static readonly RedactionService Service = new(Options.Create(new RedactionOptions()));

    [Theory]
    [InlineData("My number is 09121234567", "09121234567")]
    [InlineData("Contact with 09121234567 Take", "09121234567")]
    [InlineData("Email: ali@example.com", "ali@example.com")]
    [InlineData("Card 6037 9912 3456 7890", "6037 9912 3456 7890")]
    [InlineData("Authorization: Bearer abcdef1234567890", "abcdef1234567890")]
    public void Masks_pii_and_credentials(string input, string secret)
    {
        var outcome = Service.Redact(input);

        // English-digit input is left alone by the ASCII rules, so only assert on what matched.
        if (input.Contains(secret, StringComparison.Ordinal))
        {
            outcome.RedactedText.Should().NotContain(secret);
            outcome.ContainsPii.Should().BeTrue();
        }
    }

    [Fact]
    public void Leaves_ordinary_text_untouched()
    {
        const string text = "The customer complains about the slowness of the payment service.";

        var outcome = Service.Redact(text);

        outcome.RedactedText.Should().Be(text);
        outcome.ContainsPii.Should().BeFalse();
    }

    [Fact]
    public void Records_what_was_masked_so_the_map_stays_auditable()
    {
        var outcome = Service.Redact("Call 09121234567 and email a@b.co");

        outcome.Hits.Should().HaveCountGreaterThanOrEqualTo(2);
        outcome.Hits.Select(h => h.RuleName).Should().Contain("iran_mobile");
    }

    [Fact]
    public void Overlapping_matches_do_not_corrupt_the_output()
    {
        // A 16-digit run matches both card_number and long_digit_run.
        var outcome = Service.Redact("6037991234567890");

        outcome.RedactedText.Should().NotContain("6037991234567890");
        outcome.RedactedText.Should().StartWith("[REDACTED:");
    }

    [Fact]
    public void Disabled_policy_is_a_no_op()
    {
        var service = new RedactionService(Options.Create(new RedactionOptions { Enabled = false }));

        service.Redact("09121234567").RedactedText.Should().Be("09121234567");
    }
}

public class ObjectKeyTests
{
    [Fact]
    public void Builds_the_documented_path_shape()
    {
        var key = VoiceObjectKey.Build("cx", "bestcx", new DateTimeOffset(2026, 9, 20, 15, 11, 19, TimeSpan.Zero),
            "call_123", "rec_456", "audio/wav");

        key.Should().Be("cx/bestcx/2026/09/20/voice/call_123/rec_456.wav");
    }

    [Fact]
    public void Is_deterministic_so_a_retry_targets_the_same_object()
    {
        var at = DateTimeOffset.UtcNow;

        VoiceObjectKey.Build("cx", "bestcx", at, "call_1", "rec_1", "audio/mpeg")
            .Should().Be(VoiceObjectKey.Build("cx", "bestcx", at, "call_1", "rec_1", "audio/mpeg"));
    }

    [Fact]
    public void Sanitizes_path_traversal_and_separators()
    {
        var key = VoiceObjectKey.Build("cx", "bestcx", DateTimeOffset.UtcNow, "../../etc/passwd", "rec", "audio/wav");

        key.Should().NotContain("..");
        key.Should().NotContain("/etc/");
    }

    [Theory]
    [InlineData("audio/wav", null, "wav")]
    [InlineData("audio/mpeg", null, "mp3")]
    [InlineData(null, "https://provider/rec/file.ogg?token=x", "ogg")]
    [InlineData(null, null, "bin")]
    public void Resolves_the_file_extension(string? contentType, string? sourceUri, string expected)
    {
        VoiceObjectKey.ResolveExtension(contentType, sourceUri).Should().Be(expected);
    }
}

public class JobKeyTests
{
    [Fact]
    public void Transcription_key_includes_the_model_version()
    {
        var recordingId = Guid.NewGuid();

        JobKeys.Transcribe(recordingId, "stt-v4").Should().Be($"{recordingId}:transcribe:stt-v4");
        // A new model version is legitimately new work, not a duplicate.
        JobKeys.Transcribe(recordingId, "stt-v5")
            .Should().NotBe(JobKeys.Transcribe(recordingId, "stt-v4"));
    }

    [Fact]
    public void Analysis_key_includes_the_prompt_version()
    {
        JobKeys.Analyze("voice_call", "call_1", "cx-prompt-v7")
            .Should().Be("voice_call:call_1:analyze:cx-prompt-v7");
    }
}

public class DeterministicGuidTests
{
    [Fact]
    public void Same_name_yields_the_same_id()
    {
        DeterministicGuid.From("persisted:evt-1").Should().Be(DeterministicGuid.From("persisted:evt-1"));
    }

    [Fact]
    public void Different_names_yield_different_ids()
    {
        DeterministicGuid.From("persisted:evt-1").Should().NotBe(DeterministicGuid.From("persisted:evt-2"));
    }

    [Fact]
    public void Produces_a_version_5_uuid()
    {
        var id = DeterministicGuid.From("persisted:evt-1").ToString();

        // Version nibble sits at position 14 of the canonical form.
        id[14].Should().Be('5');
    }
}

public class NormalizationTests
{
    private static readonly EventNormalizer Normalizer = new(Options.Create(new IngestionOptions()));

    private static CanonicalEvent Event() => new()
    {
        EventName = "page.viewed",
        OccurredAt = DateTimeOffset.UtcNow,
        Source = new SourceRef { Type = "web", Service = "portal" }
    };

    [Fact]
    public void Keeps_a_producer_supplied_event_id()
    {
        var evt = Event();
        evt.EventId = "producer-supplied";

        Normalizer.Normalize(evt, DateTimeOffset.UtcNow).EventId.Should().Be("producer-supplied");
    }

    [Fact]
    public void Mints_an_event_id_only_when_absent()
    {
        Normalizer.Normalize(Event(), DateTimeOffset.UtcNow).EventId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Derives_the_category_from_the_event_name()
    {
        Normalizer.Normalize(Event(), DateTimeOffset.UtcNow).EventCategory
            .Should().Be(EventCategories.ProductInteraction);
    }

    [Fact]
    public void Clamps_a_clock_skewed_timestamp_but_keeps_the_original()
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var evt = Event();
        evt.OccurredAt = receivedAt.AddHours(3);

        var normalized = Normalizer.Normalize(evt, receivedAt);

        normalized.OccurredAt.Should().BeCloseTo(receivedAt, TimeSpan.FromSeconds(1));
        normalized.Metadata.Should().ContainKey("_original_occurred_at");
    }

    [Fact]
    public void Stores_timestamps_in_utc()
    {
        var evt = Event();
        evt.OccurredAt = new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.FromHours(3.5));

        var normalized = Normalizer.Normalize(evt, DateTimeOffset.UtcNow);

        normalized.OccurredAt.Offset.Should().Be(TimeSpan.Zero);
    }
}

public class PartitionKeyTests
{
    [Fact]
    public void Customer_events_partition_by_tenant_and_customer()
    {
        PartitionKeys.ForCustomer("bestcx", "c_1").Should().Be("bestcx:c_1");
    }

    [Fact]
    public void Never_produces_a_null_key()
    {
        PartitionKeys.ForCustomer(null, null).Should().Be("unknown:unknown");
        PartitionKeys.ForCall(null).Should().Be("unknown");
    }
}

public class CursorTests
{
    [Fact]
    public void Round_trips_a_keyset_cursor()
    {
        var original = new Cx.Application.Common.Cursor(
            new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), "evt-9");

        Cx.Application.Common.Cursor.TryDecode(original.Encode(), out var decoded).Should().BeTrue();

        decoded.Timestamp.Should().Be(original.Timestamp);
        decoded.Tiebreaker.Should().Be(original.Tiebreaker);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData(null)]
    public void Rejects_a_malformed_cursor_without_throwing(string? value)
    {
        Cx.Application.Common.Cursor.TryDecode(value, out _).Should().BeFalse();
    }

    [Fact]
    public void Is_url_safe()
    {
        var encoded = new Cx.Application.Common.Cursor(DateTimeOffset.UtcNow, "a/b+c=d").Encode();

        encoded.Should().NotContain("/").And.NotContain("+").And.NotContain("=");
    }
}
