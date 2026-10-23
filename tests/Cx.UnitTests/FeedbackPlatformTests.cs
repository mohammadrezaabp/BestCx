using System.Text.Json.Nodes;
using Cx.Application.Ai;
using Cx.Application.Query;
using Cx.Infrastructure.Ai;
using Cx.Infrastructure.Stt;
using FluentAssertions;
using Xunit;

namespace Cx.UnitTests;

public class ProviderSchemaTests
{
    [Fact]
    public void Provider_schema_drops_every_keyword_gemini_rejects()
    {
        // Gemini answers 400 INVALID_ARGUMENT for these; with a fallback model configured,
        // OpenRouter then silently serves every request from the fallback instead.
        var schema = OpenRouterCompletionClient.ProviderSchema(new AiResultValidator().SchemaJson).ToJsonString();

        foreach (var keyword in new[] { "$schema", "$id", "minLength", "maxLength", "maxItems", "minimum", "maximum" })
            schema.Should().NotContain($"\"{keyword}\"");
        schema.Should().NotContain("\"null\"");
    }

    [Fact]
    public void Provider_schema_keeps_the_structure_the_model_must_follow()
    {
        var schema = OpenRouterCompletionClient.ProviderSchema(new AiResultValidator().SchemaJson).AsObject();

        schema["required"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Contain(new[] { "sentiment", "category", "severity", "summary", "confidence" });
        schema["properties"]!["subcategory"]!["type"]!.GetValue<string>().Should().Be("string");
        schema["properties"]!["severity"]!["enum"]!.AsArray().Should().HaveCount(4);
        // A property literally named "type" (key_moments) must survive keyword stripping.
        schema["properties"]!["key_moments"]!["items"]!["properties"]!["type"].Should().NotBeNull();
    }
}

public class AudioFormatTests
{
    [Theory]
    [InlineData("audio/wav", "x.bin", "wav")]
    [InlineData("audio/mpeg", "x.bin", "mp3")]
    [InlineData("audio/x-m4a", "x.bin", "m4a")]
    [InlineData("audio/ogg; codecs=opus", "x.bin", "ogg")]
    [InlineData("application/octet-stream", "cx/call/rec.mp3", "mp3")]
    [InlineData(null, "cx/call/rec.flac", "flac")]
    public void Supported_audio_maps_to_an_openrouter_format(string? contentType, string key, string expected) =>
        OpenRouterSpeechToTextEngine.ResolveFormat(contentType, key).Should().Be(expected);

    [Fact]
    public void Webm_is_not_sent_to_the_model() =>
        // The console converts browser recordings to WAV; raw WebM would be rejected upstream.
        OpenRouterSpeechToTextEngine.ResolveFormat("audio/webm", "cx/call/rec.webm").Should().BeNull();
}

public class CustomerProfileTests
{
    [Theory]
    [InlineData("0912 345 6789", "09123456789")]
    [InlineData("+98 912-345-6789", "+989123456789")]
    [InlineData("09123456789", "09123456789")]
    [InlineData("  ", null)]
    public void Phone_numbers_are_normalized_to_ascii_digits(string input, string? expected) =>
        CustomerProfiles.NormalizePhone(input).Should().Be(expected);

    [Fact]
    public void A_customer_needs_a_name()
    {
        CustomerProfiles.Validate(new CustomerProfileInput(null, " ", null, null, null, null, null), creating: true)
            .Should().NotBeEmpty();
        CustomerProfiles.Validate(new CustomerProfileInput(null, "Maryam", "0912", null, null, null, null), creating: true)
            .Should().ContainSingle("a 4-digit phone is not a phone number");
        CustomerProfiles.Validate(new CustomerProfileInput("bad id/1", "Maryam", null, null, null, null, null), creating: true)
            .Should().ContainSingle();
        CustomerProfiles.Validate(new CustomerProfileInput(null, "Maryam Ahmadi", "09123456789", "m@x.ir", null, null, null), creating: true)
            .Should().BeEmpty();
    }

    [Fact]
    public void Generated_ids_are_valid_and_unique()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => CustomerProfiles.NewCustomerId()).ToList();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().OnlyContain(id => CustomerProfiles.IsValidCustomerId(id));
    }

    [Fact]
    public void Contact_details_are_masked_without_the_pii_permission()
    {
        var attributes = CustomerProfiles.BuildAttributes(null,
            new CustomerProfileInput(null, "Maryam", "09123456789", "Maryam@Example.com", null, null, "Note"), "manual");

        var masked = CustomerProfiles.Project(new CustomerSummary { Attributes = attributes }, canSeePii: false);
        masked.Phone.Should().Be("0912***789");
        masked.Email.Should().Be("m***@example.com");
        masked.Notes.Should().Be("Note");
        masked.Source.Should().Be("manual");

        var full = CustomerProfiles.Project(new CustomerSummary { Attributes = attributes }, canSeePii: true);
        full.Phone.Should().Be("09123456789");
        full.Email.Should().Be("maryam@example.com");
    }

    [Fact]
    public void Editing_keeps_unrelated_attributes()
    {
        var existing = new JsonObject { ["source"] = "event", ["crm_ref"] = "A-1" }.ToJsonString();
        var updated = JsonNode.Parse(CustomerProfiles.BuildAttributes(existing,
            new CustomerProfileInput(null, "Maryam", null, null, null, null, null), "manual"))!;

        updated["crm_ref"]!.GetValue<string>().Should().Be("A-1");
        updated["source"]!.GetValue<string>().Should().Be("event");
        updated["phone"].Should().BeNull();
    }
}
