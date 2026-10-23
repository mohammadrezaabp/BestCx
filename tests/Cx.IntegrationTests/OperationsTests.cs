using Cx.Application.Operations;
using Cx.Application.Options;
using Cx.Domain.Entities;
using Cx.Infrastructure.Database.Repositories;
using Cx.Infrastructure.Operations;
using Cx.Infrastructure.Security;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cx.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class OperationsTests(PostgresFixture fixture)
{
    private readonly CancellationToken ct = CancellationToken.None;
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 10, 0, 0, TimeSpan.FromHours(3.5));
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
    private async Task<(string Tenant, OperationsStore Store, ManualClock Clock)> Setup()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var tenant = $"ops-{Guid.NewGuid():N}";
        var clock = new ManualClock();
        await using var db = await fixture.DataSource.OpenAsync(ct);
        foreach (var customer in new[] { "customer", "other" }) await db.ExecuteAsync("""
            INSERT INTO customer_identity(id,tenant_id,customer_id,display_name,first_seen_at,last_seen_at)
            VALUES(@id,@tenant,@customer,@customer,@now,@now)
            """, new { id = Guid.NewGuid(), tenant, customer, now = clock.Now.ToUniversalTime() });
        return (tenant, new(fixture.DataSource, clock, Options.Create(new KafkaOptions())), clock);
    }
    private async Task<Guid> Staff(string tenant, string role = "cx_analyst")
    {
        var user = Guid.NewGuid();
        var users = new UserAccounts(fixture.DataSource, Options.Create(new AuthOptions()));
        await users.SaveStaffAsync(tenant, "test", new(user, user.ToString("N"), "Agent", role, "password-for-test"), ct);
        return user;
    }
    private Task<Guid> Create(OperationsStore store, string tenant, Guid? feedback = null, Guid? assignee = null, Guid? team = null) =>
        store.CreateAsync(tenant, "test", new(Guid.NewGuid(), "customer", "Payment tracking", "Description of the request", AssigneeId: assignee, TeamId: team, FeedbackId: feedback), ct);
    private static UpdateCaseInput Update(WorkCase item, string status, string resolution = "") =>
        new(item.Version, status, item.TeamId, item.AssigneeId, item.ProblemId, resolution);
    private async Task<Guid> Feedback(string tenant, DateTimeOffset at, string kind = "complaint", int? csat = null)
    {
        return await new FeedbackRepository(fixture.DataSource).UpsertAsync(new Feedback { Id = Guid.NewGuid(), TenantId = tenant,
            CustomerId = "customer", Provider = "test", ExternalId = Guid.NewGuid().ToString(), FeedbackType = kind, Comment = "Payment problem", CsatScore = csat, SubmittedAt = at }, ct);
    }
    [SkippableFact]
    public async Task Workflow_handoffs_are_atomic_team_scoped_and_keep_the_started_snapshot_until_close()
    {
        var (tenant, store, clock) = await Setup();
        var technician = await Staff(tenant); var accountant = await Staff(tenant);
        var technical = Guid.NewGuid(); var finance = Guid.NewGuid();
        await store.SaveTeamAsync(tenant, "test", new(technical, "Technical", null, [technician], Description: "Fix software malfunction"), ct);
        await store.SaveTeamAsync(tenant, "test", new(finance, "Finance", null, [accountant], Description: "Refund"), ct);
        var flow = new WorkflowInput(Guid.NewGuid(), "Payment processing", "Technical review and refund", null,
            [new("Technical review", technical, "Checking the transaction log"), new("Refund", finance)]);
        await store.SaveWorkflowAsync(tenant, "test", flow, ct);
        var definition = (await store.WorkflowsAsync(tenant, ct)).Single();
        var route = CaseRouter.Validate(technical.ToString(), flow.Id.ToString(), "Selection of technical team for payment error", "test-ai",
            await store.TeamsAsync(tenant, ct), [definition]);
        var feedback = await Feedback(tenant, clock.Now);
        var id = await Create(store, tenant, feedback);
        var item = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        await store.HandleWorkflowAsync(tenant, "technician", id, new(item.Version, "start"), technician, false, route, ct);
        item = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        item.TeamId.Should().Be(technical); item.AssigneeId.Should().Be(technician); item.StageIndex.Should().Be(0);
        item.RoutingModel.Should().Be("test-ai"); item.Workflow.Stages.Should().HaveCount(2);
        await store.SaveWorkflowAsync(tenant, "test", flow with { Name = "Edited procedure", Version = definition.Version,
            Stages = [new("Fresh step", finance)] }, ct);
        (await store.DetailAsync(tenant, id, null, ct))!.Case.Workflow.Stages[1].Title.Should().Be("Refund");
        var currentVersion = item.Version;
        await FluentActions.Awaiting(() => store.HandleWorkflowAsync(tenant, "accountant", id, new(currentVersion, "next", "Unauthorized transfer"), accountant, false, null, ct))
            .Should().ThrowAsync<OperationsException>().Where(error => error.Status == 403);
        await FluentActions.Awaiting(() => store.HandleWorkflowAsync(tenant, "technician", id, new(currentVersion, "next", " "), technician, false, null, ct))
            .Should().ThrowAsync<OperationsException>();
        await store.MessageAsync(tenant, "technician", id, new(Guid.NewGuid(), "The log was checked", false), null, ct);
        item = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        var nextVersion = item.Version;
        await store.HandleWorkflowAsync(tenant, "technician", id, new(nextVersion, "next", "The technical problem was fixed; Check the amount"), technician, false, null, ct);
        await FluentActions.Awaiting(() => store.HandleWorkflowAsync(tenant, "technician", id, new(nextVersion, "next", "Again"), technician, false, null, ct))
            .Should().ThrowAsync<OperationsException>().Where(error => error.Status == 409);
        item = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        item.TeamId.Should().Be(finance); item.AssigneeId.Should().BeNull(); item.StageIndex.Should().Be(1);
        (await store.CanAccessAsync(tenant, id, technician, false, ct)).Should().BeFalse();
        (await store.CanAccessAsync(tenant, id, accountant, false, ct)).Should().BeTrue();
        clock.Now = clock.Now.AddMinutes(20);
        await store.HandleWorkflowAsync(tenant, "accountant", id, new(item.Version, "close", "The money was returned to the customer’s account"), accountant, false, null, ct);
        var detail = (await store.DetailAsync(tenant, id, null, ct))!;
        detail.Case.Status.Should().Be("closed"); detail.Case.ResolutionText.Should().Contain("Returned");
        detail.Case.FirstResolvedAt.Should().NotBeNull();
        detail.Activities.Count(activity => activity.Kind == "workflow").Should().Be(3);
        detail.Activities.Single(activity => activity.Text.Contains("The technical problem was fixed")).TeamId.Should().Be(technical);
        detail.Activities.Single(activity => activity.Text == "The log was checked").StageTitle.Should().Be("Technical review");
        var customerView = (await store.DetailAsync(tenant, id, "customer", ct))!;
        customerView.Activities.Should().NotContain(activity => activity.Kind == "workflow" || activity.Text == "The log was checked");
        (await store.InvitationsAsync(tenant, "customer", ct)).Should().ContainSingle();
    }

    [SkippableFact]
    public async Task Workflows_reject_other_tenants_teams_and_stale_configuration_versions()
    {
        var (tenant, store, _) = await Setup();
        var team = Guid.NewGuid(); await store.SaveTeamAsync(tenant, "test", new(team, "Technical", null, []), ct);
        var flow = new WorkflowInput(Guid.NewGuid(), "Handling", "", null, [new("Review", team)]);
        await store.SaveWorkflowAsync(tenant, "test", flow, ct);
        await FluentActions.Awaiting(() => store.SaveWorkflowAsync(tenant, "test", flow, ct)).Should().ThrowAsync<OperationsException>().Where(error => error.Status == 409);
        await FluentActions.Awaiting(() => store.SaveWorkflowAsync(tenant + "other", "test", flow with { Id = Guid.NewGuid() }, ct)).Should().ThrowAsync<OperationsException>();
        await FluentActions.Awaiting(() => store.SaveWorkflowAsync(tenant, "test", flow with { Stages = [] }, ct)).Should().ThrowAsync<OperationsException>();
        (await store.WorkflowsAsync(tenant, ct)).Should().ContainSingle();
    }
    [SkippableFact]
    public async Task Routing_matches_the_topic_tree_even_when_descendant_codes_have_no_parent_prefix()
    {
        var (tenant, store, _) = await Setup();
        await using var db = await fixture.DataSource.OpenAsync(ct);
        var taxonomy = Guid.NewGuid(); var parent = Guid.NewGuid(); var child = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT INTO taxonomy(id,tenant_id,name) VALUES(@taxonomy,@tenant,'routing');
            INSERT INTO topic(id,tenant_id,taxonomy_id,code,title) VALUES(@parent,@tenant,@taxonomy,'billing','Finance');
            INSERT INTO topic(id,tenant_id,taxonomy_id,parent_id,code,title) VALUES(@child,@tenant,@taxonomy,@parent,'unrelated-leaf-code','Payment error');
            """, new { tenant, taxonomy, parent, child });
        var general = new WorkflowInput(Guid.NewGuid(), "General", "", null, [new("Review", null)]);
        var billing = general with { Id = Guid.NewGuid(), Name = "Finance", Topic = "billing" };
        await store.SaveWorkflowAsync(tenant, "test", general, ct);
        await store.SaveWorkflowAsync(tenant, "test", billing, ct);
        await store.SaveWorkflowAsync(tenant, "test", general with { Id = Guid.NewGuid(), Name = "Sending", Topic = "shipping" }, ct);
        await store.SaveWorkflowAsync(tenant + "other", "test", general with { Id = Guid.NewGuid() }, ct);
        (await store.RoutingWorkflowsAsync(tenant, "unrelated-leaf-code", ct)).Select(flow => flow.Id).Should().BeEquivalentTo([general.Id, billing.Id]);
        (await store.RoutingWorkflowsAsync(tenant, null, ct)).Should().ContainSingle().Which.Id.Should().Be(general.Id);
    }
    [SkippableFact]
    public async Task Cases_are_idempotent_and_private_notes_and_files_never_enter_customer_reads()
    {
        var (tenant, store, clock) = await Setup();
        var feedback = await Feedback(tenant, clock.Now);
        var id = await Create(store, tenant, feedback);
        (await Create(store, tenant, feedback)).Should().Be(id);
        var privateMessage = Guid.NewGuid();
        await store.MessageAsync(tenant, "test", id, new(privateMessage, "Internal note", false), null, ct);
        await store.MessageAsync(tenant, "test", id, new(privateMessage, "Internal note", false), null, ct);
        await store.MessageAsync(tenant, "test", id, new(Guid.NewGuid(), "General answer", true), null, ct);
        var privateFile = new CaseAttachment { Id = Guid.NewGuid(), Filename = "private.txt", ObjectKey = "private-key", ContentType = "text/plain", SizeBytes = 3, Public = false };
        var publicFile = new CaseAttachment { Id = Guid.NewGuid(), Filename = "public.txt", ObjectKey = "public-key", ContentType = "text/plain", SizeBytes = 3, Public = true };
        await store.SaveAttachmentAsync(tenant, "test", id, privateFile, null, ct);
        await store.SaveAttachmentAsync(tenant, "test", id, publicFile, null, ct);
        var staff = (await store.DetailAsync(tenant, id, null, ct))!;
        staff.Activities.Count(activity => activity.Text == "Internal note").Should().Be(1);
        var customer = (await store.DetailAsync(tenant, id, "customer", ct))!;
        customer.Activities.Should().NotContain(activity => activity.Text == "Internal note");
        customer.Attachments.Should().ContainSingle().Which.Id.Should().Be(publicFile.Id);
        (await store.AttachmentAsync(tenant, id, privateFile.Id, "customer", ct)).Should().BeNull();
        (await store.DetailAsync(tenant, id, "other", ct)).Should().BeNull();
        (await store.DetailAsync(tenant + "other", id, null, ct)).Should().BeNull();
        await FluentActions.Awaiting(() => store.MessageAsync(tenant, "test", id, new(privateMessage, "Another text", false), null, ct)).Should().ThrowAsync<OperationsException>();
    }
    [SkippableFact]
    public async Task Waiting_for_customer_pauses_only_resolution_and_first_outcome_metrics_survive_reopening()
    {
        var (tenant, store, clock) = await Setup();
        var id = await Create(store, tenant);
        clock.Now = clock.Now.AddMinutes(10);
        await store.MessageAsync(tenant, "test", id, new(Guid.NewGuid(), "More information is needed", true), null, ct);
        var item = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        item.ResponseBusinessMinutes.Should().Be(10);
        var oldDue = item.ResolutionDueAt;
        await store.UpdateAsync(tenant, "test", id, Update(item, "waiting_customer"), ct);
        clock.Now = clock.Now.AddHours(2);
        await store.MessageAsync(tenant, OperationsStore.CustomerActor("customer"), id, new(Guid.NewGuid(), "Additional information"), "customer", ct);
        item = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        item.Status.Should().Be("in_progress"); item.PausedMinutes.Should().Be(120);
        item.ResolutionDueAt.Should().Be(BusinessClock.Add(oldDue, 120, item.Calendar));
        clock.Now = clock.Now.AddMinutes(10);
        await store.UpdateAsync(tenant, "test", id, Update(item, "resolved", "The problem is solved"), ct);
        item = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        item.ResolutionBusinessMinutes.Should().Be(20);
        var firstResolution = item.FirstResolvedAt;
        await store.CustomerActionAsync(tenant, "customer", id, new(item.Version, "reopen", "The problem is back"), ct);
        item = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        item.ReopenCount.Should().Be(1); item.Status.Should().Be("in_progress"); item.PausedMinutes.Should().Be(0);
        item.FirstResolvedAt.Should().Be(firstResolution); item.ResolutionBusinessMinutes.Should().Be(20);
        var report = await store.OutcomesAsync(tenant, clock.Now.AddDays(-1), clock.Now.AddDays(1), ct);
        report.Summary.ResponseMeasured.Should().Be(1); report.Summary.ResolutionMeasured.Should().Be(1);
        report.Summary.Reopened.Should().Be(1); report.Summary.Resolved.Should().Be(0);
        report.Summary.AverageResolutionMinutes.Should().Be(20);
        await FluentActions.Awaiting(() => store.UpdateAsync(tenant, "test", id, Update(item, "closed", "") , ct)).Should().ThrowAsync<OperationsException>();
        await FluentActions.Awaiting(() => store.UpdateAsync(tenant, "test", id, Update(item, "closed", "Solved") with { Version = item.Version - 1 }, ct)).Should().ThrowAsync<OperationsException>();
    }
    [SkippableFact]
    public async Task Aftercare_answer_is_atomic_idempotent_and_enters_feedback_and_transactional_outbox()
    {
        var (tenant, store, clock) = await Setup();
        var id = await Create(store, tenant);
        var item = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        await store.UpdateAsync(tenant, "test", id, Update(item, "resolved", "The proceedings were carried out"), ct);
        var invitations = await store.InvitationsAsync(tenant, "customer", ct);
        var invitation = invitations.Should().ContainSingle().Subject;
        await store.AnswerAsync(tenant, "customer", invitation.Id, new(5, "Thank you"), ct);
        await store.AnswerAsync(tenant, "customer", invitation.Id, new(5, "Thank you"), ct);
        await FluentActions.Awaiting(() => store.AnswerAsync(tenant, "other", invitation.Id, new(5, "Thank you"), ct)).Should().ThrowAsync<OperationsException>();
        await using var db = await fixture.DataSource.OpenAsync(ct);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM feedback WHERE tenant_id=@tenant AND provider='cx-survey-campaign'", new { tenant })).Should().Be(1);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM cx_event WHERE tenant_id=@tenant AND event_id=@event", new { tenant, @event = $"survey-{invitation.Id:N}" })).Should().Be(1);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM cx_outbox WHERE aggregate_id=@event", new { @event = $"survey-{invitation.Id:N}" })).Should().Be(1);
        var report = await store.OutcomesAsync(tenant, clock.Now.AddDays(-1), clock.Now.AddDays(1), ct);
        report.Summary.AftercareCsat.Should().Be(100); report.Summary.AftercareResponses.Should().Be(1);
    }
    [SkippableFact]
    public async Task Team_ownership_last_admin_protection_and_tenant_scoping_are_enforced()
    {
        var (tenant, store, _) = await Setup();
        var agent = await Staff(tenant);
        var lead = await Staff(tenant, "support_supervisor");
        var other = await Staff(tenant + "other");
        var team = Guid.NewGuid();
        await store.SaveTeamAsync(tenant, "test", new(team, "Finance", lead, [agent]), ct);
        var id = await Create(store, tenant, assignee: agent, team: team);
        (await store.CanAccessAsync(tenant, id, agent, false, ct)).Should().BeTrue();
        (await store.CanAccessAsync(tenant, id, lead, false, ct)).Should().BeTrue();
        (await store.CanAccessAsync(tenant, id, other, false, ct)).Should().BeFalse();
        await FluentActions.Awaiting(() => Create(store, tenant, assignee: other)).Should().ThrowAsync<OperationsException>();
        var admin = await Staff(tenant, "admin");
        var users = new UserAccounts(fixture.DataSource, Options.Create(new AuthOptions()));
        await FluentActions.Awaiting(() => users.SaveStaffAsync(tenant, "test", new(admin, admin.ToString("N"), "Manager", "cx_analyst", null), ct)).Should().ThrowAsync<OperationsException>();
        var problem = Guid.NewGuid();
        await store.SaveProblemAsync(tenant, "test", new(problem, "Joint disorder", null, "", "", "open", lead), ct);
        var item = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        await store.UpdateAsync(tenant, "test", id, Update(item, "in_progress") with { ProblemId = problem }, ct);
        var problems = await store.ProblemsAsync(tenant, ct);
        problems.Should().ContainSingle().Which.AffectedCustomers.Should().Be(1);
        var queue = await store.FollowUpAsync(tenant, null, 1, ct);
        queue.Should().ContainSingle().Which.Reasons.Should().Contain("1 Unsolved case");
    }
    [SkippableFact]
    public async Task Rules_run_once_and_survey_frequency_declines_and_expiration_are_respected()
    {
        var (tenant, store, clock) = await Setup(); clock.Now = DateTimeOffset.UtcNow;
        var rule = new RuleInput(Guid.NewGuid(), "Automatic tracking", null, null, null, 1, "high", null, null, Enabled: true);
        await store.SaveRuleAsync(tenant, "test", rule, ct);
        var feedback = await Feedback(tenant, clock.Now, "generic");
        await store.TickAsync(ct, tenant); await store.TickAsync(ct, tenant);
        await using var db = await fixture.DataSource.OpenAsync(ct);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM cx_automation_execution WHERE tenant_id=@tenant AND feedback_id=@feedback", new { tenant, feedback })).Should().Be(1);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM cx_case WHERE tenant_id=@tenant", new { tenant })).Should().Be(1);
        var campaign = new CampaignInput(Guid.NewGuid(), "Manual campaign", "How satisfied are you?", "csat", "manual", null, null, Enabled: true);
        await store.SaveCampaignAsync(tenant, "test", campaign, ct);
        await store.DispatchCampaignAsync(tenant, "test", campaign.Id, ["customer"], ct);
        await store.DispatchCampaignAsync(tenant, "test", campaign.Id, ["customer"], ct);
        var invitation = (await store.InvitationsAsync(tenant, "customer", ct)).Should().ContainSingle().Subject;
        await store.DeclineAsync(tenant, "customer", invitation.Id, ct);
        await FluentActions.Awaiting(() => store.AnswerAsync(tenant, "customer", invitation.Id, new(5, ""), ct)).Should().ThrowAsync<OperationsException>();
        clock.Now = clock.Now.AddDays(31);
        await store.DispatchCampaignAsync(tenant, "test", campaign.Id, ["customer"], ct);
        var newest = (await store.InvitationsAsync(tenant, "customer", ct))[0];
        clock.Now = clock.Now.AddDays(15);
        await FluentActions.Awaiting(() => store.AnswerAsync(tenant, "customer", newest.Id, new(5, ""), ct)).Should().ThrowAsync<OperationsException>();
    }
    [SkippableFact]
    public async Task Journey_funnel_uses_ordered_events_and_distinguishes_pending_from_expired_dropoff()
    {
        var (tenant, store, clock) = await Setup();
        var journey = new JourneyInput(Guid.NewGuid(), "Shopping", 7, [new("Start", ["start"]), new("Payment", ["paid"]), new("Delivery", ["delivered"])]);
        await store.SaveJourneyAsync(tenant, "test", journey, ct);
        await Event(tenant, "customer", "paid", clock.Now.AddDays(-11));
        await Event(tenant, "customer", "start", clock.Now.AddDays(-10));
        await Event(tenant, "other", "start", clock.Now.AddDays(-1));
        await Event(tenant, "other", "paid", clock.Now.AddHours(-12));
        var result = await store.FunnelAsync(tenant, journey.Id, clock.Now.AddDays(-20), clock.Now.AddDays(1), ct);
        result.Stages[0].Reached.Should().Be(2); result.Stages[0].CompletedNext.Should().Be(1);
        result.Stages[0].Dropped.Should().Be(1); result.Stages[1].Reached.Should().Be(1); result.Stages[1].Pending.Should().Be(1);
        result.Stages[2].Reached.Should().Be(0);
        (await store.JourneysAsync(tenant, ct)).Should().ContainSingle().Which.Stages.Should().HaveCount(3);
    }
    [SkippableFact]
    public async Task Sla_warning_and_breach_are_deduplicated_and_overdue_case_escalates_to_team_lead()
    {
        var (tenant, store, clock) = await Setup();
        var agent = await Staff(tenant); var lead = await Staff(tenant, "support_supervisor");
        var team = Guid.NewGuid(); await store.SaveTeamAsync(tenant, "test", new(team, "Handling", lead, [agent]), ct);
        var id = await Create(store, tenant, assignee: agent, team: team);
        var item = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        clock.Now = item.ResponseDueAt.AddMinutes(-20);
        await store.TickAsync(ct, tenant); await store.TickAsync(ct, tenant);
        var notices = await store.NotificationsAsync(tenant, "staff", agent.ToString(), ct);
        notices.Count(n => n.Title == "The case is close to the deadline").Should().Be(1);
        var warning = notices.Single(n => n.Title == "The case is close to the deadline");
        await store.ReadNotificationAsync(tenant, "staff", lead.ToString(), warning.Id, ct);
        (await store.NotificationsAsync(tenant, "staff", agent.ToString(), ct)).Single(n => n.Id == warning.Id).ReadAt.Should().BeNull();
        clock.Now = item.ResponseDueAt.AddMinutes(1);
        await store.TickAsync(ct, tenant); await store.TickAsync(ct, tenant);
        item = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        item.AssigneeId.Should().Be(lead);
        (await store.NotificationsAsync(tenant, "staff", lead.ToString(), ct)).Count(n => n.Title == "The case passed the deadline").Should().Be(1);
        item.Version.Should().Be(2);
    }
    [SkippableFact]
    public async Task Event_campaign_records_skipped_events_and_sends_a_single_reminder()
    {
        var (tenant, store, clock) = await Setup(); clock.Now = DateTimeOffset.UtcNow.AddMinutes(1);
        var campaign = new CampaignInput(Guid.NewGuid(), "Purchase satisfaction", "How was the shopping experience?", "ces", "event", "paid", null, Enabled: true);
        await store.SaveCampaignAsync(tenant, "test", campaign, ct);
        await Event(tenant, "customer", "paid", clock.Now.AddSeconds(-20));
        await Event(tenant, "customer", "paid", clock.Now.AddSeconds(-10));
        await store.TickAsync(ct, tenant); await store.TickAsync(ct, tenant);
        var invitation = (await store.InvitationsAsync(tenant, "customer", ct)).Should().ContainSingle().Subject;
        await using var db = await fixture.DataSource.OpenAsync(ct);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM cx_campaign_event WHERE tenant_id=@tenant", new { tenant })).Should().Be(2);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM cx_campaign_event WHERE tenant_id=@tenant AND invited", new { tenant })).Should().Be(1);
        clock.Now = clock.Now.AddDays(4);
        await store.TickAsync(ct, tenant); await store.TickAsync(ct, tenant);
        (await store.NotificationsAsync(tenant, "customer", "customer", ct)).Count(n => n.Title.StartsWith("Reminder:")).Should().Be(1);
        (await store.InvitationsAsync(tenant, "customer", ct)).Should().ContainSingle();
        await store.AnswerAsync(tenant, "customer", invitation.Id, new(4, "Well"), ct);
        (await db.ExecuteScalarAsync<int>("SELECT ces_score FROM feedback WHERE tenant_id=@tenant AND provider='cx-survey-campaign'", new { tenant })).Should().Be(4);
    }
    [SkippableFact]
    public async Task A_rule_does_not_overwrite_a_human_assignment_or_priority_change()
    {
        var (tenant, store, clock) = await Setup(); clock.Now = DateTimeOffset.UtcNow;
        await store.SaveRuleAsync(tenant, "test", new(Guid.NewGuid(), "Follow-up", null, null, null, 1, "high", null, null, Enabled: true), ct);
        var feedback = await Feedback(tenant, clock.Now, "generic");
        var id = await store.CreateAsync(tenant, "automation", new(Guid.NewGuid(), "customer", "Follow-up", "Description", FeedbackId: feedback), ct);
        var item = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        await store.UpdateAsync(tenant, "human-agent", id, Update(item, "new") with { Priority="critical" }, ct);
        await store.TickAsync(ct, tenant);
        (await store.DetailAsync(tenant, id, null, ct))!.Case.Priority.Should().Be("critical");
    }
    [SkippableFact]
    public async Task Portal_cases_are_paginated_and_private_uploads_do_not_count_as_first_response()
    {
        var (tenant, store, clock) = await Setup();
        var id = await Create(store, tenant);
        clock.Now = clock.Now.AddMinutes(15);
        await store.SaveAttachmentAsync(tenant, "test", id, new() { Id=Guid.NewGuid(), Filename="internal.txt", ObjectKey="internal", ContentType="text/plain", SizeBytes=1, Public=false }, null, ct);
        (await store.DetailAsync(tenant, id, null, ct))!.Case.FirstResponseAt.Should().BeNull();
        await store.SaveAttachmentAsync(tenant, "test", id, new() { Id=Guid.NewGuid(), Filename="answer.txt", ObjectKey="answer", ContentType="text/plain", SizeBytes=1, Public=true }, null, ct);
        (await store.DetailAsync(tenant, id, null, ct))!.Case.ResponseBusinessMinutes.Should().Be(15);
        var waiting = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        await store.UpdateAsync(tenant, "test", id, Update(waiting, "waiting_customer"), ct);
        clock.Now = clock.Now.AddMinutes(30);
        await store.SaveAttachmentAsync(tenant, OperationsStore.CustomerActor("customer"), id,
            new() { Id=Guid.NewGuid(), Filename="customer.txt", ObjectKey="customer", ContentType="text/plain", SizeBytes=1, Public=true }, "customer", ct);
        var resumed = (await store.DetailAsync(tenant, id, null, ct))!.Case;
        resumed.Status.Should().Be("in_progress"); resumed.PauseStartedAt.Should().BeNull(); resumed.PausedMinutes.Should().Be(30);
        for (var i=0;i<20;i++) await Create(store, tenant);
        var first = await store.PortalCasesAsync(tenant, "customer", 1, ct);
        var second = await store.PortalCasesAsync(tenant, "customer", 2, ct);
        first.Total.Should().Be(21); first.Items.Should().HaveCount(20); second.Items.Should().ContainSingle();
        first.Items.Select(c=>c.Id).Intersect(second.Items.Select(c=>c.Id)).Should().BeEmpty();
        (await store.PortalCasesAsync(tenant, "other", 1, ct)).Items.Should().BeEmpty();
    }
    private async Task Event(string tenant, string customer, string name, DateTimeOffset at)
    {
        await new EventRepository(fixture.DataSource).PersistWithOutboxAsync(new CxEvent { EventId = Guid.NewGuid().ToString(), TenantId = tenant,
            CustomerId = customer, EventName = name, EventCategory = "business", SchemaVersion = "1.0", OccurredAt = at, ReceivedAt = at, Payload = "{}" },
            _ => new OutboxEvent { Id = Guid.NewGuid(), AggregateType = "test", AggregateId = "test", EventType = name, Topic = "test", PartitionKey = tenant, Payload = "{}" }, ct);
    }
}
