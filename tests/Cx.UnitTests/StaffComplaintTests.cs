using Cx.Application.Ai;
using Cx.Contracts.Events;
using Cx.Domain.Entities;
using Cx.Ingestion.Api;
using FluentAssertions;
using Xunit;

namespace Cx.UnitTests;

public class StaffComplaintTests
{
    private static TopicCatalog Catalog()
    {
        var root = Guid.NewGuid();
        var retired = Guid.NewGuid();
        var child = Guid.NewGuid();
        return new(7, new[] {
            new Topic { Id = root, Code = "payment", Title = "Payment", IsActive = true },
            new Topic { Id = child, ParentId = root, Code = "payment.error", Title = "Payment error", IsActive = true },
            new Topic { Id = Guid.NewGuid(), ParentId = child, Code = "payment.error.bank", Title = "Bank", IsActive = true },
            new Topic { Id = Guid.NewGuid(), ParentId = root, Code = "payment.old", Title = "Old", IsActive = false },
            new Topic { Id = retired, Code = "retired", Title = "Old", IsActive = false },
            new Topic { Id = Guid.NewGuid(), ParentId = retired, Code = "retired.child", Title = "Child", IsActive = true }
        });
    }
    private static StaffComplaint Body() => new(Guid.NewGuid(), "C-123", "payment.error", 7, "Customer payment problem");

    [Fact]
    public void Complaint_requires_an_active_subcategory_instead_of_a_root()
    {
        var catalog = Catalog();
        StaffComplaints.Validate(Body(), catalog).Should().BeEmpty();
        StaffComplaints.Validate(Body() with { Category = "payment.error.bank" }, catalog).Should().BeEmpty();
        foreach (var category in new[] { "payment", "payment.old", "retired.child" })
            StaffComplaints.Validate(Body() with { Category = category }, catalog).Should().ContainSingle()
                .Which.Should().Be("Select the active subcategory of the problem.");
    }

    [Fact]
    public void Missing_customer_text_or_active_category_is_rejected()
    {
        var body = Body();
        StaffComplaints.Validate(body with { CustomerId = null }, Catalog()).Should().ContainSingle();
        StaffComplaints.Validate(body with { Comment = " " }, Catalog()).Should().ContainSingle();
        StaffComplaints.Validate(body with { Comment = new string('x', 12001) }, Catalog()).Should().ContainSingle();
        StaffComplaints.Validate(body with { Category = "retired" }, Catalog()).Should().ContainSingle();
        StaffComplaints.Validate(body with { Category = "missing" }, Catalog()).Should().ContainSingle();
        StaffComplaints.Validate(body with { SubmissionId = Guid.Empty }, Catalog()).Should().ContainSingle();
    }

    [Fact]
    public void Complaint_records_the_customer_category_and_actor_without_voice_or_ratings()
    {
        var body = Body();
        var evt = StaffComplaints.ToEvent(body, Catalog(), "bestcx", "admin");
        evt.EventName.Should().Be(EventNames.FeedbackSubmitted);
        evt.Customer!.CustomerId.Should().Be("C-123");
        evt.Source.Service.Should().Be("cx-console");
        evt.Source.Channel.Should().Be("manual");
        evt.Metadata["submission_kind"]!.GetValue<string>().Should().Be("complaint");
        evt.Metadata["selected_category"]!.GetValue<string>().Should().Be("payment.error");
        evt.Metadata["category_title"]!.GetValue<string>().Should().Be("Payment › Payment error");
        evt.Metadata["recorded_by"]!.GetValue<string>().Should().Be("admin");
        evt.Metadata["comment"]!.GetValue<string>().Should().Contain(body.Comment!);
        evt.Voice.Should().BeNull();
        evt.Metadata.ContainsKey("rating").Should().BeFalse();
    }

    [Fact]
    public void Retries_have_the_same_event_identity_but_other_tenants_and_actors_do_not()
    {
        var body = Body();
        var evt = StaffComplaints.ToEvent(body, Catalog(), "bestcx", "admin");
        StaffComplaints.ToEvent(body, Catalog(), "bestcx", "admin").EventId.Should().Be(evt.EventId);
        StaffComplaints.ToEvent(body, Catalog(), "other", "admin").EventId.Should().NotBe(evt.EventId);
        StaffComplaints.ToEvent(body, Catalog(), "bestcx", "other-admin").EventId.Should().NotBe(evt.EventId);
    }
}
