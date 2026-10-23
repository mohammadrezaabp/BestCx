using System.Text.Json.Serialization;

namespace Cx.Application.Query;

/// <summary>Response-based NPS; passives count in the denominator. No responses means no score.</summary>
public sealed record NpsSummary(
    [property: JsonPropertyName("promoters")] long Promoters,
    [property: JsonPropertyName("passives")] long Passives,
    [property: JsonPropertyName("detractors")] long Detractors)
{
    [JsonPropertyName("total")] public long Total => Promoters + Passives + Detractors;
    [JsonPropertyName("promoter_percent")] public decimal? PromoterPercent => Percent(Promoters);
    [JsonPropertyName("passive_percent")] public decimal? PassivePercent => Percent(Passives);
    [JsonPropertyName("detractor_percent")] public decimal? DetractorPercent => Percent(Detractors);
    [JsonPropertyName("score")] public decimal? Score => Percent(Promoters - Detractors);

    private decimal? Percent(long count) => Total == 0 ? null : Math.Round(100m * count / Total, 2);
}
