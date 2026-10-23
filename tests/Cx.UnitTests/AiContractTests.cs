using Cx.Application.Ai;
using Cx.Application.Options;
using Cx.Contracts.Ai;
using Cx.Domain.Entities;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cx.UnitTests;

public class AiResultValidatorTests
{
    private static readonly AiResultValidator Validator = new();

    private const string ValidJson = """
        {
          "sentiment": "negative",
          "intent": "technical_support",
          "category": "authentication",
          "subcategory": "invalid_credentials",
          "severity": "high",
          "journey_stage": "activation",
          "resolution_status": "unresolved",
          "summary": "The client failed authentication several times.",
          "action_items": ["Review key settings"],
          "mentioned_products": ["payments"],
          "confidence": 0.91
        }
        """;

    [Fact]
    public void Accepts_a_valid_result()
    {
        var result = Validator.Validate(ValidJson);

        result.IsValid.Should().BeTrue();
        result.Value!.Sentiment.Should().Be(Sentiments.Negative);
        result.Value.Confidence.Should().Be(0.91m);
        result.Value.Summary.Should().Contain("authentication");
    }

    [Fact]
    public void Extracts_json_from_a_fenced_code_block()
    {
        // Models frequently wrap output in prose or markdown; that should not fail a job.
        var wrapped = $"Here is the analysis:\n```json\n{ValidJson}\n```\nHope this helps.";

        Validator.Validate(wrapped).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Normalizes_confidence_reported_as_a_percentage()
    {
        var json = ValidJson.Replace("0.91", "91");

        var result = Validator.Validate(json);

        result.IsValid.Should().BeTrue();
        result.Value!.Confidence.Should().Be(0.91m);
    }

    [Fact]
    public void Normalizes_enum_casing()
    {
        var json = ValidJson.Replace("\"negative\"", "\"Negative\"").Replace("\"high\"", "\"HIGH\"");

        var result = Validator.Validate(json);

        result.IsValid.Should().BeTrue();
        result.Value!.Sentiment.Should().Be("negative");
        result.Value.Severity.Should().Be("high");
    }

    [Fact]
    public void Rejects_an_invalid_enum_value()
    {
        var json = ValidJson.Replace("\"negative\"", "\"furious\"");

        var result = Validator.Validate(json);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Rejects_a_missing_required_field()
    {
        var json = ValidJson.Replace("\"summary\": \"The client failed authentication several times.\",", "");

        Validator.Validate(json).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_non_json_output()
    {
        Validator.Validate("I cannot analyse this conversation.").IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_an_unknown_extra_field()
    {
        // additionalProperties:false keeps hallucinated fields out of the store.
        var json = ValidJson.Replace("\"confidence\": 0.91", "\"confidence\": 0.91, \"customer_name\": \"Ali\"");

        Validator.Validate(json).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Accepts_the_voice_extension()
    {
        var json = ValidJson.Replace(
            "\"confidence\": 0.91",
            """
            "confidence": 0.91,
              "call_reason": "integration_failure",
              "follow_up_required": true,
              "key_moments": [
                {"start_ms": 121000, "end_ms": 142000, "type": "problem_statement", "summary": "Description of the problem"}
              ]
            """);

        var result = Validator.Validate(json);

        result.IsValid.Should().BeTrue();
        result.Value!.KeyMoments.Should().HaveCount(1);
        result.Value.KeyMoments![0].Type.Should().Be(KeyMomentTypes.ProblemStatement);
    }
}

public class ReviewPolicyTests
{
    private static ReviewPolicy Policy(ReviewPolicyOptions options, double sample = 1.0) =>
        new(Options.Create(new AiOptions { Review = options }), () => sample);

    private static AiAnalysisResult Result(decimal confidence = 0.95m, string severity = "low",
        string category = "authentication") => new()
    {
        Sentiment = Sentiments.Neutral,
        Intent = "technical_support",
        Category = category,
        Severity = severity,
        ResolutionStatus = ResolutionStatuses.Resolved,
        Summary = "Summary",
        Confidence = confidence
    };

    private static readonly TopicCatalog Taxonomy = TopicTreeFixture.Catalog();

    [Fact]
    public void Queues_low_confidence_results()
    {
        var decision = Policy(new ReviewPolicyOptions { ConfidenceThreshold = 0.75m })
            .Evaluate(Result(confidence: 0.4m), Taxonomy);

        decision.RequiresReview.Should().BeTrue();
        decision.Reason.Should().Be(ReviewReasons.LowConfidence);
    }

    [Fact]
    public void Queues_critical_severity_even_at_high_confidence()
    {
        var decision = Policy(new ReviewPolicyOptions { QaSampleRate = 0 })
            .Evaluate(Result(confidence: 0.99m, severity: "critical"), Taxonomy);

        decision.RequiresReview.Should().BeTrue();
        decision.Reason.Should().Be(ReviewReasons.CriticalSeverity);
    }

    [Fact]
    public void Queues_a_category_outside_the_active_taxonomy()
    {
        var decision = Policy(new ReviewPolicyOptions { QaSampleRate = 0 })
            .Evaluate(Result(category: "made_up_category"), Taxonomy);

        decision.RequiresReview.Should().BeTrue();
        decision.Reason.Should().Be(ReviewReasons.UnknownCategory);
    }

    [Fact]
    public void Does_not_queue_a_confident_in_taxonomy_result()
    {
        var decision = Policy(new ReviewPolicyOptions { QaSampleRate = 0 }).Evaluate(Result(), Taxonomy);

        decision.RequiresReview.Should().BeFalse();
    }

    [Fact]
    public void Queues_a_qa_sample()
    {
        var decision = Policy(new ReviewPolicyOptions { QaSampleRate = 0.5 }, sample: 0.1)
            .Evaluate(Result(), Taxonomy);

        decision.RequiresReview.Should().BeTrue();
        decision.Reason.Should().Be(ReviewReasons.QaSample);
    }

    [Fact]
    public void Does_not_invent_review_work_when_no_topics_are_defined()
    {
        var decision = Policy(new ReviewPolicyOptions { QaSampleRate = 0 })
            .Evaluate(Result(category: "anything"), topics: TopicCatalog.Empty);

        decision.RequiresReview.Should().BeFalse();
    }

    [Fact]
    public void Queues_a_subcategory_filed_under_the_wrong_category()
    {
        var result = Result(category: "billing");
        result.Subcategory = "authentication.otp";

        var decision = Policy(new ReviewPolicyOptions { QaSampleRate = 0 }).Evaluate(result, Taxonomy);

        decision.Reason.Should().Be(ReviewReasons.UnknownCategory);
    }

    [Fact]
    public void Queues_a_topic_that_was_switched_off()
    {
        var catalog = TopicTreeFixture.Catalog(inactive: "billing");

        var decision = Policy(new ReviewPolicyOptions { QaSampleRate = 0 })
            .Evaluate(Result(category: "billing"), catalog);

        decision.Reason.Should().Be(ReviewReasons.UnknownCategory);
    }
}

internal static class TopicTreeFixture
{
    private static readonly Guid Auth = Guid.NewGuid();
    private static readonly Guid Otp = Guid.NewGuid();
    private static readonly Guid Billing = Guid.NewGuid();

    public static TopicCatalog Catalog(string? inactive = null)
    {
        var topics = new List<Topic>
        {
            new() { Id = Auth, Code = "authentication", Title = "Authentication", SortOrder = 0 },
            new()
            {
                Id = Otp, ParentId = Auth, Code = "authentication.otp", Title = "One-time use code",
                Description = "SMS confirmation code does not arrive or expires", Keywords = new[] { "The code did not come" }
            },
            new() { Id = Guid.NewGuid(), ParentId = Otp, Code = "t-sms", Title = "SMS" },
            new() { Id = Billing, Code = "billing", Title = "Finance", SortOrder = 10 },
            new() { Id = Guid.NewGuid(), ParentId = Billing, Code = "billing.refund", Title = "Refund" }
        };
        foreach (var topic in topics.Where(t => t.Code == inactive)) topic.IsActive = false;
        return new TopicCatalog(7, topics);
    }
}

public class TopicCatalogTests
{
    [Fact]
    public void Stores_the_root_as_category_and_the_chosen_node_as_subcategory()
    {
        var catalog = TopicTreeFixture.Catalog();

        catalog.Classification(catalog.Find("t-sms")!).Should().Be(("authentication", "t-sms"));
        catalog.Classification(catalog.Find("billing")!).Should().Be(("billing", (string?)null));
        catalog.TitlePath(catalog.Find("t-sms")!).Should().Be("Authentication › One-time use code › SMS");
        catalog.Version.Should().Be("rev-7");
    }

    [Fact]
    public void Switching_off_a_parent_retires_its_whole_branch()
    {
        var catalog = TopicTreeFixture.Catalog(inactive: "authentication.otp");

        catalog.IsKnown("authentication").Should().BeTrue();
        catalog.IsKnown("authentication.otp").Should().BeFalse();
        catalog.IsKnown("t-sms").Should().BeFalse();
    }

    [Fact]
    public void Renders_the_tree_with_descriptions_and_examples_for_the_decision_model()
    {
        var rendered = TopicTreeFixture.Catalog().Render();

        rendered.Should().Contain("- [authentication] Authentication\n");
        rendered.Should().Contain(
            "  - [authentication.otp] One-time use code — SMS confirmation code does not arrive or expires (examples: The code did not come)");
        rendered.Should().Contain("    - [t-sms] SMS");
        rendered.IndexOf("[billing]", StringComparison.Ordinal)
            .Should().BeGreaterThan(rendered.IndexOf("[t-sms]", StringComparison.Ordinal));
    }
}

public class DecisionMakerTests
{
    private static AiAnalysisResult TextModelOutput() => new()
    {
        Sentiment = Sentiments.Neutral,
        Intent = "information_request",
        Category = "billing",
        Severity = Severities.Low,
        ResolutionStatus = ResolutionStatuses.Unknown,
        Summary = "Summary",
        Confidence = 0.95m
    };

    [Fact]
    public void Decisions_override_whatever_the_text_model_echoed()
    {
        var catalog = TopicTreeFixture.Catalog();
        var decision = DecisionMaker.Parse("""
            {"topic":"authentication.otp","sentiment":"negative","intent":"complaint","severity":"high",
             "resolution_status":"unresolved","journey_stage":"activation","confidence":0.88,"reason":"The code did not arrive"}
            """, catalog);
        var result = TextModelOutput();

        DecisionMaker.Apply(result, decision, catalog);

        result.Category.Should().Be("authentication");
        result.Subcategory.Should().Be("authentication.otp");
        result.Sentiment.Should().Be(Sentiments.Negative);
        result.Intent.Should().Be("complaint");
        result.Severity.Should().Be(Severities.High);
        result.ResolutionStatus.Should().Be(ResolutionStatuses.Unresolved);
        result.JourneyStage.Should().Be("activation");
        result.Confidence.Should().Be(0.88m);
    }

    [Fact]
    public void An_invented_topic_code_is_unclassified_and_goes_to_review()
    {
        var catalog = TopicTreeFixture.Catalog();
        var decision = DecisionMaker.Parse(
            """{"topic":"made_up","sentiment":"negative","intent":"complaint","severity":"low","resolution_status":"unknown","confidence":0.9}""",
            catalog);
        var result = TextModelOutput();

        DecisionMaker.Apply(result, decision, catalog);

        decision.Topic.Should().BeNull();
        decision.Reason.Should().Contain("made_up");
        result.Category.Should().Be(TopicCodes.Unclassified);
        result.Subcategory.Should().BeNull();
        new ReviewPolicy(Options.Create(new AiOptions { Review = new ReviewPolicyOptions { QaSampleRate = 0 } }))
            .Evaluate(result, catalog).Reason.Should().Be(ReviewReasons.UnknownCategory);
    }

    [Fact]
    public void Values_outside_the_contract_are_dropped_not_guessed()
    {
        var decision = DecisionMaker.Parse("""
            ```json
            {"topic":"none","sentiment":"furious","intent":"rant","severity":"HIGH","resolution_status":"unknown","confidence":"85"}
            ```
            """, TopicTreeFixture.Catalog());

        decision.Topic.Should().BeNull();
        decision.Sentiment.Should().BeNull();
        decision.Intent.Should().BeNull();
        decision.Severity.Should().Be(Severities.High);
        decision.Confidence.Should().Be(0.85m);
    }

    [Fact]
    public void A_doubtful_decision_lowers_the_result_confidence()
    {
        var catalog = TopicTreeFixture.Catalog();
        var decision = DecisionMaker.Parse(
            """{"topic":"billing","sentiment":"neutral","intent":"billing_inquiry","severity":"low","resolution_status":"unknown","confidence":0.4}""",
            catalog);
        var result = TextModelOutput();

        DecisionMaker.Apply(result, decision, catalog);

        result.Confidence.Should().Be(0.4m);
    }

    [Fact]
    public void Garbage_output_decides_nothing()
    {
        var decision = DecisionMaker.Parse("I think it is about payments.", TopicTreeFixture.Catalog());

        decision.Topic.Should().BeNull();
        decision.Confidence.Should().Be(0m);
    }

    [Fact]
    public void The_decision_prompt_lists_the_tree_and_the_system_vocabulary()
    {
        var prompt = DecisionMaker.BuildSystemPrompt(TopicTreeFixture.Catalog(), isVoice: false);

        prompt.Should().Contain("[authentication.otp] One-time use code");
        prompt.Should().Contain("billing_inquiry");
        prompt.Should().Contain("churn_risk");
    }
}
