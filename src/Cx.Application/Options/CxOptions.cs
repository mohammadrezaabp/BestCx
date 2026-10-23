using Cx.Contracts.Messaging;

namespace Cx.Application.Options;

public sealed class IngestionOptions
{
    public const string Section = "Ingestion";

    public string DefaultTenantId { get; set; } = "bestcx";
    public string Environment { get; set; } = "development";
    public int MaxBatchSize { get; set; } = 500;
    public int MaxEventBytes { get; set; } = 262_144;
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(5);
    public int RateLimitPermitPerMinute { get; set; } = 6000;
    public int RateLimitQueueLimit { get; set; } = 200;

    /// <summary>Never accepted in any event, at any nesting depth (technical design 17).</summary>
    public List<string> ForbiddenFieldNames { get; set; } = new()
    {
        "password", "passwd", "secret", "client_secret", "api_secret", "api_key", "apikey",
        "access_token", "refresh_token", "id_token", "authorization", "private_key",
        "cvv", "cvv2", "card_number", "pan", "otp", "pin"
    };
}

public sealed class KafkaOptions
{
    public const string Section = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";
    public string ClientId { get; set; } = "cx-platform";
    public string? SecurityProtocol { get; set; }
    public string? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
    public bool EnableIdempotence { get; set; } = true;
    public string Acks { get; set; } = "all";
    public int MessageTimeoutMs { get; set; } = 30_000;
    public bool AutoCreateTopics { get; set; } = true;
    public int DefaultPartitions { get; set; } = 6;
    public short DefaultReplicationFactor { get; set; } = 1;

    /// <summary>Topic names stay configurable so an existing naming convention can win (spec 8).</summary>
    public TopicMap Topics { get; set; } = new();

    public sealed class TopicMap
    {
        public string RawEvents { get; set; } = CxTopics.RawEvents;
        public string PersistedEvents { get; set; } = CxTopics.PersistedEvents;
        public string VoiceRecordingReady { get; set; } = CxTopics.VoiceRecordingReady;
        public string VoiceTranscriptionRequest { get; set; } = CxTopics.VoiceTranscriptionRequest;
        public string VoiceTranscriptionResult { get; set; } = CxTopics.VoiceTranscriptionResult;
        public string AiAnalysisRequest { get; set; } = CxTopics.AiAnalysisRequest;
        public string AiAnalysisResult { get; set; } = CxTopics.AiAnalysisResult;
        public string CustomerTimelineUpdated { get; set; } = CxTopics.CustomerTimelineUpdated;
        public string InvalidEvents { get; set; } = CxTopics.InvalidEvents;
        public string PersistenceDlq { get; set; } = CxTopics.PersistenceDlq;
        public string AiDlq { get; set; } = CxTopics.AiDlq;
        public string VoiceDlq { get; set; } = CxTopics.VoiceDlq;

        public IEnumerable<string> All() => new[]
        {
            RawEvents, PersistedEvents, VoiceRecordingReady, VoiceTranscriptionRequest,
            VoiceTranscriptionResult, AiAnalysisRequest, AiAnalysisResult, CustomerTimelineUpdated,
            InvalidEvents, PersistenceDlq, AiDlq, VoiceDlq
        };
    }
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=cx;Username=cx";
    public bool RunMigrationsOnStartup { get; set; } = true;
    public string MigrationsPath { get; set; } = "deploy/postgres/migrations";
    public int CommandTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Local showcase data. This is deliberately opt-in so a production database is never
/// populated with sample customers by accident.
/// </summary>
public sealed class MockDataOptions
{
    public const string Section = "MockData";

    public bool Enabled { get; set; }
    public string SeedPath { get; set; } = "deploy/postgres/mock-data.sql";
}

public sealed class ObjectStorageOptions
{
    public const string Section = "ObjectStorage";

    public string ServiceUrl { get; set; } = "http://localhost:9000";
    public string Region { get; set; } = "us-east-1";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = "cx-voice";
    public bool ForcePathStyle { get; set; } = true;
    public bool CreateBucketOnStartup { get; set; } = true;
    /// <summary>Signed URLs stay short-lived (security checklist).</summary>
    public TimeSpan SignedUrlTtl { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Public base URL used to rewrite signed URLs for browsers outside the container network.</summary>
    public string? PublicServiceUrl { get; set; }
}

public sealed class AiOptions
{
    public const string Section = "Ai";

