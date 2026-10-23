using Cx.Application.Ai;
using Cx.Application.Ingestion;
using Cx.Application.Options;
using Cx.Contracts.Events;
using Cx.Domain.Entities;
using Cx.Ingestion.Api;
using FluentAssertions;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
using Xunit;

namespace Cx.UnitTests;

public class CustomerPortalTests
{
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid Child = Guid.NewGuid();
    private static TopicCatalog Catalog() => new(7, new[]
    {
        new Topic { Id = Root, Code = "service", Title = "Services", IsActive = true },
        new Topic { Id = Child, ParentId = Root, Code = "delivery", Title = "Delivery", IsActive = true },
        new Topic { Id = Guid.NewGuid(), Code = "retired", Title = "Old", IsActive = false }
    });

    private static PortalSubmission Submission(string kind = "voc") => new(Guid.NewGuid(), kind, "delivery", 7,
        "Delivery delay", "Description of customer experience", 2, 0,
        new() { ["satisfaction"] = "2", ["effort"] = "7", ["experience"] = "Delivery was late", ["improvement"] = "More detailed information" });

    [Fact]
    public void Questionnaire_persists_wording_category_revision_and_zero_nps_as_an_analyzable_event()
    {
        var body = Submission();
        PortalQuestionnaire.Validate(body, Catalog()).Should().BeEmpty();
        var evt = PortalQuestionnaire.ToEvent(body, Catalog(), "configured-tenant", "C-maryam");
        evt.EventName.Should().Be(EventNames.FeedbackSubmitted);
        evt.TenantId.Should().Be("configured-tenant");
        evt.Customer!.CustomerId.Should().Be("C-maryam");
        evt.Customer.AnonymousId.Should().BeNull();
        evt.Metadata["nps_score"]!.GetValue<int>().Should().Be(0);
        evt.Metadata["taxonomy_revision"]!.GetValue<int>().Should().Be(7);
        evt.Metadata["questionnaire"]!.AsArray().Should().HaveCount(4);
        evt.Metadata["csat_score"]!.GetValue<int>().Should().Be(2);
        evt.Metadata["ces_score"]!.GetValue<int>().Should().Be(7);
        var text = evt.Metadata["comment"]!.GetValue<string>();
        text.Should().Contain("Services › Delivery").And.Contain("Delivery was late").And.Contain("More detailed information");
        text.Should().NotContain("maryam@example.com");
        new EventValidator(Options.Create(new IngestionOptions())).Validate(evt).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Submitted_events_do_not_claim_contact_details_or_consent()
    {
        var evt = PortalQuestionnaire.ToEvent(Submission(), Catalog(), "bestcx", "C-maryam");
        var modelDetail = ContextBuilder.MetadataDetail(evt);
        modelDetail.Should().NotContain("maryam@example.com").And.NotContain("Maryam");
        modelDetail.Should().Contain("More detailed information");
        evt.Metadata.ContainsKey("contact").Should().BeFalse();
        evt.Metadata.ContainsKey("consent").Should().BeFalse();
    }

    [Theory]
    [InlineData("complaint")]
    [InlineData("feedback")]
    [InlineData("voc")]
    public void Retry_preserves_the_event_identity(string kind)
    {
        var body = Submission(kind);
        var first = PortalQuestionnaire.ToEvent(body, Catalog(), "bestcx", "C-maryam");
        var retry = PortalQuestionnaire.ToEvent(body, Catalog(), "bestcx", "C-maryam");
        first.EventId.Should().Be(retry.EventId);
        first.Metadata["external_id"]!.GetValue<string>().Should().Be(retry.Metadata["external_id"]!.GetValue<string>());
    }

    [Fact]
    public void A_receipt_cannot_overwrite_another_customers_submission()
    {
        var body = Submission("feedback");
        var first = PortalQuestionnaire.ToEvent(body, Catalog(), "bestcx", "C-maryam");
        var other = PortalQuestionnaire.ToEvent(body, Catalog(), "bestcx", "C-other");
        var otherTenant = PortalQuestionnaire.ToEvent(body, Catalog(), "other", "C-maryam");
        first.EventId.Should().NotBe(other.EventId).And.NotBe(otherTenant.EventId);
        first.Metadata["submission_id"]!.GetValue<string>().Should().Be(body.SubmissionId.ToString());
    }

    [Fact]
    public void Inactive_and_unknown_categories_are_rejected()
    {
        PortalQuestionnaire.Validate(Submission() with { Category = "retired" }, Catalog()).Should().NotBeEmpty();
        PortalQuestionnaire.Validate(Submission() with { Category = "unknown" }, Catalog()).Should().NotBeEmpty();
    }

    [Fact]
    public void Missing_out_of_range_and_injected_answers_are_rejected()
    {
        PortalQuestionnaire.Validate(Submission() with { Answers = null }, Catalog()).Should().NotBeEmpty();
        PortalQuestionnaire.Validate(Submission() with { NpsScore = 11 }, Catalog()).Should().NotBeEmpty();
        var answers = Submission().Answers!;
        answers["satisfaction"] = "6";
        PortalQuestionnaire.Validate(Submission() with { Answers = answers }, Catalog()).Should().NotBeEmpty();
        answers["satisfaction"] = "3";
        answers["injected-question"] = "spoofed";
        PortalQuestionnaire.Validate(Submission() with { Answers = answers }, Catalog()).Should().NotBeEmpty();
    }

    [Fact]
    public void Empty_complaints_and_oversized_responses_are_rejected()
    {
        PortalQuestionnaire.Validate(Submission("complaint") with { Subject = " ", Comment = " " }, Catalog()).Should().HaveCount(2);
        PortalQuestionnaire.Validate(Submission("feedback") with { Comment = new string('a', 4001) }, Catalog()).Should().NotBeEmpty();
    }

    [Fact]
    public void A_voice_message_replaces_the_written_description_but_not_the_questionnaire()
    {
        PortalQuestionnaire.Validate(Submission("feedback") with { Comment = null }, Catalog(), hasVoice: true)
            .Should().BeEmpty();
        PortalQuestionnaire.Validate(Submission("complaint") with { Comment = " " }, Catalog(), hasVoice: true)
            .Should().BeEmpty();
        PortalQuestionnaire.Validate(Submission("complaint") with { Subject = " " }, Catalog(), hasVoice: true)
            .Should().ContainSingle("a complaint still needs its subject");
        PortalQuestionnaire.Validate(Submission("voc"), Catalog(), hasVoice: true).Should().NotBeEmpty();
    }

    [Fact]
    public void Voice_header_keeps_the_chosen_topic_and_ends_with_the_transcript_marker()
    {
        var header = PortalQuestionnaire.VoiceHeader(Submission("complaint"), Catalog());
        header.Should().Contain("Complaint").And.Contain("Services › Delivery").And.Contain("Delivery delay")
            .And.NotContain("maryam@example.com");
        header.Should().EndWith(VoiceFeedbackComment.TranscriptMarker);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("8")]
    [InlineData("3.5")]
    [InlineData("")]
    public void Invalid_or_missing_effort_answers_are_rejected(string value)
    {
        var body = Submission();
        body.Answers!["effort"] = value;
        PortalQuestionnaire.Validate(body, Catalog()).Should().NotBeEmpty();
    }

    [Fact]
    public void Feedback_and_voice_capture_independent_scores_without_using_nps_as_csat()
    {
        var body = Submission("feedback") with { Rating = 5, CesScore = 1 };
        var evt = PortalQuestionnaire.ToEvent(body, Catalog(), "bestcx", "C-maryam");
        evt.Metadata["csat_score"]!.GetValue<int>().Should().Be(5);
        evt.Metadata["ces_score"]!.GetValue<int>().Should().Be(1);
        PortalQuestionnaire.VoiceHeader(body, Catalog()).Should().Contain("1/7");
        PortalQuestionnaire.Validate(body with { CesScore = 8 }, Catalog(), hasVoice: true).Should().NotBeEmpty();
        PortalQuestionnaire.ToEvent(Submission("complaint"), Catalog(), "bestcx", "C-maryam")
            .Metadata["csat_score"].Should().BeNull();
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("7", true)]
    [InlineData("8", false)]
    [InlineData("3.5", false)]
    [InlineData("\"7\"", false)]
    public void Canonical_ingestion_rejects_invalid_effort_scores(string json, bool valid)
    {
        var evt = PortalQuestionnaire.ToEvent(Submission(), Catalog(), "bestcx", "C-maryam");
        evt.Metadata["ces_score"] = JsonNode.Parse(json);
        new EventValidator(Options.Create(new IngestionOptions())).Validate(evt).IsValid.Should().Be(valid);
    }
}
