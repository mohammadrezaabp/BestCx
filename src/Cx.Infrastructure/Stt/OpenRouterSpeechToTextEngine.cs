using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Options;
using Cx.Contracts.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Stt;

/// <summary>
/// Transcription through an audio-capable chat model on OpenRouter (input_audio content).
/// OpenRouter has no /audio/transcriptions endpoint, but multimodal models such as Gemini
/// Flash transcribe English well and cost a fraction of a cent per minute - and it lets one
/// API key serve both analysis and transcription.
///
/// Segment timings come from the model and are approximate; they are clamped to the
/// recording length and forced monotonic so the synced player never jumps backwards.
/// </summary>
public sealed class OpenRouterSpeechToTextEngine : ISpeechToTextEngine
{
    private const string SystemPrompt = """
        You are a precise speech-to-text engine for customer feedback recordings.
        Write down exactly the words that are spoken, in the language and script they are spoken in.
        NEVER translate: English speech is written in English, English in English, mixed speech stays mixed.
        Never summarize, correct grammar, or invent words. Mark unclear words as […].
        Split the transcript into segments at natural pauses or speaker changes, each at most ~20 seconds.
        For each segment give start_ms and end_ms (milliseconds from the start of the audio) and the speaker:
        "customer" for the customer, "agent" for a company representative, "unknown" if you cannot tell.
        If only one person speaks, they are the customer.
        If the audio has no intelligible speech, return an empty segments array.
        Respond with ONE JSON object and nothing else:
        {"language": "<ISO 639-1 code of the spoken language>", "segments": [{"start_ms": 0, "end_ms": 0, "speaker": "customer", "text": "<the exact spoken words>"}]}
        """;

    private readonly HttpClient _http;
    private readonly SttOptions _options;
    private readonly ILogger<OpenRouterSpeechToTextEngine> _logger;

