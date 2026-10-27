using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cx.Contracts;
using FluentAssertions;
using Xunit;

namespace Cx.EndToEndTests;

[Collection(StackCollection.Name)]
public class CustomerPortalEndToEndTests(StackFixture stack)
{
    private async Task<HttpClient> PortalClientAsync()
    {
        Skip.IfNot(stack.Available, stack.SkipReason);
        var username = Environment.GetEnvironmentVariable("CX_E2E_PORTAL_USERNAME");
        var password = Environment.GetEnvironmentVariable("CX_E2E_PORTAL_PASSWORD");
        Skip.If(string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password), "Portal test credentials are not configured.");
        var customerId = $"portal-test-{username}";
        using var staff = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = stack.Client.BaseAddress };
        staff.DefaultRequestHeaders.Add("X-Api-Key", stack.AdminKey);
        using var profile = await staff.GetAsync($"/v1/customers/{customerId}");
        if (profile.StatusCode == HttpStatusCode.NotFound)
        {
            using var created = await staff.PostAsJsonAsync("/v1/customers", new { customer_id = customerId, display_name = "Portal Test Customer" });
            created.StatusCode.Should().Be(HttpStatusCode.Created);
        }
        using var saved = await staff.PutAsJsonAsync($"/v1/customers/{customerId}/portal-account", new { username, password, enabled = true });
        saved.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = stack.Client.BaseAddress };
        using var login = await client.PostAsJsonAsync("/v1/portal/auth/login", new { username, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    [SkippableTheory]
    [InlineData("complaint", "complaint")]
    [InlineData("feedback", "generic")]
    [InlineData("voc", "voc")]
    public async Task Signed_in_submission_reaches_the_staff_inbox_and_analysis_without_duplicate_retries(string kind, string expectedType)
    {
        Skip.IfNot(stack.Available, stack.SkipReason);
        using var client = await PortalClientAsync();
        using var meResponse = await client.GetAsync("/v1/portal/auth/me");
        using var me = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync());
        var customerId = me.RootElement.GetProperty("customer_id").GetString();
        using var categoriesResponse = await client.GetAsync("/v1/portal/categories");
        categoriesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var catalog = JsonDocument.Parse(await categoriesResponse.Content.ReadAsStringAsync());
        var categories = catalog.RootElement.GetProperty("categories");
        Skip.If(categories.GetArrayLength() == 0, "The configured portal taxonomy has no active categories.");
        var id = Guid.NewGuid();
        var marker = $"portal-e2e-{id:N}";
        var body = new
        {
            submission_id = id, kind,
            category = categories[0].GetProperty("id").GetString(),
            revision = catalog.RootElement.GetProperty("revision").GetInt32(),
            subject = marker, comment = $"{marker}: customer service needs improvement", rating = 2,
            nps_score = kind == "voc" ? (int?)0 : null,
            ces_score = 7,
            answers = new { satisfaction = "2", effort = "7", experience = "Service arrived late", improvement = "Please improve updates" },
            customer_id = "spoofed-customer"
        };
        // Deliberately no staff key on the customer requests.
        using var accepted = await client.PostAsJsonAsync("/v1/portal/submissions", body, CxJson.Options);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var retry = await client.PostAsJsonAsync("/v1/portal/submissions", body, CxJson.Options);
        retry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var grid = await stack.WaitForAsync($"/v1/feedback/grid?q={marker}",
            json => json.GetProperty("items").GetArrayLength() == 1);
        grid.Should().NotBeNull("the persisted submission must appear once in the staff inbox");
        var row = grid!.Value.GetProperty("items")[0];
        row.GetProperty("feedback_type").GetString().Should().Be(expectedType);
        row.GetProperty("customer_id").GetString().Should().Be(customerId);
        row.GetProperty("customer_name").GetString().Should().NotBeNullOrWhiteSpace();
        var feedbackId = row.GetProperty("id").GetString();
        var detail = await stack.WaitForAsync($"/v1/feedback/{feedbackId}",
            json => json.TryGetProperty("analysis", out var analysis) && analysis.ValueKind == JsonValueKind.Object);
        detail.Should().NotBeNull("the existing persisted-event AI pipeline must process the submission");
        detail!.Value.GetProperty("submission").GetProperty("receipt").GetString().Should().Be(id.ToString());
        if (kind == "voc")
        {
            detail.Value.GetProperty("feedback").GetProperty("nps_score").GetInt32().Should().Be(0);
            detail.Value.GetProperty("feedback").GetProperty("comment").GetString()
                .Should().Contain("Service arrived late").And.Contain("Please improve updates");
        }
    }

    [SkippableFact]
    public async Task Signed_in_submission_rejects_a_stale_taxonomy()
    {
        Skip.IfNot(stack.Available, stack.SkipReason);
        using var client = await PortalClientAsync();
        using var response = await client.GetAsync("/v1/portal/categories");
        using var catalog = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var categories = catalog.RootElement.GetProperty("categories");
        Skip.If(categories.GetArrayLength() == 0, "No active portal categories.");
        var body = new
        {
            submission_id = Guid.NewGuid(), kind = "feedback",
            category = categories[0].GetProperty("id").GetString(), revision = -1,
            comment = "Taxonomy validation test"
        };
        using var stale = await client.PostAsJsonAsync("/v1/portal/submissions", body, CxJson.Options);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [SkippableFact]
    public async Task Voice_submissions_use_the_signed_in_customer_and_remain_idempotent()
    {
        using var client = await PortalClientAsync();
        using var meResponse = await client.GetAsync("/v1/portal/auth/me");
        using var me = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync());
        var customerId = me.RootElement.GetProperty("customer_id").GetString();
        using var response = await client.GetAsync("/v1/portal/categories");
        using var catalog = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var categories = catalog.RootElement.GetProperty("categories");
        Skip.If(categories.GetArrayLength() == 0, "No active portal categories.");
        var marker = $"portal-voice-e2e-{Guid.NewGuid():N}";
        var body = new { submission_id = Guid.NewGuid(), kind = "complaint", subject = marker,
            category = categories[0].GetProperty("id").GetString(),
            revision = catalog.RootElement.GetProperty("revision").GetInt32(), customer_id = "spoofed-customer" };
        // A short, silent mono PCM recording: exercises real storage without an external speech service.
        using var wav = new MemoryStream();
        using (var writer = new BinaryWriter(wav, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(3236);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(3200); writer.Write(new byte[3200]);
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(body, CxJson.Options)), "submission");
            var audio = new ByteArrayContent(wav.ToArray());
            audio.Headers.ContentType = new("audio/wav");
            form.Add(audio, "audio", "message.wav");
            using var accepted = await client.PostAsync("/v1/portal/voice", form);
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
        var grid = await stack.WaitForAsync($"/v1/feedback/grid?q={marker}", json => json.GetProperty("items").GetArrayLength() == 1);
        grid.Should().NotBeNull();
        var row = grid!.Value.GetProperty("items")[0];
        row.GetProperty("customer_id").GetString().Should().Be(customerId);
        row.GetProperty("customer_name").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [SkippableFact]
    public async Task Customer_sessions_do_not_authorize_staff_APIs_and_logout_revokes_access()
    {
        using var client = await PortalClientAsync();
        using var staff = await client.GetAsync("/v1/customers");
        staff.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var logout = await client.PostAsJsonAsync("/v1/portal/auth/logout", new { });
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var me = await client.GetAsync("/v1/portal/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task Database_password_reset_and_disable_revoke_existing_customer_sessions()
    {
        using var client = await PortalClientAsync();
        var username = Environment.GetEnvironmentVariable("CX_E2E_PORTAL_USERNAME")!;
        var customerId = $"portal-test-{username}";
        using var staff = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = stack.Client.BaseAddress };
        staff.DefaultRequestHeaders.Add("X-Api-Key", stack.AdminKey);
        using var reset = await staff.PutAsJsonAsync($"/v1/customers/{customerId}/portal-account",
            new { username, password = "reset-password123", enabled = true });
        reset.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var expired = await client.GetAsync("/v1/portal/auth/me");
        expired.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var login = await client.PostAsJsonAsync("/v1/portal/auth/login", new { username, password = "reset-password123" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var disabled = await staff.PutAsJsonAsync($"/v1/customers/{customerId}/portal-account", new { username, enabled = false });
        disabled.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var rejected = await client.GetAsync("/v1/portal/auth/me");
        rejected.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var blockedLogin = await client.PostAsJsonAsync("/v1/portal/auth/login", new { username, password = "reset-password123" });
        blockedLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var staffIdentity = await staff.GetAsync("/v1/auth/me");
        staffIdentity.StatusCode.Should().Be(HttpStatusCode.OK);
        using var identity = JsonDocument.Parse(await staffIdentity.Content.ReadAsStringAsync());
        var passwordLoginEnabled = identity.RootElement.GetProperty("mode").GetString() == "login";
        using var staffLogin = await client.PostAsJsonAsync("/v1/auth/login", new { username, password = "reset-password123" });
        // API-key deployments deliberately return 404 for staff password login.
        staffLogin.StatusCode.Should().Be(passwordLoginEnabled ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound);
    }

    [SkippableTheory]
    [InlineData("GET", "/v1/portal/categories")]
    [InlineData("POST", "/v1/portal/submissions")]
    [InlineData("POST", "/v1/portal/voice")]
    public async Task Anonymous_customers_cannot_read_or_submit(string method, string path)
    {
        Skip.IfNot(stack.Available, stack.SkipReason);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = stack.Client.BaseAddress };
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
