using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Application.Operations;
using Cx.Application.Options;
using Cx.Application.Privacy;
using Cx.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cx.UnitTests;

public class CaseRoutingTests
{
    private static readonly Guid Technical = Guid.NewGuid(), Finance = Guid.NewGuid(), Flow = Guid.NewGuid();
    private static WorkTeam[] Teams() => [new() { Id = Technical, Name = "Technical", Description = "Fix software malfunction", Enabled = true },
        new() { Id = Finance, Name = "Finance", Description = "Refund", Enabled = true }];
    private static WorkflowDefinition Workflow() => new() { Id = Flow, Name = "Payment processing", Enabled = true,
        StagesJson = System.Text.Json.JsonSerializer.Serialize(new[] { new WorkflowStage("Technical review", Technical), new WorkflowStage("Refund", Finance) }, CxJson.Options) };

    [Fact]
    public void Routes_are_validated_against_enabled_teams_and_workflow_first_stage()
    {
        var teams = Teams(); var workflow = Workflow();
        FluentActions.Invoking(() => CaseRouter.Validate(Guid.NewGuid().ToString(), null, "", "ai", teams, [workflow])).Should().Throw<OperationsException>();
        FluentActions.Invoking(() => CaseRouter.Validate(Technical.ToString(), Guid.NewGuid().ToString(), "", "ai", teams, [workflow])).Should().Throw<OperationsException>();
        FluentActions.Invoking(() => CaseRouter.Validate(Finance.ToString(), Flow.ToString(), "", "ai", teams, [workflow])).Should().Throw<OperationsException>();
        teams[1].Enabled = false;
        FluentActions.Invoking(() => CaseRouter.Validate(Technical.ToString(), Flow.ToString(), "", "ai", teams, [workflow])).Should().Throw<OperationsException>();
    }

    [Fact]
    public async Task Decision_options_include_responsibilities_and_complaint_is_redacted()
    {
        var decisions = new FakeDecisions(true, Technical.ToString(), Flow.ToString());
        var router = Router(decisions, new FakeChat("stub"));
        var route = await router.DecideAsync(new() { Description = "Payment error for 09123456789", Topic = "payment.gateway" }, Teams(), [Workflow()], CancellationToken.None);
        route.Model.Should().Be("decision-model"); route.Workflow.Stages[0].TeamId.Should().Be(Technical);
        decisions.Request!.State.ToJsonString().Should().NotContain("09123456789");
        decisions.Request.Questions["team"].Criteria![Technical.ToString()]!.GetValue<string>().Should().Contain("Fix software malfunction");
    }

    [Fact]
    public async Task Invalid_decision_uses_validated_chat_fallback()
    {
        var decisions = new FakeDecisions(true, Guid.NewGuid().ToString(), "none");
        var chat = new FakeChat("fake") { Content = $$"""{"team_id":"{{Technical}}","workflow_id":null,"reason":"Software error checking"}""" };
        var route = await Router(decisions, chat).DecideAsync(new() { Description = "Payment error", Topic = "payment" }, Teams(), [], CancellationToken.None);
        route.Model.Should().Be("fallback-model"); route.WorkflowId.Should().BeNull();
        decisions.Request!.Questions["workflow"].Criteria!.AsObject().ContainsKey(Flow.ToString()).Should().BeFalse();
        chat.Request!.JsonSchemaName.Should().Be(CaseRouter.SchemaName);
    }

    [Fact]
    public async Task Offline_single_destination_is_explicit_and_multiple_destinations_are_not_guessed()
    {
        var router = Router(new FakeDecisions(false, "", ""), new FakeChat("stub"));
        var route = await router.DecideAsync(new(), [Teams()[0]], [], CancellationToken.None);
        route.Model.Should().BeNull(); route.Reason.Should().Contain("only the active team");
        await FluentActions.Awaiting(() => router.DecideAsync(new(), Teams(), [], CancellationToken.None)).Should().ThrowAsync<OperationsException>();
    }

    private static CaseRouter Router(IAiDecisionClient decisions, IAiCompletionClient chat) => new(decisions, chat,
        new RedactionService(Options.Create(new RedactionOptions())), Options.Create(new AiOptions { DecisionModel = "decision-model", DecisionFallbackModel = "fallback-model" }), NullLogger<CaseRouter>.Instance);
    private sealed class FakeDecisions(bool enabled, string team, string workflow) : IAiDecisionClient
    {
        public bool Enabled => enabled;
        public DecisionRequest? Request { get; private set; }
        public Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(new DecisionResponse("decision-model", new Dictionary<string, DecisionAnswer> {
                ["team"] = new("choice", team, null, null, .9m), ["workflow"] = new("choice", workflow, null, null, .9m)
            }, 10, 1));
        }
    }
    private sealed class FakeChat(string provider) : IAiCompletionClient
    {
        public string Provider => provider;
        public string DefaultModel => "fallback-model";
        public string Content { get; init; } = "{}";
        public AiCompletionRequest? Request { get; private set; }
        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<AiCompletion> CompleteJsonAsync(AiCompletionRequest request, CancellationToken ct = default)
        { Request = request; return Task.FromResult(new AiCompletion(Content, provider, DefaultModel, null, 1, 1, 1)); }
    }
}
