using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Options;
using Cx.Contracts.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Stt;

/// <summary>
/// Deterministic offline transcript generator (spec 40: "transcript worker stub").
/// It produces correctly shaped, speaker-labelled, timestamped segments so the voice
/// pipeline and the synced-transcript UI can be built and tested before an STT vendor is
/// chosen. It does NOT read the audio - the text is placeholder text, derived from the
/// object key so the same recording always yields the same transcript.
/// </summary>
public sealed class StubSpeechToTextEngine : ISpeechToTextEngine
{
    private static readonly string[] AgentLines =
    {
        "Hi, I’m BestCx Support. Come to my service.",
        "I understand. Let me check the status of your service.",
        "Yes, the error you mention is recorded in our logs.",
        "I register a ticket for this case and the technical team follows up.",
        "We will inform you of the result by the end of today.",
        "Please. Have a nice day."
    };

    private static readonly string[] CustomerLines =
    {
        "Hi, I can’t connect to the service since yesterday.",
        "I get an authentication error every time I send a request.",
        "Key API I changed it, but the problem is still the same.",
        "This has affected the payments of our customers.",
        "Please follow up faster, very February.",
        "Thank you, I look forward to your call."
    };

    public string Engine => "stub";
    public string Model => "cx-stub-stt";
    public string? ModelVersion => "v1";

    public Task<TranscriptionResult> TranscribeAsync(TranscriptionAudio audio, CancellationToken ct = default)
    {
        // Seeded by the object key so a reprocess of the same recording is reproducible.
        var seed = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(audio.ObjectKey)), 0);
        var random = new Random(seed);

        var totalMs = audio.DurationMs is > 0 ? audio.DurationMs.Value : 180_000;
        var segmentCount = Math.Clamp((int)(totalMs / 15_000), 4, 12);
        var segments = new List<TranscriptSegmentDto>(segmentCount);

        long cursor = 0;
        var step = totalMs / segmentCount;

        for (var i = 0; i < segmentCount; i++)
        {
            var isAgent = i % 2 == 0;
            var lines = isAgent ? AgentLines : CustomerLines;
            var duration = Math.Max(1200, step - random.Next(0, (int)Math.Max(1, step / 4)));

            segments.Add(new TranscriptSegmentDto
            {
                Index = i,
                Speaker = isAgent ? SpeakerLabels.Agent : SpeakerLabels.Customer,
                SpeakerRef = isAgent ? "agent" : "customer",
                StartMs = cursor,
                EndMs = cursor + duration,
                Text = lines[i / 2 % lines.Length],
                Confidence = Math.Round((decimal)(0.72 + random.NextDouble() * 0.2), 4)
            });

            cursor += step;
        }

        var result = new TranscriptionResult
        {
            Language = audio.Language ?? "en",
            Engine = Engine,
            Model = Model,
            ModelVersion = ModelVersion,
            Segments = segments,
            Text = string.Join(" ", segments.Select(s => s.Text)),
            AverageConfidence = Math.Round(segments.Average(s => s.Confidence ?? 0), 4)
        };

        return Task.FromResult(result);
    }
}

/// <summary>
/// Whisper-style STT over an OpenAI-compatible /audio/transcriptions endpoint.
/// Word/segment timestamps come from verbose_json; speaker labels are assigned by a simple
/// alternation heuristic unless the provider supplies diarization, because a guessed speaker
/// is worse than an explicit "unknown" (voice design 8).
/// </summary>
public sealed class OpenAiCompatibleSpeechToTextEngine : ISpeechToTextEngine
{
    private readonly HttpClient _http;
    private readonly SttOptions _options;
    private readonly ILogger<OpenAiCompatibleSpeechToTextEngine> _logger;

