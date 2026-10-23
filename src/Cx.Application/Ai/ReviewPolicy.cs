using Cx.Application.Options;
using Cx.Contracts.Ai;
using Cx.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Cx.Application.Ai;

public sealed record ReviewDecision(bool RequiresReview, string? Reason);

public interface IReviewPolicy
{
    /// <summary>Spec 23: confidence, severity, taxonomy membership, policy and QA sampling.</summary>
    ReviewDecision Evaluate(AiAnalysisResult result, TopicCatalog? topics);
}

public sealed class ReviewPolicy : IReviewPolicy
{
    private readonly ReviewPolicyOptions _options;
    private readonly Func<double> _sampler;

    public ReviewPolicy(IOptions<AiOptions> ai, Func<double>? sampler = null)
    {
        _options = ai.Value.Review;
        _sampler = sampler ?? Random.Shared.NextDouble;
    }

    public ReviewDecision Evaluate(AiAnalysisResult result, TopicCatalog? topics)
    {
        if (result.Confidence < _options.ConfidenceThreshold)
            return new ReviewDecision(true, ReviewReasons.LowConfidence);

        if (_options.ReviewCriticalSeverity && result.Severity == Severities.Critical)
            return new ReviewDecision(true, ReviewReasons.CriticalSeverity);

        if (_options.ReviewUnknownCategory && !IsCategoryKnown(result, topics))
            return new ReviewDecision(true, ReviewReasons.UnknownCategory);

        if (_options.QaSampleRate > 0 && _sampler() < _options.QaSampleRate)
            return new ReviewDecision(true, ReviewReasons.QaSample);

        return new ReviewDecision(false, null);
    }

    private static bool IsCategoryKnown(AiAnalysisResult result, TopicCatalog? catalog)
    {
        // With no topics defined there is nothing to check against; do not manufacture
        // a review backlog out of a missing configuration.
        if (catalog is null || catalog.IsEmpty) return true;

        var category = catalog.Find(result.Category);
        if (category is null || category.ParentId is not null) return false;

        // The subcategory must sit under the category it is stored with.
        if (string.IsNullOrWhiteSpace(result.Subcategory)) return true;
        var subcategory = catalog.Find(result.Subcategory);
        return subcategory is not null && catalog.PathOf(subcategory)[0].Id == category.Id;
    }
}