    public string Provider { get; set; } = "openrouter";
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>
    /// Text generation for summaries, action items and follow-up drafts.
    /// Decisions and audio transcription have separate model settings.
    /// </summary>
    public string Model { get; set; } = "deepseek/deepseek-v4.1-flash";
    /// <summary>Used by OpenRouter only when the primary model is unavailable.</summary>
    public string? FallbackModel { get; set; } = "deepseek/deepseek-v4.1-flash";
    public string PromptVersion { get; set; } = "cx-prompt-v3";
    public decimal Temperature { get; set; } = 0.1m;
    public int MaxTokens { get; set; } = 1500;
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxAttempts { get; set; } = 3;
    /// <summary>Bounded JSON repair retries before the job goes to the AI DLQ (technical design 19).</summary>
    public int MaxRepairAttempts { get; set; } = 1;
    public string TaxonomyName { get; set; } = "cx-core";
    /// <summary>
    /// Model for every judgement the AI makes: topic (from the operator's tree), sentiment,
    /// intent, severity, resolution and follow-up. <see cref="Model"/> only writes text
    /// (summary, action items). Empty means: use <see cref="Model"/> for both.
    /// </summary>
    public string? DecisionModel { get; set; } = "typesafe/jev-1.13";
    /// <summary>
    /// Chat model that makes the same decisions when the decision model fails (it is a
    /// decisions-endpoint model, so OpenRouter cannot fall back on its own). The model that
    /// actually decided is stored as the analysis' decision_model.
    /// </summary>
    public string? DecisionFallbackModel { get; set; } = "deepseek/deepseek-v4-flash";
    /// <summary>OpenRouter's decisions endpoint, used by decisions models such as typesafe/jev.</summary>
    public string DecisionsUrl { get; set; } = "https://openrouter.ai/api/alpha/decisions";
    public int DecisionMaxTokens { get; set; } = 600;
    public string? HttpReferer { get; set; } = "https://connector.example.invalid";
    public string? AppTitle { get; set; } = "BestCx";
    /// <summary>Set false to run the pipeline end to end with a deterministic local stub.</summary>
    public bool Enabled { get; set; } = true;

    public AiContextOptions Context { get; set; } = new();
    public ReviewPolicyOptions Review { get; set; } = new();
}

/// <summary>Context window policy (spec 20). Configuration-driven by design.</summary>
public sealed class AiContextOptions
{
    public int PreviousEventCount { get; set; } = 20;
    public TimeSpan LookbackWindow { get; set; } = TimeSpan.FromMinutes(30);
    public bool IncludeOpenSupportCase { get; set; } = true;
    public bool IncludeLatestFeedback { get; set; } = true;
    public int MaxTranscriptChars { get; set; } = 24_000;
    public int MaxContextChars { get; set; } = 40_000;
}

/// <summary>When an AI result must be seen by a human (spec 23).</summary>
public sealed class ReviewPolicyOptions
{
    public decimal ConfidenceThreshold { get; set; } = 0.75m;
    public bool ReviewCriticalSeverity { get; set; } = true;
    public bool ReviewUnknownCategory { get; set; } = true;
    /// <summary>Random QA sampling rate, 0..1.</summary>
    public double QaSampleRate { get; set; } = 0.05;
}

public sealed class VoiceOptions
{
    public const string Section = "Voice";

    public string ObjectKeyPrefix { get; set; } = "cx";
    public string DefaultLanguage { get; set; } = "en";
    public long MaxRecordingBytes { get; set; } = 512L * 1024 * 1024;
    public int StreamBufferBytes { get; set; } = 81_920;
    public bool AutoTranscribeOnReady { get; set; } = true;
    public bool AutoAnalyzeOnTranscript { get; set; } = true;

