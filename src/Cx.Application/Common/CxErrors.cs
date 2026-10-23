namespace Cx.Application.Common;

/// <summary>Error taxonomy from spec 31. Drives retry vs DLQ decisions.</summary>
public enum ErrorCategory
{
    Validation,
    Auth,
    RateLimit,
    TransientNetwork,
    TransientDb,
    Provider5xx,
    PermanentProvider,
    Schema,
    AiOutputInvalid,
    ObjectCorrupt,
    Unknown
}

public static class ErrorCategories
{
    public static bool IsRetryable(ErrorCategory category) => category switch
    {
        ErrorCategory.RateLimit => true,
        ErrorCategory.TransientNetwork => true,
        ErrorCategory.TransientDb => true,
        ErrorCategory.Provider5xx => true,
        ErrorCategory.AiOutputInvalid => true, // bounded repair attempts, then DLQ
        ErrorCategory.Unknown => true,
        _ => false
    };
}

public class CxException : Exception
{
    public CxException(ErrorCategory category, string message, Exception? inner = null) : base(message, inner)
        => Category = category;

    public ErrorCategory Category { get; }
    public bool IsRetryable => ErrorCategories.IsRetryable(Category);
}

public sealed class ValidationFailedException : CxException
{
    public ValidationFailedException(IReadOnlyList<string> errors)
        : base(ErrorCategory.Validation, "Validation failed: " + string.Join("; ", errors))
        => Errors = errors;

    public IReadOnlyList<string> Errors { get; }
}

public sealed class AiOutputInvalidException : CxException
{
    public AiOutputInvalidException(IReadOnlyList<string> errors, string? rawOutput)
        : base(ErrorCategory.AiOutputInvalid, "AI output failed schema validation: " + string.Join("; ", errors))
    {
        Errors = errors;
        RawOutput = rawOutput;
    }

    public IReadOnlyList<string> Errors { get; }
    public string? RawOutput { get; }
}

public sealed class ObjectCorruptException : CxException
{
    public ObjectCorruptException(string message) : base(ErrorCategory.ObjectCorrupt, message) { }
}
