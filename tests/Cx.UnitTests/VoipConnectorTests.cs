using System.Net;
using Cx.Application.Abstractions;
using Cx.Application.Common;
using Cx.Integrations.Voip;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cx.UnitTests;

public class VoipConnectorTests
{
    private sealed class StubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private static HttpResponseMessage Audio() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpVoipConnector Create(StubHandler handler, params string[] allowed) => new(
        new HttpClient(handler),
        Options.Create(new VoipOptions
        {
            BaseUrl = "https://pbx.example",
            AuthMode = "apikey",
            ApiKey = "voip-key",
            AllowedRecordingHosts = allowed.ToList()
        }),
        NullLogger<HttpVoipConnector>.Instance);

    private static VoipRecordingDescriptor Recording(string url) =>
        new("call-1", "rec-1", url, "audio/wav", null, null, null, null, null, null);

    [Theory]
    [InlineData("https://attacker.example/r.wav")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("file:///etc/passwd")]
    public async Task Event_url_outside_the_trusted_hosts_is_refused_without_a_request(string url)
    {
        var handler = new StubHandler();

        var act = () => Create(handler).OpenRecordingStreamAsync(Recording(url));

        (await act.Should().ThrowAsync<CxException>()).Which.Category.Should().Be(ErrorCategory.Validation);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Provider_and_allowlisted_hosts_are_fetched_with_credentials()
    {
        var handler = new StubHandler(Audio(), Audio());
        var connector = Create(handler, "media.example");

        await connector.OpenRecordingStreamAsync(Recording("https://pbx.example/rec/1.wav"));
        await connector.OpenRecordingStreamAsync(Recording("https://media.example/rec/1.wav"));

        handler.Requests.Should().HaveCount(2)
            .And.OnlyContain(r => r.Headers.GetValues("X-Api-Key").Single() == "voip-key");
    }

    [Fact]
    public async Task Redirect_to_an_untrusted_host_is_refused()
    {
        var handler = new StubHandler(Redirect("http://10.0.0.5/admin"));

        var act = () => Create(handler).OpenRecordingStreamAsync(Recording("https://pbx.example/rec/1.wav"));

        (await act.Should().ThrowAsync<CxException>()).Which.Category.Should().Be(ErrorCategory.Validation);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Trusted_redirect_is_followed_without_credentials()
    {
        var handler = new StubHandler(Redirect("https://media.example/signed/1.wav"), Audio());

        await using var stream = await Create(handler, "media.example")
            .OpenRecordingStreamAsync(Recording("https://pbx.example/rec/1.wav"));

        handler.Requests.Select(r => r.RequestUri!.Host).Should().Equal("pbx.example", "media.example");
        handler.Requests[1].Headers.Contains("X-Api-Key").Should().BeFalse();
    }
}
