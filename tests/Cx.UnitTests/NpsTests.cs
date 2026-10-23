using System.Text.Json.Nodes;
using Cx.Application.Ingestion;
using Cx.Application.Options;
using Cx.Application.Query;
using Cx.Contracts.Events;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cx.UnitTests;

public class NpsTests
{
    [Theory]
    [InlineData(6, 2, 2, 40)]
    [InlineData(1, 0, 0, 100)]
    [InlineData(0, 0, 1, -100)]
    [InlineData(0, 3, 0, 0)]
    [InlineData(1, 1, 1, 0)]
    public void Calculates_response_based_score(long promoters, long passives, long detractors, int expected)
    {
        var summary = new NpsSummary(promoters, passives, detractors);
        summary.Score.Should().Be(expected);
        summary.Total.Should().Be(promoters + passives + detractors);
    }

    [Fact]
    public void No_responses_is_not_a_zero_score()
    {
        var summary = new NpsSummary(0, 0, 0);
        summary.Score.Should().BeNull();
        summary.PromoterPercent.Should().BeNull();
        summary.PassivePercent.Should().BeNull();
        summary.DetractorPercent.Should().BeNull();
    }

    [Fact]
    public void Rounds_only_after_subtraction()
    {
        var summary = new NpsSummary(2, 0, 1);
        summary.Score.Should().Be(33.33m);
        summary.PromoterPercent.Should().Be(66.67m);
        summary.DetractorPercent.Should().Be(33.33m);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("6", true)]
    [InlineData("7", true)]
    [InlineData("8", true)]
    [InlineData("9", true)]
    [InlineData("10", true)]
    [InlineData("-1", false)]
    [InlineData("11", false)]
    [InlineData("6.5", false)]
    [InlineData("null", false)]
    [InlineData("\"9\"", false)]
    [InlineData("true", false)]
    [InlineData("{}", false)]
    public void Validates_canonical_nps_before_persistence(string score, bool valid)
    {
        var evt = new CanonicalEvent
        {
            EventId = "nps-test",
            SchemaVersion = EventSchema.CurrentVersion,
            EventName = EventNames.FeedbackNpsSubmitted,
            EventCategory = EventCategories.Feedback,
            TenantId = "bestcx",
            OccurredAt = DateTimeOffset.UtcNow,
            Source = new SourceRef { Type = "feedback", Service = "test" }
        };
        evt.Metadata["nps_score"] = JsonNode.Parse(score);
        var validator = new EventValidator(Options.Create(new IngestionOptions()));
        validator.Validate(evt).IsValid.Should().Be(valid);
        evt.Metadata.Clear();
        validator.Validate(evt).IsValid.Should().BeFalse();
    }
}