    public OpenRouterSpeechToTextEngine(HttpClient http, IOptions<VoiceOptions> voice, IOptions<AiOptions> ai,
        ILogger<OpenRouterSpeechToTextEngine> logger)
    {
        _options = voice.Value.Stt;
        _logger = logger;
        _http = http;

        var aiOptions = ai.Value;
        var apiKey = string.IsNullOrWhiteSpace(_options.ApiKey) ? aiOptions.ApiKey : _options.ApiKey;
        _http.BaseAddress = new Uri(aiOptions.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (!string.IsNullOrWhiteSpace(aiOptions.HttpReferer))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", aiOptions.HttpReferer);
        if (!string.IsNullOrWhiteSpace(aiOptions.AppTitle))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", aiOptions.AppTitle);
    }

    public string Engine => "openrouter";
    public string Model => _options.Model;
    public string? ModelVersion => _options.ModelVersion;

    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionAudio audio, CancellationToken ct = default)
    {
        var format = ResolveFormat(audio.ContentType, audio.ObjectKey)
                     ?? throw new CxException(ErrorCategory.Validation,
                         $"audio format '{audio.ContentType}' is not supported for transcription; " +
                         "upload WAV, MP3, OGG, FLAC, M4A or AAC");

        var bytes = await ReadAllAsync(audio.Content, _options.MaxInlineAudioBytes, ct);

        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["temperature"] = 0,
            ["max_tokens"] = 16000,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "text",
                            // A hint, not an instruction: a firm "language: fa" made the model
                            // translate non-English speech instead of transcribing it.
                            ["text"] = "Transcribe this recording word for word. Most recordings are " +
                                       $"'{audio.Language ?? "en"}', but keep whatever language is actually spoken." +
                                       (audio.DurationMs is > 0 ? $" Duration: {audio.DurationMs} ms." : "")
                        },
                        new JsonObject
                        {
                            ["type"] = "input_audio",
                            ["input_audio"] = new JsonObject
                            {
                                ["data"] = Convert.ToBase64String(bytes),
                                ["format"] = format
                            }
                        })
                })
        };

        if (!string.IsNullOrWhiteSpace(_options.FallbackModel) && _options.FallbackModel != _options.Model)
            body["models"] = new JsonArray(_options.Model, _options.FallbackModel);

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, ct);
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
                $"transcription provider error {(int)response.StatusCode}: {Truncate(payload, 400)}");
        }

        return Parse(payload, audio);
    }

    private TranscriptionResult Parse(string payload, TranscriptionAudio audio)
    {
        string content;
        string servedModel;
        using (var document = JsonDocument.Parse(payload))
        {
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : "unknown provider error";
                throw new CxException(ErrorCategory.PermanentProvider, $"transcription provider error: {message}");
            }

            content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            servedModel = root.TryGetProperty("model", out var model) ? model.GetString() ?? Model : Model;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(StripFence(content));
        }
        catch (JsonException ex)
        {
            throw new CxException(ErrorCategory.AiOutputInvalid,
                $"transcription model did not return JSON: {Truncate(content, 200)}", ex);
        }

        var language = node?["language"]?.GetValue<string>() ?? audio.Language;
        var limit = audio.DurationMs is > 0 ? audio.DurationMs.Value : long.MaxValue;
        var segments = new List<TranscriptSegmentDto>();
        long previousEnd = 0;

        foreach (var item in node?["segments"]?.AsArray() ?? new JsonArray())
        {
            var text = item?["text"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(text)) continue;

            var start = Math.Clamp(ReadLong(item, "start_ms") ?? previousEnd, previousEnd, limit);
            var end = Math.Clamp(ReadLong(item, "end_ms") ?? start, start, limit);
            var speaker = item?["speaker"]?.GetValue<string>();

            segments.Add(new TranscriptSegmentDto
            {
                Index = segments.Count,
                Speaker = speaker is not null && SpeakerLabels.All.Contains(speaker) ? speaker : SpeakerLabels.Unknown,
                StartMs = start,
                EndMs = end,
                Text = text
            });
            previousEnd = end;
        }

        if (segments.Count == 0)
            _logger.LogWarning("Transcription of {Key} returned no speech", audio.ObjectKey);

        return new TranscriptionResult
        {
            Language = language,
            Engine = Engine,
            Model = servedModel,
            ModelVersion = ModelVersion ?? servedModel,
            Text = string.Join(" ", segments.Select(s => s.Text)),
            Segments = segments,
            // Chat models do not report token probabilities; an invented score would mislead.
            AverageConfidence = null
        };
    }

    /// <summary>The formats OpenRouter accepts for input_audio, from the MIME type or the file extension.</summary>
    public static string? ResolveFormat(string? contentType, string objectKey)
    {
        var mime = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        var fromMime = mime switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/vnd.wave" => "wav",
            "audio/mpeg" or "audio/mp3" or "audio/mpeg3" => "mp3",
            "audio/ogg" or "audio/opus" or "audio/x-opus+ogg" => "ogg",
            "audio/flac" or "audio/x-flac" => "flac",
            "audio/mp4" or "audio/m4a" or "audio/x-m4a" => "m4a",
            "audio/aac" or "audio/x-aac" => "aac",
            "audio/aiff" or "audio/x-aiff" => "aiff",
            _ => null
        };
        if (fromMime is not null) return fromMime;

        return Path.GetExtension(objectKey).TrimStart('.').ToLowerInvariant() switch
        {
            "wav" => "wav",
            "mp3" => "mp3",
            "ogg" or "oga" or "opus" => "ogg",
            "flac" => "flac",
            "m4a" or "mp4" => "m4a",
            "aac" => "aac",
            "aif" or "aiff" => "aiff",
            _ => null
        };
    }

    private static async Task<byte[]> ReadAllAsync(Stream content, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new CxException(ErrorCategory.Validation,
                    $"recording is larger than {maxBytes / (1024 * 1024)} MB, the inline transcription limit");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static long? ReadLong(JsonNode? item, string name)
    {
        if (item?[name] is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var d)) return (long)Math.Round(d);
        return null;
    }

    private static string StripFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstLine = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine > 0 && lastFence > firstLine ? trimmed[(firstLine + 1)..lastFence].Trim() : trimmed;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
