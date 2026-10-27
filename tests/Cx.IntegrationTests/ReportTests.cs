using Cx.Application.Query;
using Cx.Domain.Entities;
using Cx.Infrastructure.Database.Repositories;
using Dapper;
using FluentAssertions;
using Xunit;

namespace Cx.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class ReportTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Reports_use_valid_answers_exclusive_dates_tenant_isolation_and_idempotent_feedback()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var tenant = $"report-{Guid.NewGuid():N}";
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(3);
        var feedback = new FeedbackRepository(fixture.DataSource);
        async Task Insert(int? csat, int? ces, string? tenantId = null, DateTimeOffset? at = null, string? type = null, string? customerId = null)
        {
            var row = new Feedback { TenantId = tenantId ?? tenant, Provider = "report-test", ExternalId = Guid.NewGuid().ToString(),
                CsatScore = csat, CesScore = ces, Rating = 5, NpsScore = 10,
                FeedbackType = type ?? FeedbackTypes.Generic, SubmittedAt = at ?? from, CustomerId = customerId ?? "C1", Channel = "web", SelectedTopic = "delivery" };
            await feedback.UpsertAsync(row);
            await feedback.UpsertAsync(row);
        }
        await Insert(4, 7);
        await Insert(2, 1, at: from.AddDays(1), customerId: "C2");
        await Insert(null, null); // generic stars and unrelated NPS must not count
        await Insert(9, null); // invalid historical satisfaction
        await Insert(1, 1, tenantId: tenant + "other");
        await Insert(1, 1, at: from.AddSeconds(-1));
        await Insert(1, 1, at: to);
        var repo = new ReportRepository(fixture.DataSource);
        var report = await repo.GetAsync(new ReportQuery { TenantId = tenant, From = from, To = to });
        report.Summary.Total.Should().Be(4);
        report.Summary.CsatResponses.Should().Be(2);
        report.Summary.CsatPercent.Should().Be(50);
        report.Summary.CesResponses.Should().Be(2);
        report.Summary.CesAverage.Should().Be(4);
        report.Summary.NpsResponses.Should().Be(0);
        report.CesDistribution.Sum(x => x.Count).Should().Be(2);
        report.Trend.Should().HaveCount(3);
        report.Trend[0].Name.Should().Be("2026-10-01");
        report.Trend[2].Total.Should().Be(0);
        report.Trend[2].CsatPercent.Should().BeNull();
        var customerReport = await repo.GetAsync(new ReportQuery { TenantId = tenant, From = from, To = to, CustomerId = "C2" });
        customerReport.Summary.Total.Should().Be(1);
        customerReport.Summary.Customers.Should().Be(1);
        customerReport.Summary.CsatPercent.Should().Be(0);
        customerReport.Summary.CesAverage.Should().Be(1);
        customerReport.Trend.Sum(x => x.Total).Should().Be(1);
        customerReport.Topics.Sum(x => x.Total).Should().Be(1);
        var unknownCustomer = await repo.GetAsync(new ReportQuery { TenantId = tenant, From = from, To = to, CustomerId = "missing" });
        unknownCustomer.Summary.Total.Should().Be(0);
        var empty = await repo.GetAsync(new ReportQuery { TenantId = tenant, From = from, To = to, Channels = ["email"] });
        empty.Summary.Total.Should().Be(0);
        empty.Summary.CsatPercent.Should().BeNull();
        empty.Summary.CesAverage.Should().BeNull();
        empty.Summary.NpsScore.Should().BeNull();
    }

    [SkippableFact]
    public async Task Combined_filters_respect_latest_analysis_human_overrides_and_topic_descendants()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var tenant = $"filter-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var feedback = new FeedbackRepository(fixture.DataSource);
        var id = await feedback.UpsertAsync(new Feedback { TenantId = tenant, Provider = "cx-customer-portal", ExternalId = "one", EventId = "event-one",
            SelectedTopic = "child", FeedbackType = FeedbackTypes.VoiceOfCustomer, Rating = 4, CesScore = 6, NpsScore = 0,
            Channel = "web", Status = "new", Comment = "delivery experience", SubmittedAt = now });
        (await feedback.GetAsync(tenant, id))!.CesScore.Should().Be(6);
        await using var connection = await fixture.DataSource.OpenAsync();
        var taxonomyId = Guid.NewGuid(); var parent = Guid.NewGuid(); var child = Guid.NewGuid(); var analysis = Guid.NewGuid();
        await connection.ExecuteAsync("""
            INSERT INTO taxonomy(id, tenant_id, name) VALUES (@taxonomyId, @tenant, 'report');
            INSERT INTO topic(id, tenant_id, taxonomy_id, code, title) VALUES (@parent, @tenant, @taxonomyId, 'parent', 'Parent');
            INSERT INTO topic(id, tenant_id, taxonomy_id, parent_id, code, title) VALUES (@child, @tenant, @taxonomyId, @parent, 'child', 'Child');
            INSERT INTO ai_analysis(id, tenant_id, entity_type, entity_id, analysis_type, model_provider, model_name, result, status, created_at)
            VALUES (gen_random_uuid(), @tenant, 'cx_event', 'event-one', 'feedback', 'test', 'test', '{"sentiment":"positive","category":"other"}', 'superseded', @now - interval '1 minute'),
                   (@analysis, @tenant, 'cx_event', 'event-one', 'feedback', 'test', 'test', '{"sentiment":"neutral","category":"other"}', 'succeeded', @now);
            INSERT INTO human_review(id, tenant_id, analysis_id, entity_type, entity_id, status, review_reason, override_result)
            VALUES (gen_random_uuid(), @tenant, @analysis, 'cx_event', 'event-one', 'OVERRIDDEN', 'qa_sample',
                    '{"sentiment":"negative","category":"parent","subcategory":"child","severity":"high"}');
            """, new { tenant, taxonomyId, parent, child, analysis, now = now.ToUniversalTime() });
        var query = new ReportQuery { TenantId = tenant, From = now.AddDays(-1), To = now.AddDays(1), Topics = ["parent"],
            Sentiments = ["negative"], Channels = ["web"], Severities = ["high"], Types = ["voc"], Statuses = ["new"], Search = "delivery" };
        var repo = new ReportRepository(fixture.DataSource);
        var report = await repo.GetAsync(query);
        report.Summary.Total.Should().Be(1);
        report.Summary.CsatPercent.Should().Be(100);
        report.Summary.CesAverage.Should().Be(6);
        report.Summary.NpsScore.Should().Be(-100);
        report.Summary.OpenHighSeverity.Should().Be(1);
        report.Topics.Should().ContainSingle().Which.Name.Should().Be("child");
        query.Sentiments = ["positive"];
        (await repo.GetAsync(query)).Summary.Total.Should().Be(0);
        query.Sentiments = [];
        query.Channels = ["email"];
        (await repo.GetAsync(query)).Summary.Total.Should().Be(0);
    }
}
