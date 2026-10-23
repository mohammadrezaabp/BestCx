using System.Text;
using System.Text.Json.Serialization;

namespace Cx.Application.Common;

/// <summary>Cursor pagination for every large list (spec 37.14).</summary>
public sealed class Page<T>
{
    [JsonPropertyName("items")] public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; init; }
    [JsonPropertyName("has_more")] public bool HasMore => NextCursor is not null;

    public static Page<T> Empty => new();

    public static Page<T> From(IReadOnlyList<T> items, int limit, Func<T, string> cursorFactory)
    {
        if (items.Count <= limit) return new Page<T> { Items = items };
        var trimmed = items.Take(limit).ToList();
        return new Page<T> { Items = trimmed, NextCursor = cursorFactory(trimmed[^1]) };
    }
}

/// <summary>
/// Opaque keyset cursor: "&lt;timestamp ticks&gt;|&lt;tiebreaker&gt;", base64url encoded.
/// Keyset beats OFFSET on large tables and stays stable while new rows arrive.
/// </summary>
public readonly record struct Cursor(DateTimeOffset Timestamp, string Tiebreaker)
{
    public string Encode()
    {
        var raw = $"{Timestamp.UtcTicks}|{Tiebreaker}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? value, out Cursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var parts = raw.Split('|', 2);
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks)) return false;
            cursor = new Cursor(new DateTimeOffset(ticks, TimeSpan.Zero), parts[1]);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