    public SttOptions Stt { get; set; } = new();
}

public sealed class SttOptions
{
    /// <summary>
    /// "openrouter" (an audio-capable chat model through the same OpenRouter key as analysis),
    /// "deepgram" (Deepgram /listen; Model is a Deepgram model such as "nova-3"),
    /// "openai-compatible" (a Whisper-style /audio/transcriptions endpoint), or "stub"
    /// (deterministic placeholder text, for offline tests only).
    /// </summary>
    public string Engine { get; set; } = "openrouter";
    /// <summary>For "openai-compatible"; "openrouter" uses Ai:BaseUrl.</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    /// <summary>Empty means: reuse Ai:ApiKey for the "openrouter" engine.</summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>
    /// For "deepgram". Kept apart from ApiKey, which the "openrouter" engine would otherwise
    /// send to OpenRouter after an engine switch.
    /// </summary>
    public string DeepgramApiKey { get; set; } = string.Empty;
    public string DeepgramBaseUrl { get; set; } = "https://api.deepgram.com/v1";
    public string Model { get; set; } = "google/gemini-3.1-flash-lite";
    public string? FallbackModel { get; set; } = "google/gemini-2.5-flash-lite";
    public string? ModelVersion { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    /// <summary>Audio is sent inline (base64); providers cap the request at about 20 MB.</summary>
    public long MaxInlineAudioBytes { get; set; } = 18L * 1024 * 1024;
}

/// <summary>Redaction policy is configuration-driven (spec 19).</summary>
public sealed class RedactionOptions
{
    public const string Section = "Redaction";

    public bool Enabled { get; set; } = true;
    public string Mask { get; set; } = "[REDACTED:{0}]";
    public List<RedactionRule> Rules { get; set; } = new()
    {
        new RedactionRule { Name = "iran_mobile", Pattern = @"(?<![0-9])(?:\+98|0098|0)?9\d{9}(?![0-9])" },
        new RedactionRule { Name = "iran_landline", Pattern = @"(?<![0-9])0\d{2,3}[ -]?\d{8}(?![0-9])" },
        new RedactionRule { Name = "national_id", Pattern = @"(?<![0-9])\d{10}(?![0-9])" },
        new RedactionRule { Name = "card_number", Pattern = @"(?<![0-9])(?:\d{4}[ -]?){3}\d{4}(?![0-9])" },
        new RedactionRule { Name = "iban", Pattern = @"\bIR\d{24}\b" },
        new RedactionRule { Name = "email", Pattern = @"[\w\.\-\+]+@[\w\-]+\.[\w\.\-]+" },
        new RedactionRule { Name = "bearer_token", Pattern = @"(?i)\b(?:bearer|token|api[_-]?key)\b[\s:=]+\S{8,}" },
        new RedactionRule { Name = "long_digit_run", Pattern = @"(?<![0-9])\d{12,19}(?![0-9])" }
    };
}

public sealed class RedactionRule
{
    public string Name { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// "login" for database username/password sessions, "jwt" for organization
    /// SSO, "apikey" for machine access only, or "mock" for the local demo (no sign-in).
    /// API keys are accepted alongside login and jwt, for producers and integrations.
    /// </summary>
    public string Mode { get; set; } = "apikey";
    public string MockSubject { get; set; } = "demo-admin";
    public string MockTenantId { get; set; } = "bestcx";
    public string? Authority { get; set; }
    public string? Audience { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
    public string PermissionsClaim { get; set; } = "cx_permissions";
    public string RolesClaim { get; set; } = "roles";
    public string TenantClaim { get; set; } = "tenant_id";
    public List<ApiKeyDefinition> ApiKeys { get; set; } = new();
    /// <summary>Bootstrap admin accounts for the "login" mode; customer accounts live in PostgreSQL.</summary>
    public List<LoginUserDefinition> Users { get; set; } = new();
    /// <summary>Sliding session length for the "login" mode.</summary>
    public int SessionHours { get; set; } = 12;
}

public sealed class LoginUserDefinition
{
    public string Username { get; set; } = string.Empty;
    /// <summary>From secret configuration (.env); never committed.</summary>
    public string Password { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string TenantId { get; set; } = "bestcx";
    public List<string> Roles { get; set; } = new();
    public List<string> Permissions { get; set; } = new();
}

public sealed class ApiKeyDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string TenantId { get; set; } = "bestcx";
    public List<string> Roles { get; set; } = new();
    public List<string> Permissions { get; set; } = new();
}
