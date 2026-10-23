using System.Net.Http.Headers;
using System.Text.Json;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Options;
using Cx.Contracts.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cx.Infrastructure.Stt;

/// <summary>
/// Pre-recorded transcription through Deepgram's /listen endpoint. Nova-3 supports English
/// ("en"), returns utterance-level timestamps and real confidence scores, and accepts the
/// browser's own recording formats, so the audio is streamed as-is rather than base64-inlined.
///
/// Speaker labels: two-channel audio is transcribed per channel (channel 0 agent, 1 customer,
/// as in the other engines). Mono audio is diarized; a single detected speaker is the
/// customer leaving a message, several stay "unknown" with a stable speaker ref.
/// </summary>
public sealed class DeepgramSpeechToTextEngine : ISpeechToTextEngine
{
    private readonly HttpClient _http;
    private readonly SttOptions _options;
    private readonly ILogger<DeepgramSpeechToTextEngine> _logger;

    public DeepgramSpeechToTextEngine(HttpClient http, IOptions<VoiceOptions> options,
        ILogger<DeepgramSpeechToTextEngine> logger)
    {
        _options = options.Value.Stt;
        _logger = logger;
        _http = http;
        _http.BaseAddress = new Uri(_options.DeepgramBaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", _options.DeepgramApiKey);
    }

    public string Engine => "deepgram";
    public string Model => _options.Model;
    public string? ModelVersion => _options.ModelVersion;

    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionAudio audio, CancellationToken ct = default)
    {
        var multichannel = audio.Channels is 2;
        var query = new Dictionary<string, string>
        {
            ["model"] = _options.Model,
            ["language"] = audio.Language ?? "en",
            ["smart_format"] = "true",
            ["punctuate"] = "true",
            ["utterances"] = "true",
            [multichannel ? "multichannel" : "diarize"] = "true"
        };
        var path = "listen?" + string.Join("&",
            query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        // Streamed, not buffered: Deepgram takes the raw body, so a long call never sits in memory.
        var content = new StreamContent(audio.Content);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(audio.ContentType) ? "application/octet-stream" : audio.ContentType);

        using var response = await _http.PostAsync(path, content, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var category = (int)response.StatusCode switch
            {
                429 => ErrorCategory.RateLimit,
                401 or 402 or 403 => ErrorCategory.Auth,
                >= 500 => ErrorCategory.Provider5xx,
                _ => ErrorCategory.PermanentProvider
            };
            throw new CxException(category,
                $"Deepgram error {(int)response.StatusCode}: {Truncate(payload, 400)}");
        }

        return Parse(payload, audio, multichannel);
    }

    private TranscriptionResult Parse(string payload, TranscriptionAudio audio, bool multichannel)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var results = root.GetProperty("results");

        var channels = results.TryGetProperty("channels", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().ToList()
            : new List<JsonElement>();

        var language = channels.Select(ch => ch.TryGetProperty("detected_language", out var l) ? l.GetString() : null)
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? audio.Language;

        var segments = new List<TranscriptSegmentDto>();
        if (results.TryGetProperty("utterances", out var utterances) && utterances.ValueKind == JsonValueKind.Array)
        {
            var items = utterances.EnumerateArray()
                .Select(u => new
                {
                    Start = GetSeconds(u, "start"),
                    End = GetSeconds(u, "end"),
                    Channel = GetInt(u, "channel"),
                    Speaker = GetInt(u, "speaker"),
                    Text = u.TryGetProperty("transcript", out var t) ? t.GetString()?.Trim() ?? "" : "",
                    Confidence = u.TryGetProperty("confidence", out var conf) && conf.TryGetDouble(out var v)
                        ? Math.Round((decimal)Math.Clamp(v, 0, 1), 4)
                        : (decimal?)null
                })
                .Where(u => u.Text.Length > 0)
                .OrderBy(u => u.Start)
                .ToList();

            var speakerCount = items.Select(u => u.Speaker).Distinct().Count();

            foreach (var u in items)
            {
                string speaker;
                string? speakerRef;
                if (multichannel)
                {
                    speaker = u.Channel == 0 ? SpeakerLabels.Agent : SpeakerLabels.Customer;
                    speakerRef = $"channel_{u.Channel}";
                }
                else
                {
                    speaker = speakerCount == 1 ? SpeakerLabels.Customer : SpeakerLabels.Unknown;
                    speakerRef = speakerCount == 1 ? "customer" : $"speaker_{u.Speaker}";
                }

                segments.Add(new TranscriptSegmentDto
                {
                    Index = segments.Count,
                    Speaker = speaker,
                    SpeakerRef = speakerRef,
                    StartMs = (long)(u.Start * 1000),
                    EndMs = (long)(u.End * 1000),
                    Text = u.Text,
                    Confidence = u.Confidence
                });
            }
        }

        // Per-channel transcripts are the authoritative full text; utterances are its segmentation.
        var text = string.Join(" ", channels
            .Select(ch => ch.TryGetProperty("alternatives", out var alts) && alts.GetArrayLength() > 0 &&
                          alts[0].TryGetProperty("transcript", out var t) ? t.GetString()?.Trim() : null)
            .Where(t => !string.IsNullOrEmpty(t)));

        if (segments.Count == 0 && text.Length > 0)
        {
            _logger.LogWarning("Deepgram returned no utterances for {Key}; storing a single unsegmented block",
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

        if (segments.Count == 0)
            _logger.LogWarning("Transcription of {Key} returned no speech", audio.ObjectKey);

        string? servedVersion = null;
        if (root.TryGetProperty("metadata", out var metadata) &&
            metadata.TryGetProperty("model_info", out var modelInfo) && modelInfo.ValueKind == JsonValueKind.Object)
        {
            servedVersion = modelInfo.EnumerateObject()
                .Select(m => m.Value.TryGetProperty("version", out var ver) ? ver.GetString() : null)
                .FirstOrDefault(ver => ver is not null);
        }

        return new TranscriptionResult
        {
            Language = language,
            Engine = Engine,
            Model = Model,
            ModelVersion = ModelVersion ?? servedVersion,
            Text = text.Length > 0 ? text : string.Join(" ", segments.Select(s => s.Text)),
            Segments = segments,
            AverageConfidence = segments.Any(s => s.Confidence is not null)
                ? Math.Round(segments.Where(s => s.Confidence is not null).Average(s => s.Confidence!.Value), 4)
                : null
        };
    }

    private static double GetSeconds(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDouble(out var seconds) ? seconds : 0;

    private static int GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
