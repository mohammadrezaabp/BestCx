using Cx.Domain.Entities;
using Cx.Infrastructure.Database.Repositories;
using FluentAssertions;
using Xunit;

namespace Cx.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class NpsTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Voice_of_customer_questionnaires_contribute_to_nps_and_can_be_filtered()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var tenant = $"voc-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var feedback = new FeedbackRepository(fixture.DataSource);
        foreach (var kind in new[] { FeedbackTypes.VoiceOfCustomer, FeedbackTypes.Complaint })
            await feedback.UpsertAsync(new Feedback
            {
                TenantId = tenant, Provider = "cx-customer-portal", ExternalId = Guid.NewGuid().ToString(),
                FeedbackType = kind, NpsScore = kind == FeedbackTypes.VoiceOfCustomer ? 0 : null,
                Comment = "Questionnaire answer", SubmittedAt = now
            });
        var report = await new InsightsRepository(fixture.DataSource).GetOverviewAsync(tenant, now.AddMinutes(-1), now.AddMinutes(1));
        report.Nps.Total.Should().Be(1);
        report.Nps.Score.Should().Be(-100);
        var rows = await feedback.QueryGridAsync(new Cx.Application.Query.FeedbackGridQuery { TenantId = tenant, Kind = "voc", PromptVersion = "cx-prompt-v3" });
        rows.Items.Should().ContainSingle().Which.FeedbackType.Should().Be(FeedbackTypes.VoiceOfCustomer);
    }

    [SkippableFact]
    public async Task Overview_counts_nps_groups_with_tenant_time_and_replay_isolation()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var tenant = $"nps-{Guid.NewGuid():N}";
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = from.AddDays(1);
        var feedback = new FeedbackRepository(fixture.DataSource);
        var insights = new InsightsRepository(fixture.DataSource);

        async Task Insert(int? score, string? tenantId = null, DateTimeOffset? at = null,
            string type = FeedbackTypes.Nps)
        {
            var row = new Feedback
            {
                TenantId = tenantId ?? tenant,
                Provider = "nps-test",
                ExternalId = Guid.NewGuid().ToString(),
                FeedbackType = type,
                NpsScore = score,
                Rating = type == FeedbackTypes.Rating ? 5 : null,
                SubmittedAt = at ?? from
            };
            await feedback.UpsertAsync(row);
            await feedback.UpsertAsync(row); // replay must not increase the denominator
        }

        foreach (var score in new[] { 9, 10, 9, 10, 9, 10, 7, 8, 0, 6 })
            await Insert(score);

        await Insert(0, tenantId: tenant + "-other");
        await Insert(0, at: from.AddTicks(-10));
        await Insert(0, at: to); // upper bound is exclusive
        await Insert(null, type: FeedbackTypes.Rating);
        await Insert(5, type: FeedbackTypes.Csat);
        await Insert(null); // historical incomplete/invalid responses are excluded
        await Insert(-1);
        await Insert(11);

        var report = (await insights.GetOverviewAsync(tenant, from, to)).Nps;
        report.Total.Should().Be(10);
        report.Promoters.Should().Be(6);
        report.Passives.Should().Be(2);
        report.Detractors.Should().Be(2);
        report.PromoterPercent.Should().Be(60);
        report.PassivePercent.Should().Be(20);
        report.DetractorPercent.Should().Be(20);
        report.Score.Should().Be(40);

        var empty = (await insights.GetOverviewAsync(tenant + "-empty", from, to)).Nps;
        empty.Total.Should().Be(0);
        empty.Score.Should().BeNull();
    }
}
