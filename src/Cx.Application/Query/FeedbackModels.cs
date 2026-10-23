using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Cx.Application.Query;

/// <summary>
/// Numbered pagination for operator grids, where "page 3 of 12" and a total count are
/// what people need. Keyset cursors (<see cref="Common.Page{T}"/>) remain for large feeds.
/// </summary>
public sealed class NumberedPage<T>
{
    [JsonPropertyName("items")] public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    [JsonPropertyName("total")] public long Total { get; init; }
    [JsonPropertyName("page")] public int Page { get; init; }
    [JsonPropertyName("page_size")] public int PageSize { get; init; }
    [JsonPropertyName("status_counts")] public IReadOnlyDictionary<string, long>? StatusCounts { get; init; }
}

/// <summary>Customer row for the customer list: profile plus how much feedback they gave.</summary>
public sealed class CustomerSummary
{
    [JsonPropertyName("customer_id")] public string CustomerId { get; set; } = string.Empty;
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("business_id")] public string? BusinessId { get; set; }
    [JsonPropertyName("segment")] public string? Segment { get; set; }
    [JsonPropertyName("phone")] public string? Phone { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("feedback_count")] public long FeedbackCount { get; set; }
    [JsonPropertyName("negative_count")] public long NegativeCount { get; set; }
    [JsonPropertyName("first_seen_at")] public DateTimeOffset FirstSeenAt { get; set; }
    [JsonPropertyName("last_seen_at")] public DateTimeOffset LastSeenAt { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Raw attributes JSON from the row; projected into the typed fields above.</summary>
    [JsonIgnore] public string Attributes { get; set; } = "{}";
}

/// <summary>What an operator types in when defining or editing a customer.</summary>
public sealed record CustomerProfileInput(
    string? CustomerId,
    string? DisplayName,
    string? Phone,
    string? Email,
    string? BusinessId,
    string? Segment,
    string? Notes);

/// <summary>
/// Contact details live in customer_identity.attributes; hashes in their own columns so a
/// phone or email can be matched without scanning JSON.
/// </summary>
public static partial class CustomerProfiles
{
    public const int MaxNotesLength = 2000;

    [GeneratedRegex(@"^[A-Za-z0-9._@\-]{1,128}$")]
    private static partial Regex CustomerIdPattern();

    public static bool IsValidCustomerId(string value) => CustomerIdPattern().IsMatch(value);

    /// <summary>Short, readable and collision-safe: C-7K2M9QXA.</summary>
    public static string NewCustomerId() => $"C-{Ulid.NewUlid().ToString()[^8..]}";

    public static IReadOnlyList<string> Validate(CustomerProfileInput input, bool creating)
    {
        var errors = new List<string>();

        if (creating && !string.IsNullOrWhiteSpace(input.CustomerId) && !IsValidCustomerId(input.CustomerId.Trim()))
            errors.Add("Customer ID can only contain English letters, numbers and . _ - @ be.");

        if (string.IsNullOrWhiteSpace(input.DisplayName))
            errors.Add("Customer name is required.");
        else if (input.DisplayName.Trim().Length > 256)
            errors.Add("Customer name is a maximum of 256 characters.");

        var phone = NormalizePhone(input.Phone);
        if (!string.IsNullOrWhiteSpace(input.Phone) && (phone is null || phone.Length is < 5 or > 20))
            errors.Add("The contact number is not valid.");

        if (!string.IsNullOrWhiteSpace(input.Email) && !input.Email.Contains('@'))
            errors.Add("The email is not valid.");

        if (input.Notes?.Length > MaxNotesLength)
            errors.Add("Note is a maximum of 2000 characters.");

        return errors;
    }

