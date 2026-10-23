using System.Net;
using System.Text;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Application.Options;
using Cx.Contracts.Voice;
using Cx.Infrastructure.Stt;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cx.UnitTests;

public class DeepgramSpeechToTextEngineTests
{
    private const string MonoResponse = """
        {"metadata":{"model_info":{"m":{"name":"general-nova-3","version":"2026-01-21.1","arch":"nova-3"}}},
         "results":{"channels":[{"alternatives":[{"transcript":"Hello. My order arrived late.","confidence":0.97}]}],
           "utterances":[
             {"start":2.5,"end":4.0,"confidence":0.91,"channel":0,"speaker":0,"transcript":"My order arrived late."},
             {"start":0.0,"end":1.2,"confidence":0.99,"channel":0,"speaker":0,"transcript":"Hello."}]}}
        """;

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static (DeepgramSpeechToTextEngine Engine, StubHandler Handler) Create(string body,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(status, body);
        var options = Options.Create(new VoiceOptions
        {
            Stt = new SttOptions { Engine = "deepgram", DeepgramApiKey = "dg-key", Model = "nova-3" }
        });
        return (new DeepgramSpeechToTextEngine(new HttpClient(handler), options,
            NullLogger<DeepgramSpeechToTextEngine>.Instance), handler);
    }

    private static TranscriptionAudio Audio(int? channels = 1) => new()
    {
        Content = new MemoryStream(new byte[] { 1, 2, 3 }),
        ObjectKey = "cx/pv-1/rec.wav",
        ContentType = "audio/wav",
        Language = "en",
        DurationMs = 4_000,
        Channels = channels
    };

    [Fact]
    public async Task Mono_message_is_sent_with_the_token_and_parsed_into_ordered_customer_segments()
    {
        var (engine, handler) = Create(MonoResponse);

        var result = await engine.TranscribeAsync(Audio());

        handler.Request!.Headers.Authorization!.ToString().Should().Be("Token dg-key");
        var query = handler.Request.RequestUri!.Query;
        query.Should().Contain("model=nova-3").And.Contain("language=en").And.Contain("diarize=true")
            .And.Contain("utterances=true").And.NotContain("multichannel");
        handler.Request.Content!.Headers.ContentType!.MediaType.Should().Be("audio/wav");

        result.Engine.Should().Be("deepgram");
        result.ModelVersion.Should().Be("2026-01-21.1");
        result.Text.Should().Be("Hello. My order arrived late.");
        result.Segments.Select(s => s.Text).Should().Equal("Hello.", "My order arrived late.");
        result.Segments.Select(s => s.Index).Should().Equal(0, 1);
        result.Segments.Should().OnlyContain(s => s.Speaker == SpeakerLabels.Customer);
        result.Segments[1].StartMs.Should().Be(2_500);
        result.AverageConfidence.Should().Be(0.95m);
    }

    [Fact]
    public async Task Several_mono_speakers_stay_unknown_and_stereo_maps_channels_to_agent_and_customer()
    {
        const string diarized = """
            {"results":{"channels":[{"alternatives":[{"transcript":"a b"}]}],"utterances":[
              {"start":0,"end":1,"channel":0,"speaker":0,"transcript":"a"},
              {"start":1,"end":2,"channel":0,"speaker":1,"transcript":"b"}]}}
            """;
        var mono = await Create(diarized).Engine.TranscribeAsync(Audio());
        mono.Segments.Should().OnlyContain(s => s.Speaker == SpeakerLabels.Unknown);
        mono.Segments.Select(s => s.SpeakerRef).Should().Equal("speaker_0", "speaker_1");

        const string stereo = """
            {"results":{"channels":[{"alternatives":[{"transcript":"a"}]},{"alternatives":[{"transcript":"b"}]}],
             "utterances":[{"start":0,"end":1,"channel":0,"speaker":0,"transcript":"a"},
                           {"start":1,"end":2,"channel":1,"speaker":0,"transcript":"b"}]}}
            """;
        var (engine, handler) = Create(stereo);
        var call = await engine.TranscribeAsync(Audio(channels: 2));
        handler.Request!.RequestUri!.Query.Should().Contain("multichannel=true").And.NotContain("diarize");
        call.Segments.Select(s => s.Speaker).Should().Equal(SpeakerLabels.Agent, SpeakerLabels.Customer);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ErrorCategory.Auth)]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorCategory.RateLimit)]
    [InlineData(HttpStatusCode.BadGateway, ErrorCategory.Provider5xx)]
    [InlineData(HttpStatusCode.BadRequest, ErrorCategory.PermanentProvider)]
    public async Task Provider_errors_map_to_retry_categories(HttpStatusCode status, ErrorCategory expected)
    {
        var act = () => Create("{\"err_msg\":\"x\"}", status).Engine.TranscribeAsync(Audio());
        (await act.Should().ThrowAsync<CxException>()).Which.Category.Should().Be(expected);
    }
}
