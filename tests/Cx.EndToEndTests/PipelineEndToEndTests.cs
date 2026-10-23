using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cx.Contracts;
using Cx.Contracts.Events;
using FluentAssertions;
using Xunit;

namespace Cx.EndToEndTests;

/// <summary>
/// Drives the running stack over HTTP, the way a producer and the CX panel do
/// (spec 32 end-to-end scenarios).
///
/// These target a live deployment rather than an in-process host, because the point is to
/// prove the real path: API -> Kafka -> PostgreSQL -> outbox -> persisted topic -> AI.
/// Point CX_E2E_BASE_URL at the stack (default http://localhost:8080); the suite skips
/// itself when nothing is listening.
/// </summary>
public sealed class StackFixture : IAsyncLifetime
{
    public HttpClient Client { get; private set; } = null!;
    public bool Available { get; private set; }
    public string? SkipReason { get; private set; }

    public string ProducerKey { get; } =
        Environment.GetEnvironmentVariable("CX_E2E_PRODUCER_KEY") ?? "cx-dev-producer-key";

    public string AdminKey { get; } =
        Environment.GetEnvironmentVariable("CX_E2E_ADMIN_KEY") ?? "cx-dev-admin-key";

    public async Task InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("CX_E2E_BASE_URL") ?? "http://localhost:8080";

        // A corporate proxy in the developer's environment must not intercept localhost.
        var handler = new HttpClientHandler { UseProxy = false };
        Client = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };

        try
        {
            using var response = await Client.GetAsync("/health");
            Available = response.IsSuccessStatusCode;
            if (!Available) SkipReason = $"health check returned {(int)response.StatusCode}";
        }
        catch (Exception ex)
        {
            Available = false;
            SkipReason = $"CX platform is not reachable at {baseUrl}: {ex.Message}";
        }
    }

    public Task DisposeAsync()
    {
        Client.Dispose();
        return Task.CompletedTask;
    }

    public HttpRequestMessage Request(HttpMethod method, string path, string key)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Api-Key", key);
        return request;
    }

    /// <summary>
    /// A plain GET, retried briefly. The stack is shared and its workers are busy, so a
    /// single request that loses a race with a batch of AI jobs should not fail a test
    /// that is asserting on the endpoint's contract, not on its latency.
    /// </summary>
    public async Task<HttpResponseMessage> GetWithRetryAsync(string path, string key, int attempts = 4)
    {
        HttpResponseMessage? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            last?.Dispose();

            using var request = Request(HttpMethod.Get, path, key);
            last = await Client.SendAsync(request);

            // Only a server-side hiccup is worth retrying; 401/403/404 are the answer.
            if ((int)last.StatusCode < 500) return last;

            await Task.Delay(1000 * attempt);
        }

        return last!;
    }

    /// <summary>Polls until the pipeline catches up; async processing has no completion callback.</summary>
    public async Task<JsonElement?> WaitForAsync(string path, Func<JsonElement, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(90));

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var request = Request(HttpMethod.Get, path, AdminKey);
            using var response = await Client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (predicate(json.RootElement)) return json.RootElement.Clone();
            }

            await Task.Delay(2000);
        }

        return null;
    }
}

[CollectionDefinition(Name)]
public sealed class StackCollection : ICollectionFixture<StackFixture>
{
    public const string Name = "stack";
}

[Collection(StackCollection.Name)]
public class PipelineEndToEndTests
{
    private readonly StackFixture _stack;

    public PipelineEndToEndTests(StackFixture stack) => _stack = stack;

    private static CanonicalEvent SupportEvent(string eventId, string customerId) => new()
    {
        EventId = eventId,
        SchemaVersion = "1.0",
        EventName = EventNames.SupportTicketCreated,
        EventCategory = EventCategories.Support,
        OccurredAt = DateTimeOffset.UtcNow,
        TenantId = "bestcx",
        Customer = new CustomerRef { CustomerId = customerId, BusinessId = "b_e2e" },
        Source = new SourceRef { Type = "support", Service = "support-desk", Channel = "email" },
        Product = new ProductRef { ProductId = "payments" },
        Context = new ContextRef { CorrelationId = $"E2E-{eventId}" }
    };

    private async Task<HttpResponseMessage> PostEventAsync(CanonicalEvent evt)
    {
        using var request = _stack.Request(HttpMethod.Post, "/v1/events", _stack.ProducerKey);
        request.Content = new StringContent(CxJson.Serialize(evt), Encoding.UTF8, "application/json");
        return await _stack.Client.SendAsync(request);
    }

    [SkippableFact]
    public async Task Event_flows_from_api_to_the_customer_timeline()
    {
        Skip.IfNot(_stack.Available, _stack.SkipReason);

        var customerId = $"c_e2e_{Guid.NewGuid():N}"[..20];
        var eventId = $"e2e-{Guid.NewGuid():N}";

        var evt = SupportEvent(eventId, customerId);
        evt.Metadata["external_id"] = $"TKT-{eventId}";
        evt.Metadata["subject"] = "End-to-end testing";
        evt.Metadata["description"] = "This event is generated by the automated test.";
        evt.Metadata["status"] = "open";

        using var response = await PostEventAsync(evt);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // capture -> Kafka raw -> persistence worker -> PostgreSQL
        var stored = await _stack.WaitForAsync($"/v1/events/{eventId}",
            json => json.TryGetProperty("event_id", out var id) && id.GetString() == eventId);

        stored.Should().NotBeNull("the event must reach the source of truth");

        // persisted event -> inbox projection -> Customer 360 timeline
        var timeline = await _stack.WaitForAsync($"/v1/customers/{customerId}/timeline",
            json => json.GetProperty("items").GetArrayLength() > 0);

        timeline.Should().NotBeNull("the timeline must merge the persisted event");

        var kinds = timeline!.Value.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("kind").GetString())
            .ToList();

