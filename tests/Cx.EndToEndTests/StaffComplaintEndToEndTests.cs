using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Cx.EndToEndTests;

[Collection(StackCollection.Name)]
public class StaffComplaintEndToEndTests(StackFixture stack)
{
    private HttpClient Staff(string key)
    {
        Skip.IfNot(stack.Available, stack.SkipReason);
        var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = stack.Client.BaseAddress };
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private async Task<(string Customer, string Parent, string Category, int Revision)> SetupAsync(HttpClient staff)
    {
        var id = $"complaint-test-{Guid.NewGuid():N}";
        using var created = await staff.PostAsJsonAsync("/v1/customers", new { customer_id = id, display_name = "Complaint Test Customer" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var topic = await staff.PostAsJsonAsync("/v1/admin/topics", new { title = id, is_active = true, keywords = new[] { "payment" } });
        topic.StatusCode.Should().Be(HttpStatusCode.Created);
        using var node = JsonDocument.Parse(await topic.Content.ReadAsStringAsync());
        using var child = await staff.PostAsJsonAsync("/v1/admin/topics", new {
            title = "Details", parent_id = node.RootElement.GetProperty("id").GetString(), is_active = true, keywords = new[] { "payment" }
        });
        child.StatusCode.Should().Be(HttpStatusCode.Created);
        using var childNode = JsonDocument.Parse(await child.Content.ReadAsStringAsync());
        using var tree = await staff.GetAsync("/v1/admin/topics");
        using var catalog = JsonDocument.Parse(await tree.Content.ReadAsStringAsync());
        return (id, node.RootElement.GetProperty("code").GetString()!, childNode.RootElement.GetProperty("code").GetString()!,
            catalog.RootElement.GetProperty("revision").GetInt32());
    }

    [SkippableFact]
    public async Task Admin_text_complaint_is_projected_once_and_channel_filters_include_its_status_counts()
    {
        using var staff = Staff(stack.AdminKey);
        var (customer, _, category, revision) = await SetupAsync(staff);
        var marker = $"staff-complaint-{Guid.NewGuid():N}";
        var body = new { submission_id = Guid.NewGuid(), customer_id = customer, category, revision, comment = marker,
            channel = "web", rating = 5, kind = "voice" }; // Extra client fields cannot change the server's complaint type.
        for (var i = 0; i < 2; i++)
        {
            using var accepted = await staff.PostAsJsonAsync("/v1/complaints", body);
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
        var grid = await stack.WaitForAsync($"/v1/feedback/grid?q={marker}&channel=manual&kind=complaint",
            json => json.GetProperty("items").GetArrayLength() == 1);
        grid.Should().NotBeNull();
        var row = grid!.Value.GetProperty("items")[0];
        row.GetProperty("kind").GetString().Should().Be("text");
        row.GetProperty("feedback_type").GetString().Should().Be("complaint");
        row.GetProperty("channel").GetString().Should().Be("manual");
        row.GetProperty("customer_id").GetString().Should().Be(customer);
        (!row.TryGetProperty("rating", out var rating) || rating.ValueKind == JsonValueKind.Null).Should().BeTrue();
        grid.Value.GetProperty("total").GetInt32().Should().Be(1);
        grid.Value.GetProperty("status_counts").GetProperty("new").GetInt32().Should().Be(1);
        using var web = await staff.GetAsync($"/v1/feedback/grid?q={marker}&channel=web");
        using var empty = JsonDocument.Parse(await web.Content.ReadAsStringAsync());
        empty.RootElement.GetProperty("total").GetInt32().Should().Be(0);
        using var detail = await staff.GetAsync($"/v1/feedback/{row.GetProperty("id").GetString()}");
        using var record = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        record.RootElement.GetProperty("submission").GetProperty("category_title").GetString().Should().Be($"{customer} › Details");
    }

    [SkippableFact]
    public async Task Complaint_requires_admin_existing_customer_active_topic_and_text()
    {
        using var staff = Staff(stack.AdminKey);
        var (customer, parent, category, revision) = await SetupAsync(staff);
        using var anonymous = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = stack.Client.BaseAddress };
        var body = new { submission_id = Guid.NewGuid(), customer_id = customer, category, revision, comment = "test" };
        using var unauthenticated = await anonymous.PostAsJsonAsync("/v1/complaints", body);
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var producer = Staff(stack.ProducerKey);
        using var forbidden = await producer.PostAsJsonAsync("/v1/complaints", body);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var missingCustomer = await staff.PostAsJsonAsync("/v1/complaints", new { body.submission_id, customer_id = "missing-customer", category, revision, body.comment });
        missingCustomer.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var missingText = await staff.PostAsJsonAsync("/v1/complaints", new { body.submission_id, body.customer_id, category, revision, comment = " " });
        missingText.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var unknownTopic = await staff.PostAsJsonAsync("/v1/complaints", new { body.submission_id, body.customer_id, category = "unknown", revision, body.comment });
        unknownTopic.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var parentTopic = await staff.PostAsJsonAsync("/v1/complaints", new { body.submission_id, body.customer_id, category = parent, revision, body.comment });
        parentTopic.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var stale = await staff.PostAsJsonAsync("/v1/complaints", new { body.submission_id, body.customer_id, category, revision = revision - 1, body.comment });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
