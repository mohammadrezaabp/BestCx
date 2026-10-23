using Cx.Application.Operations;
using FluentAssertions;
using Xunit;

namespace Cx.UnitTests;

public class OperationsTests
{
    private static readonly BusinessCalendar Calendar = new() { TimeZone = "Asia/Tehran", WorkingDays = [6, 0, 1, 2, 3] };
    [Fact]
    public void Business_deadline_skips_nonworking_days_and_holidays_in_Tehran()
    {
        var thursday = new DateTimeOffset(2026, 10, 15, 15, 0, 0, TimeSpan.FromHours(3.5));
        var calendar = Calendar with { Holidays = ["2026-10-17"] };
        BusinessClock.Add(thursday, 60, calendar).Should().Be(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.FromHours(3.5)));
    }
    [Fact]
    public void Deadline_can_end_exactly_at_close_and_spans_working_windows()
    {
        var monday = new DateTimeOffset(2026, 10, 12, 16, 30, 0, TimeSpan.FromHours(3.5));
        BusinessClock.Add(monday, 30, Calendar).Should().Be(monday.AddMinutes(30));
        BusinessClock.Add(monday, 90, Calendar).Should().Be(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.FromHours(3.5)));
        BusinessClock.Between(monday, monday.AddDays(1).AddHours(-6.5), Calendar).Should().Be(90);
    }
    [Fact]
    public void Empty_or_invalid_calendars_are_rejected_instead_of_looping()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var calendar in new[] { Calendar with { WorkingDays = [] }, Calendar with { StartsAt = "18:00" }, Calendar with { Holidays = ["1405/07/21"] }, Calendar with { TimeZone = "bad-zone" } })
            FluentActions.Invoking(() => BusinessClock.Add(now, 30, calendar)).Should().Throw<OperationsException>();
        BusinessClock.Between(now, now.AddDays(-1), Calendar).Should().Be(0);
    }
    [Fact]
    public void Missing_scores_do_not_turn_into_dissatisfaction_or_churn_predictions()
    {
        var customer = new FollowUpCustomer(); FollowUpPriority.Apply(customer);
        customer.FollowUpScore.Should().Be(0); customer.Reasons.Should().BeEmpty();
        customer.Complaints = 3; customer.OpenCases = 2; customer.OverdueCases = 1; customer.RecentCsat = 2;
        customer.PreviousCsat = 4; customer.RecentCes = 2; customer.NegativeFeedback = 3;
        FollowUpPriority.Apply(customer);
        customer.FollowUpScore.Should().Be(58); customer.Reasons.Should().HaveCount(3);
        customer.Complaints = 100; customer.OpenCases = 100; customer.OverdueCases = 100;
        FollowUpPriority.Apply(customer); customer.FollowUpScore.Should().Be(100);
    }
}
