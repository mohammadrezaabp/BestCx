using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cx.Application.Ingestion;

public static class ExperienceScoreValidation
{
    public static bool IsValid(JsonNode? node, int maximum)
    {
        var value = JsonSerializer.SerializeToElement(node);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var score)
            && score >= 1 && score <= maximum && decimal.Truncate(score) == score;
    }
}
