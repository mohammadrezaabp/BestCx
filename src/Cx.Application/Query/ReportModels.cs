using System.Text.Json.Serialization;

namespace Cx.Application.Query;

public sealed class ReportQuery
{
    public string TenantId { get; set; } = string.Empty;
    public string Inbox { get; set; } = "feedback";
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public string[] Topics { get; set; } = [];
    public string[] Sentiments { get; set; } = [];
    public string[] Channels { get; set; } = [];
    public string[] Severities { get; set; } = [];
    public string[] Types { get; set; } = [];
    public string[] Statuses { get; set; } = [];
    public string? Search { get; set; }
    public string? CustomerId { get; set; }
    public string Bucket { get; set; } = "day";
    public string TimeZone { get; set; } = "UTC";
}

public interface IReportRepository
{
    Task<ReportSnapshot> GetAsync(ReportQuery query, CancellationToken ct = default);
}

public sealed class ReportSnapshot
{
    [JsonPropertyName("from")] public DateTimeOffset From { get; set; }
    [JsonPropertyName("to")] public DateTimeOffset To { get; set; }
    [JsonPropertyName("summary")] public ReportMetrics Summary { get; set; } = new();
    [JsonPropertyName("csat_distribution")] public List<NamedCount> CsatDistribution { get; set; } = [];
    [JsonPropertyName("ces_distribution")] public List<NamedCount> CesDistribution { get; set; } = [];
    [JsonPropertyName("trend")] public List<ReportGroup> Trend { get; set; } = [];
    [JsonPropertyName("topics")] public List<ReportGroup> Topics { get; set; } = [];
    [JsonPropertyName("sentiments")] public List<ReportGroup> Sentiments { get; set; } = [];
    [JsonPropertyName("channels")] public List<ReportGroup> Channels { get; set; } = [];
    [JsonPropertyName("severities")] public List<ReportGroup> Severities { get; set; } = [];
    [JsonPropertyName("types")] public List<ReportGroup> Types { get; set; } = [];
    [JsonPropertyName("statuses")] public List<ReportGroup> Statuses { get; set; } = [];
    [JsonPropertyName("available_channels")] public List<string> AvailableChannels { get; set; } = [];
    [JsonPropertyName("available_topics")] public List<string> AvailableTopics { get; set; } = [];
}

public class ReportMetrics
{
    [JsonPropertyName("total")] public long Total { get; set; }
    [JsonPropertyName("customers")] public long Customers { get; set; }
    [JsonPropertyName("analyzed")] public long Analyzed { get; set; }
    [JsonPropertyName("negative")] public long Negative { get; set; }
    [JsonPropertyName("complaints")] public long Complaints { get; set; }
    [JsonPropertyName("open_high_severity")] public long OpenHighSeverity { get; set; }
    [JsonPropertyName("open")] public long Open { get; set; }
    [JsonPropertyName("resolved")] public long Resolved { get; set; }
    [JsonPropertyName("overdue")] public long Overdue { get; set; }
    [JsonPropertyName("csat_responses")] public long CsatResponses { get; set; }
    [JsonPropertyName("csat_satisfied")] public long CsatSatisfied { get; set; }
    [JsonPropertyName("csat_percent")] public decimal? CsatPercent { get; set; }
    [JsonPropertyName("ces_responses")] public long CesResponses { get; set; }
    [JsonPropertyName("ces_average")] public decimal? CesAverage { get; set; }
    [JsonPropertyName("ces_easy_percent")] public decimal? CesEasyPercent { get; set; }
    [JsonPropertyName("nps_responses")] public long NpsResponses { get; set; }
    [JsonPropertyName("nps_score")] public decimal? NpsScore { get; set; }
}

public sealed class ReportGroup : ReportMetrics
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}
