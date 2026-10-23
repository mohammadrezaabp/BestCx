using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cx.Application.Ingestion;

public static class NpsScoreValidation
{
    public const string Error = "nps_score must be an integer between 0 and 10";

    public static bool IsValid(JsonNode? node)
    {
        var value = JsonSerializer.SerializeToElement(node);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var score)
            && score is >= 0 and <= 10 && decimal.Truncate(score) == score;
    }
}
