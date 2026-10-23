using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Application.Ai;
using Cx.Application.Common;
using Cx.Application.Options;
using Cx.Contracts.Ai;
using Cx.Infrastructure.Ai;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cx.UnitTests;

public class DecisionClientTests
{
    // Shape returned by OpenRouter's decisions endpoint for typesafe/jev-1.13.
    private const string JevResponse = """
        {"model":"typesafe/jev-1.13-20260917","answers":{
          "topic":{"type":"choice","choice":"authentication.otp","probabilities":{"authentication.otp":0.91},"confidence":0.9},
          "sentiment":{"type":"choice","choice":"negative","confidence":0.99},
          "intent":{"type":"choice","choice":"complaint","confidence":0.8},
          "severity":{"type":"choice","choice":"high","confidence":0.7},
          "resolution_status":{"type":"choice","choice":"unresolved","confidence":0.95},
          "journey_stage":{"type":"choice","choice":"unclear","confidence":0.5},
          "follow_up_required":{"type":"noul","noul":0.8}},
         "usage":{"input_tokens":596,"output_tokens":122},"provider":"TypeSafe"}
        """;

    [Fact]
    public void Maps_typed_answers_onto_a_decision()
    {
        var response = OpenRouterDecisionClient.Parse(JevResponse, "typesafe/jev-1.13", 12);

        var decision = DecisionMaker.FromAnswers(response, TopicTreeFixture.Catalog(), isVoice: true);

        decision.Model.Should().Be("typesafe/jev-1.13-20260917");
        decision.Topic!.Code.Should().Be("authentication.otp");
        decision.Sentiment.Should().Be(Sentiments.Negative);
        decision.Intent.Should().Be("complaint");
        decision.Severity.Should().Be(Severities.High);
        decision.ResolutionStatus.Should().Be(ResolutionStatuses.Unresolved);
        decision.JourneyStage.Should().BeNull("'unclear' means no journey stage");
        decision.FollowUpRequired.Should().BeTrue();
        decision.Confidence.Should().Be(0.7m, "the least certain of topic, sentiment and severity");
    }

    [Fact]
    public void Topic_options_carry_path_description_and_examples()
    {
        var request = DecisionMaker.BuildDecisionRequest("typesafe/jev-1.13", new AnalysisContext(),
            TopicTreeFixture.Catalog(), isVoice: false);

        var topic = request.Questions["topic"];
        topic.Type.Should().Be(DecisionQuestion.Choice);
        topic.Criteria!["authentication.otp"]!.GetValue<string>().Should()
            .Be("Authentication › One-time use code — SMS confirmation code does not arrive or expires (Example: The code did not come)");
        topic.Criteria![DecisionMaker.NoTopic].Should().NotBeNull();
        request.Questions.Should().NotContainKey("follow_up_required");
    }

    [Fact]
    public async Task Uses_the_decisions_model_when_it_answers()
    {
        var chat = new RecordingChat();
        var maker = Maker(new FakeDecisions(() => OpenRouterDecisionClient.Parse(JevResponse, "jev", 1)), chat);

        var decision = await maker.DecideAsync(new AnalysisContext(), TopicTreeFixture.Catalog());

        decision.Model.Should().StartWith("typesafe/jev");
        chat.Models.Should().BeEmpty();
    }

    [Fact]
    public async Task Falls_back_to_the_chat_model_when_the_decisions_model_fails()
    {
        var chat = new RecordingChat();
        var maker = Maker(new FakeDecisions(() =>
            throw new CxException(ErrorCategory.Provider5xx, "AI provider error 503")), chat);

        var decision = await maker.DecideAsync(new AnalysisContext(), TopicTreeFixture.Catalog());

        chat.Models.Should().Equal("deepseek/deepseek-v4-flash");
        decision.Model.Should().Be("deepseek/deepseek-v4-flash");
        decision.Topic!.Code.Should().Be("billing");
    }

    private static DecisionMaker Maker(IAiDecisionClient decisions, IAiCompletionClient chat) =>
        new(decisions, chat, Options.Create(new AiOptions
        {
            DecisionModel = "typesafe/jev-1.13",
            DecisionFallbackModel = "deepseek/deepseek-v4-flash"
        }), NullLogger<DecisionMaker>.Instance);

    private sealed class FakeDecisions(Func<DecisionResponse> answer) : IAiDecisionClient
    {
        public bool Enabled => true;
        public Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken ct = default) =>
            Task.FromResult(answer());
    }

    private sealed class RecordingChat : IAiCompletionClient
    {
        public List<string?> Models { get; } = new();
        public string Provider => "fake";
        public string DefaultModel => "google/gemini-3.1-flash-lite";

        public Task<AiCompletion> CompleteJsonAsync(AiCompletionRequest request, CancellationToken ct = default)
        {
            Models.Add(request.ModelOverride);
            var json = new JsonObject
            {
                ["topic"] = "billing", ["sentiment"] = "negative", ["intent"] = "billing_inquiry",
                ["severity"] = "medium", ["resolution_status"] = "unresolved", ["confidence"] = 0.8
            };
            return Task.FromResult(new AiCompletion(json.ToJsonString(), "openrouter",
                request.ModelOverride ?? DefaultModel, null, 0, 0, 1));
        }

        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
    }
}