        kinds.Should().Contain("event");
        kinds.Should().Contain("support", "the support projection must land in the same timeline");
    }

    [SkippableFact]
    public async Task Duplicate_delivery_does_not_duplicate_the_timeline()
    {
        Skip.IfNot(_stack.Available, _stack.SkipReason);

        var customerId = $"c_dup_{Guid.NewGuid():N}"[..20];
        var eventId = $"dup-{Guid.NewGuid():N}";
        var evt = SupportEvent(eventId, customerId);
        evt.Metadata["external_id"] = $"TKT-{eventId}";

        // The producer retries the same event three times, as an at-least-once client does.
        for (var i = 0; i < 3; i++)
        {
            using var response = await PostEventAsync(evt);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        await _stack.WaitForAsync($"/v1/events/{eventId}",
            json => json.TryGetProperty("event_id", out _));

        var timeline = await _stack.WaitForAsync($"/v1/customers/{customerId}/timeline",
            json => json.GetProperty("items").GetArrayLength() > 0);

        timeline.Should().NotBeNull();

        var eventRows = timeline!.Value.GetProperty("items").EnumerateArray()
            .Count(i => i.GetProperty("kind").GetString() == "event");

        eventRows.Should().Be(1, "duplicate delivery must not duplicate business state");
    }

    [SkippableFact]
    public async Task Invalid_event_is_rejected_and_never_persisted()
    {
        Skip.IfNot(_stack.Available, _stack.SkipReason);

        var eventId = $"bad-{Guid.NewGuid():N}";

        using var request = _stack.Request(HttpMethod.Post, "/v1/events", _stack.ProducerKey);
        request.Content = new StringContent($$"""
            {
              "event_id": "{{eventId}}",
              "schema_version": "1.0",
              "event_name": "page.viewed",
              "event_category": "product_interaction",
              "occurred_at": "2026-09-20T10:00:00Z",
              "tenant_id": "bestcx",
              "source": { "type": "web", "service": "portal" },
              "metadata": { "access_token": "should-never-be-accepted" }
            }
            """, Encoding.UTF8, "application/json");

        using var response = await _stack.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("reason").GetString().Should().Be("forbidden_field");

        await Task.Delay(3000);

        using var stored = await _stack.GetWithRetryAsync($"/v1/events/{eventId}", _stack.AdminKey);

        stored.StatusCode.Should().Be(HttpStatusCode.NotFound, "a rejected event never reaches the store");
    }

    [SkippableFact]
    public async Task Unauthenticated_requests_are_refused()
    {
        Skip.IfNot(_stack.Available, _stack.SkipReason);

        using var response = await _stack.Client.PostAsync("/v1/events",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task A_producer_key_cannot_read_customer_data()
    {
        Skip.IfNot(_stack.Available, _stack.SkipReason);

        // Backend enforcement, not UI hiding (spec 27).
        using var response = await _stack.GetWithRetryAsync("/v1/customers", _stack.ProducerKey);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [SkippableFact]
    public async Task Batch_ingestion_reports_per_item_results()
    {
        Skip.IfNot(_stack.Available, _stack.SkipReason);

        var good = SupportEvent($"batch-ok-{Guid.NewGuid():N}", $"c_b_{Guid.NewGuid():N}"[..18]);
        good.Metadata["external_id"] = "TKT-batch-ok";

        var payload = $"[{CxJson.Serialize(good)},{{\"event_id\":\"batch-bad\",\"schema_version\":\"9.0\"}}]";

        using var request = _stack.Request(HttpMethod.Post, "/v1/events/batch", _stack.ProducerKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _stack.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // One bad item must not reject the good one.
        body.GetProperty("accepted").GetInt32().Should().Be(1);
        body.GetProperty("rejected").GetInt32().Should().Be(1);
    }

    [SkippableFact]
    public async Task Pipeline_health_is_observable()
    {
        Skip.IfNot(_stack.Available, _stack.SkipReason);

        using var response = await _stack.GetWithRetryAsync("/v1/admin/pipelines", _stack.AdminKey);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.TryGetProperty("outbox_backlog", out _).Should().BeTrue();
        body.TryGetProperty("jobs_by_state", out _).Should().BeTrue();
        body.TryGetProperty("failed_jobs", out _).Should().BeTrue();
    }

    [SkippableFact]
    public async Task Data_quality_checks_are_exposed()
    {
        Skip.IfNot(_stack.Available, _stack.SkipReason);

        using var response = await _stack.GetWithRetryAsync("/v1/admin/data-quality", _stack.AdminKey);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var checks = body.GetProperty("checks");

        checks.GetArrayLength().Should().BeGreaterThan(0);

        var keys = checks.EnumerateArray().Select(c => c.GetProperty("key").GetString()).ToList();
        keys.Should().Contain("missing_customer_id");
        keys.Should().Contain("recording_not_transcribed");
    }
}
