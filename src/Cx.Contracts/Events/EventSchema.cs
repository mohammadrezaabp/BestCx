namespace Cx.Contracts.Events;

/// <summary>Schema version policy (spec 6): unsupported MAJOR version is rejected.</summary>
public static class EventSchema
{
    public const string CurrentVersion = "1.0";
    public const int SupportedMajor = 1;

    public static bool TryParse(string? version, out int major, out int minor)
    {
        major = 0; minor = 0;
        if (string.IsNullOrWhiteSpace(version)) return false;
        var parts = version.Split('.');
        if (parts.Length is < 1 or > 3) return false;
        if (!int.TryParse(parts[0], out major)) return false;
        if (parts.Length > 1 && !int.TryParse(parts[1], out minor)) return false;
        return true;
    }

    public static bool IsSupported(string? version) =>
        TryParse(version, out var major, out _) && major == SupportedMajor;
}