    /// <summary>Digits only, Unicode numerals folded to ASCII, a leading + kept.</summary>
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var builder = new StringBuilder();
        foreach (var ch in phone.Trim())
        {
            if (char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.DecimalDigitNumber)
                builder.Append((char)('0' + (int)char.GetNumericValue(ch)));
            else if (ch == '+' && builder.Length == 0) builder.Append(ch);
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    public static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    public static string? Hash(string? normalized) =>
        normalized is null
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();

    public static string BuildAttributes(string? existingJson, CustomerProfileInput input, string source)
    {
        var attributes = ParseObject(existingJson);
        Set(attributes, "phone", NormalizePhone(input.Phone));
        Set(attributes, "email", NormalizeEmail(input.Email));
        Set(attributes, "notes", string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim());
        attributes["source"] ??= source;
        return attributes.ToJsonString();
    }

    /// <summary>Projects the attributes JSON onto the summary; contact details only for PII readers.</summary>
    public static CustomerSummary Project(CustomerSummary summary, bool canSeePii)
    {
        var attributes = ParseObject(summary.Attributes);
        var phone = Text(attributes, "phone");
        var email = Text(attributes, "email");
        summary.Phone = canSeePii ? phone : MaskPhone(phone);
        summary.Email = canSeePii ? email : MaskEmail(email);
        summary.Notes = Text(attributes, "notes");
        summary.Source = Text(attributes, "source") ?? "event";
        return summary;
    }

    public static string? MaskPhone(string? phone) =>
        phone is null || phone.Length < 6 ? phone : $"{phone[..4]}***{phone[^3..]}";

    public static string? MaskEmail(string? email)
    {
        if (email is null) return null;
        var at = email.IndexOf('@');
        return at <= 1 ? "***" + email[Math.Max(at, 0)..] : $"{email[0]}***{email[at..]}";
    }

    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }

    private static void Set(JsonObject obj, string key, string? value)
    {
        if (value is null) obj.Remove(key);
        else obj[key] = value;
    }

    private static string? Text(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is JsonValue value &&
        value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}

/// <summary>Filters of the feedback grid. Every filter is optional and combinable.</summary>
public sealed class FeedbackGridQuery
{
    public required string TenantId { get; init; }
    public string? Search { get; init; }
    public string? Status { get; init; }
    public string? Sentiment { get; init; }
    public string? Severity { get; init; }
    public string? Category { get; init; }
    /// <summary>Topic codes stored as subcategory: the chosen subtopic and everything beneath it.</summary>
    public IReadOnlyList<string>? Subcategories { get; init; }
    public string? Channel { get; init; }
    /// <summary>"feedback" excludes complaints; "complaints" includes only complaints.</summary>
    public string? Inbox { get; init; }
    /// <summary>"text" or "voice".</summary>
    public string? Kind { get; init; }
    public string? CustomerId { get; init; }
    /// <summary>"analyzed", "pending" or "failed".</summary>
    public string? AnalysisState { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    /// <summary>Prompt version of the current analysis job, to find failed jobs.</summary>
    public required string PromptVersion { get; init; }
}

/// <summary>One grid row: the feedback, who gave it, and the effective AI labels.</summary>
public sealed class FeedbackGridRow
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "text";
    [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }
    [JsonPropertyName("customer_name")] public string? CustomerName { get; set; }
    [JsonPropertyName("comment")] public string? Comment { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("product_id")] public string? ProductId { get; set; }
    [JsonPropertyName("feedback_type")] public string FeedbackType { get; set; } = string.Empty;
    [JsonPropertyName("rating")] public int? Rating { get; set; }
    [JsonPropertyName("nps_score")] public int? NpsScore { get; set; }
    [JsonPropertyName("csat_score")] public int? CsatScore { get; set; }
    [JsonPropertyName("ces_score")] public int? CesScore { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("reviewed")] public bool Reviewed { get; set; }
    [JsonPropertyName("submitted_at")] public DateTimeOffset SubmittedAt { get; set; }
    [JsonPropertyName("call_id")] public string? CallId { get; set; }

    /// <summary>"analyzed", "pending" or "failed".</summary>
    [JsonPropertyName("analysis_state")] public string AnalysisState { get; set; } = "pending";
    [JsonPropertyName("analysis_id")] public Guid? AnalysisId { get; set; }
    [JsonPropertyName("analysis_provider")] public string? AnalysisProvider { get; set; }
    [JsonPropertyName("analysis_error")] public string? AnalysisError { get; set; }
    [JsonPropertyName("sentiment")] public string? Sentiment { get; set; }
    [JsonPropertyName("severity")] public string? Severity { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("subcategory")] public string? Subcategory { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("confidence")] public decimal? Confidence { get; set; }
    [JsonPropertyName("human_reviewed")] public bool HumanReviewed { get; set; }

    [JsonIgnore] public long TotalCount { get; set; }
}
