using Cx.Contracts;
using Cx.Contracts.Ai;
using Cx.Domain.Entities;
using Cx.Infrastructure.Database.Repositories;
using FluentAssertions;
using Xunit;

namespace Cx.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class AiVersioningTests
{
    private readonly PostgresFixture _fixture;

    public AiVersioningTests(PostgresFixture fixture) => _fixture = fixture;

    private AiAnalysisRepository Analyses => new(_fixture.DataSource);
    private HumanReviewRepository Reviews => new(_fixture.DataSource);

    private static AiAnalysis NewAnalysis(string entityId, string promptVersion, decimal confidence,
        string category = "authentication") => new()
    {
        TenantId = "bestcx",
        EntityType = AnalysisEntityTypes.VoiceCall,
        EntityId = entityId,
        CustomerId = "c_ver_1",
        AnalysisType = AnalysisTypes.VoiceAnalysis,
        ModelProvider = "openrouter",
        ModelName = "anthropic/claude-sonnet-4.5",
        ModelVersion = "2026-09",
        PromptVersion = promptVersion,
        TaxonomyVersion = "v1",
        InputHash = Guid.NewGuid().ToString("N"),
        Confidence = confidence,
        Status = AiAnalysisStatuses.Succeeded,
        Result = CxJson.Serialize(new AiAnalysisResult
        {
            Sentiment = Sentiments.Negative,
            Intent = "technical_support",
            Category = category,
            Severity = Severities.High,
            ResolutionStatus = ResolutionStatuses.Unresolved,
            Summary = "Summary of the analysis",
            Confidence = confidence
        })
    };

    [SkippableFact]
    public async Task Reprocessing_creates_a_new_version_and_never_edits_the_old_one()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);

        var entityId = $"call-{Guid.NewGuid():N}";

        var firstId = await Analyses.InsertAsync(NewAnalysis(entityId, "cx-prompt-v1", 0.60m));
        var secondId = await Analyses.InsertAsync(NewAnalysis(entityId, "cx-prompt-v2", 0.92m));

        secondId.Should().NotBe(firstId);

        var versions = await Analyses.GetVersionsForEntityAsync("bestcx", AnalysisEntityTypes.VoiceCall, entityId);

        versions.Should().HaveCount(2, "the earlier result is retained, not overwritten");

        var latest = versions.First();
        latest.Id.Should().Be(secondId);
        latest.SupersedesAnalysisId.Should().Be(firstId);
        latest.Status.Should().Be(AiAnalysisStatuses.Succeeded);

        var previous = versions.Single(v => v.Id == firstId);
        previous.Status.Should().Be(AiAnalysisStatuses.Superseded);
        previous.Confidence.Should().Be(0.60m, "the original numbers stay exactly as the model reported them");
    }

    [SkippableFact]
    public async Task Latest_for_entity_returns_the_newest_version()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);

        var entityId = $"call-{Guid.NewGuid():N}";
        await Analyses.InsertAsync(NewAnalysis(entityId, "cx-prompt-v1", 0.5m));
        var newest = await Analyses.InsertAsync(NewAnalysis(entityId, "cx-prompt-v2", 0.8m));

        var latest = await Analyses.GetLatestForEntityAsync("bestcx", AnalysisEntityTypes.VoiceCall, entityId);

        latest!.Id.Should().Be(newest);
    }

    [SkippableFact]
    public async Task Review_is_created_once_per_analysis()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);

        var entityId = $"call-{Guid.NewGuid():N}";
        var analysisId = await Analyses.InsertAsync(NewAnalysis(entityId, "cx-prompt-v1", 0.30m));

        var review = new HumanReview
        {
            TenantId = "bestcx",
            AnalysisId = analysisId,
            EntityType = AnalysisEntityTypes.VoiceCall,
            EntityId = entityId,
            Status = HumanReviewStatuses.Pending,
            ReviewReason = ReviewReasons.LowConfidence,
            PreviousResult = """{"category":"authentication"}"""
        };

        var firstId = await Reviews.CreateAsync(review);
        // A redelivered result must not queue the same work twice.
        var secondId = await Reviews.CreateAsync(review);

        secondId.Should().Be(firstId);
    }

    [SkippableFact]
    public async Task Human_override_is_stored_beside_the_model_result()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);

        var entityId = $"call-{Guid.NewGuid():N}";
        var analysisId = await Analyses.InsertAsync(NewAnalysis(entityId, "cx-prompt-v1", 0.40m, "billing"));

        var reviewId = await Reviews.CreateAsync(new HumanReview
        {
            TenantId = "bestcx",
            AnalysisId = analysisId,
            EntityType = AnalysisEntityTypes.VoiceCall,
            EntityId = entityId,
            Status = HumanReviewStatuses.Pending,
            ReviewReason = ReviewReasons.LowConfidence,
            PreviousResult = """{"category":"billing"}"""
        });

        await Reviews.ResolveAsync(reviewId, HumanReviewStatuses.Overridden, "analyst-1",
            """{"category":"payment"}""", "wrong category", "checked the transcript");

        var resolved = await Reviews.GetAsync("bestcx", reviewId);
        resolved!.Status.Should().Be(HumanReviewStatuses.Overridden);
        resolved.ReviewerId.Should().Be("analyst-1");
        resolved.OverrideResult.Should().Contain("payment");

        // The model's own row is untouched by the human decision.
        var analysis = await Analyses.GetAsync("bestcx", analysisId);
        analysis!.Result.Should().Contain("billing");
        analysis.Confidence.Should().Be(0.40m);
    }

    [SkippableFact]
    public async Task Review_queue_lists_pending_items_with_their_analysis_fields()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);

        var entityId = $"call-{Guid.NewGuid():N}";
        var analysisId = await Analyses.InsertAsync(NewAnalysis(entityId, "cx-prompt-v1", 0.20m));

        await Reviews.CreateAsync(new HumanReview
        {
            TenantId = "bestcx",
            AnalysisId = analysisId,
            EntityType = AnalysisEntityTypes.VoiceCall,
            EntityId = entityId,
            Status = HumanReviewStatuses.Pending,
            ReviewReason = ReviewReasons.LowConfidence
        });

        var page = await Reviews.QueryAsync(new Cx.Application.Abstractions.ReviewQuery
        {
            TenantId = "bestcx",
            Status = HumanReviewStatuses.Pending,
            Limit = 50
        });

        var item = page.Items.FirstOrDefault(i => i.EntityId == entityId);

        item.Should().NotBeNull();
        item!.Confidence.Should().Be(0.20m);
        item.Category.Should().Be("authentication");
        item.Severity.Should().Be(Severities.High);
        item.Summary.Should().Be("Summary of the analysis", "English must survive the round trip");
    }
}
