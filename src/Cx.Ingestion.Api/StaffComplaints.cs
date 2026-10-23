using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cx.Application.Ai;
using Cx.Application.Query;
using Cx.Contracts.Events;

namespace Cx.Ingestion.Api;

public sealed record StaffComplaint(
    [property: JsonPropertyName("submission_id")] Guid SubmissionId,
    [property: JsonPropertyName("customer_id")] string? CustomerId,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("comment")] string? Comment);

public static class StaffComplaints
{
    public static IReadOnlyList<string> Validate(StaffComplaint body, TopicCatalog catalog)
    {
        var errors = new List<string>();
        if (body.SubmissionId == Guid.Empty) errors.Add("Complaint registration ID is not valid.");
        if (string.IsNullOrWhiteSpace(body.CustomerId) || !CustomerProfiles.IsValidCustomerId(body.CustomerId))
            errors.Add("Select the client.");
        if (catalog.Find(body.Category)?.ParentId is null) errors.Add("Select the active subcategory of the problem.");
        if (string.IsNullOrWhiteSpace(body.Comment)) errors.Add("Enter the text of the complaint.");
        if (body.Comment?.Length > 12000) errors.Add("The text of the complaint is a maximum of 12,000 characters.");
        return errors;
    }

    public static CanonicalEvent ToEvent(StaffComplaint body, TopicCatalog catalog, string tenantId, string actor)
    {
        var topic = catalog.Find(body.Category)!;
        var title = catalog.TitlePath(topic);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{tenantId}\n{actor}\n{body.SubmissionId:N}")))[..32].ToLowerInvariant();
        return new CanonicalEvent
        {
            EventId = $"complaint-{key}", TenantId = tenantId,
            EventName = EventNames.FeedbackSubmitted, EventCategory = EventCategories.Feedback,
            OccurredAt = DateTimeOffset.UtcNow,
            Customer = new CustomerRef { CustomerId = body.CustomerId },
            Source = new SourceRef { Type = "web", Service = "cx-console", Channel = "manual" },
            Privacy = new PrivacyRef { ContainsPii = true, Classification = "confidential" },
            Metadata = new JsonObject
            {
                ["external_id"] = key, ["submission_id"] = body.SubmissionId.ToString(),
                ["submission_kind"] = "complaint", ["selected_category"] = topic.Code,
                ["category_title"] = title, ["taxonomy_revision"] = catalog.Revision,
                ["recorded_by"] = actor,
                ["comment"] = $"Type of request: Complaint\nSelected topic: {title}\n\n{body.Comment!.Trim()}"
            }
        };
    }
}