    public OpenAiCompatibleSpeechToTextEngine(HttpClient http, IOptions<VoiceOptions> options,
        ILogger<OpenAiCompatibleSpeechToTextEngine> logger)
    {
        _options = options.Value.Stt;
        _logger = logger;
        _http = http;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public string Engine => "openai-compatible";
    public string Model => _options.Model;
    public string? ModelVersion => _options.ModelVersion;

    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionAudio audio, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(audio.Content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(audio.ContentType ?? "application/octet-stream");

        form.Add(fileContent, "file", Path.GetFileName(audio.ObjectKey));
        form.Add(new StringContent(_options.Model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("segment"), "timestamp_granularities[]");
        if (!string.IsNullOrWhiteSpace(audio.Language))
            form.Add(new StringContent(audio.Language), "language");

        using var response = await _http.PostAsync("audio/transcriptions", form, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var category = (int)response.StatusCode switch
            {
                429 => ErrorCategory.RateLimit,
                401 or 403 => ErrorCategory.Auth,
                >= 500 => ErrorCategory.Provider5xx,
                _ => ErrorCategory.PermanentProvider
            };
            throw new CxException(category,
                $"STT provider error {(int)response.StatusCode}: {Truncate(payload, 400)}");
        }

        return Parse(payload, audio);
    }

    private TranscriptionResult Parse(string payload, TranscriptionAudio audio)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var text = root.TryGetProperty("text", out var textProperty) ? textProperty.GetString() ?? "" : "";
        var language = root.TryGetProperty("language", out var languageProperty)
            ? languageProperty.GetString()
            : audio.Language;

        var segments = new List<TranscriptSegmentDto>();

        if (root.TryGetProperty("segments", out var segmentArray) && segmentArray.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var segment in segmentArray.EnumerateArray())
            {
                var start = GetSeconds(segment, "start");
                var end = GetSeconds(segment, "end");
                var segmentText = segment.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? "" : "";
                if (segmentText.Length == 0) continue;

                decimal? confidence = null;
                if (segment.TryGetProperty("avg_logprob", out var logprob) &&
                    logprob.TryGetDouble(out var value))
                {
                    // avg_logprob is a log probability; map it into 0..1 for a comparable score.
                    confidence = Math.Round((decimal)Math.Clamp(Math.Exp(value), 0, 1), 4);
                }

                segments.Add(new TranscriptSegmentDto
                {
                    Index = index++,
                    Speaker = SpeakerLabels.Unknown,
                    StartMs = (long)(start * 1000),
                    EndMs = (long)(end * 1000),
                    Text = segmentText,
                    Confidence = confidence
                });
            }
        }

        if (segments.Count == 0 && text.Length > 0)
        {
            _logger.LogWarning("STT returned no segments for {Key}; storing a single unsegmented block",
                audio.ObjectKey);
            segments.Add(new TranscriptSegmentDto
            {
                Index = 0,
                Speaker = SpeakerLabels.Unknown,
                StartMs = 0,
                EndMs = audio.DurationMs ?? 0,
                Text = text
            });
        }

        AssignSpeakers(segments, audio.Channels);

        return new TranscriptionResult
        {
            Language = language,
            Engine = Engine,
            Model = Model,
            ModelVersion = ModelVersion,
            Text = text.Length > 0 ? text : string.Join(" ", segments.Select(s => s.Text)),
            Segments = segments,
            AverageConfidence = segments.Any(s => s.Confidence is not null)
                ? Math.Round(segments.Where(s => s.Confidence is not null).Average(s => s.Confidence!.Value), 4)
                : null
        };
    }

    /// <summary>
    /// Channel-separated audio gives real speaker identity. Without it the labels stay
    /// "unknown": an alternation guess would look authoritative in the review UI while
    /// being wrong whenever one party speaks twice in a row.
    /// </summary>
    private static void AssignSpeakers(List<TranscriptSegmentDto> segments, int? channels)
    {
        if (channels is not 2) return;

        for (var i = 0; i < segments.Count; i++)
        {
            segments[i].Speaker = i % 2 == 0 ? SpeakerLabels.Agent : SpeakerLabels.Customer;
            segments[i].SpeakerRef = $"channel_{i % 2}";
        }
    }

    private static double GetSeconds(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDouble(out var seconds) ? seconds : 0;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
