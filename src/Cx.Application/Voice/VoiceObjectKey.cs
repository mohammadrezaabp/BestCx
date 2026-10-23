using System.Text.RegularExpressions;

namespace Cx.Application.Voice;

/// <summary>
/// Deterministic object key (spec 16): cx/{tenant}/{yyyy}/{mm}/{dd}/voice/{call_id}/{recording_id}.{ext}
/// The key carries no customer PII - it is visible in storage listings, logs and metrics.
/// </summary>
public static class VoiceObjectKey
{
    private static readonly Regex Unsafe = new("[^a-zA-Z0-9_.-]", RegexOptions.Compiled);

    /// <summary>
    /// A run of dots is collapsed as well as slashes. An S3 key is not a filesystem path,
    /// but keys get mirrored, synced and downloaded by tooling that does treat them as
    /// paths, and "../" belongs in none of them.
    /// </summary>
    private static readonly Regex DotRun = new(@"\.{2,}", RegexOptions.Compiled);

    public static string Build(string prefix, string tenantId, DateTimeOffset occurredAt, string callId,
        string recordingId, string? contentType, string? sourceUri = null)
    {
        var utc = occurredAt.ToUniversalTime();
        var ext = ResolveExtension(contentType, sourceUri);
        return string.Join('/',
            Sanitize(prefix),
            Sanitize(tenantId),
            utc.ToString("yyyy"),
            utc.ToString("MM"),
            utc.ToString("dd"),
            "voice",
            Sanitize(callId),
            $"{Sanitize(recordingId)}.{ext}");
    }

    public static string ArtifactKey(string objectKey, string suffix) =>
        $"{objectKey}.{Sanitize(suffix)}.json";

    internal static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";

        var cleaned = DotRun.Replace(Unsafe.Replace(value.Trim(), "_"), "_").Trim('.');
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

    internal static string ResolveExtension(string? contentType, string? sourceUri)
    {
        var fromContentType = contentType?.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/ogg" or "application/ogg" => "ogg",
            "audio/opus" => "opus",
            "audio/webm" => "webm",
            "audio/flac" or "audio/x-flac" => "flac",
            "audio/mp4" or "audio/m4a" or "audio/x-m4a" => "m4a",
            "audio/amr" => "amr",
            "audio/gsm" => "gsm",
            _ => null
        };

        if (fromContentType is not null) return fromContentType;

        if (!string.IsNullOrWhiteSpace(sourceUri))
        {
            var path = sourceUri.Split('?')[0];
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (ext.Length is > 0 and <= 5 && ext.All(char.IsAsciiLetterOrDigit)) return ext;
        }

        return "bin";
    }
}
