using System.Text.Json.Nodes;
using Cx.Application.Ingestion;
using Cx.Application.Options;
using Cx.Contracts.Events;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cx.UnitTests;

public class EventValidationTests
{
    private static readonly EventValidator Validator =
        new(Options.Create(new IngestionOptions()));

    private static JsonObject ValidEvent() => new()
    {
        ["event_id"] = "01J8YQ6J7W6B0R1S5D9V5J4A3F",
        ["schema_version"] = "1.0",
        ["event_name"] = "api.request.failed",
        ["event_category"] = "api",
        ["occurred_at"] = "2026-09-20T15:11:19.123Z",
        ["tenant_id"] = "bestcx",
        ["source"] = new JsonObject { ["type"] = "backend", ["service"] = "payment-service" }
    };

    [Fact]
    public void Accepts_a_well_formed_event()
    {
        Validator.Validate(ValidEvent()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Allows_unknown_metadata_fields()
    {
        // spec 6: unknown non-required metadata is allowed so producers can evolve
        // without waiting for a platform release.
        var evt = ValidEvent();
        evt["metadata"] = new JsonObject { ["some_future_field"] = "value", ["nested"] = new JsonObject() };

        Validator.Validate(evt).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("event_id")]
    [InlineData("event_name")]
    [InlineData("occurred_at")]
    [InlineData("tenant_id")]
    [InlineData("source")]
    public void Rejects_a_missing_required_field(string field)
    {
        var evt = ValidEvent();
        evt.Remove(field);

        var result = Validator.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.RejectReason.Should().Be(RejectReasons.SchemaInvalid);
    }

    [Fact]
    public void Rejects_an_unsupported_major_schema_version()
    {
        var evt = ValidEvent();
        evt["schema_version"] = "2.0";

        var result = Validator.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.RejectReason.Should().Be(RejectReasons.UnsupportedSchemaVersion);
    }

    [Fact]
    public void Accepts_a_newer_minor_schema_version()
    {
        // Minor versions are additive, so 1.3 must still be processed by a 1.0 platform.
        var evt = ValidEvent();
        evt["schema_version"] = "1.3";

        Validator.Validate(evt).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("api.request.failed.c_12345")] // an id smuggled into the name
    [InlineData("API.Request.Failed")]
    [InlineData("apirequestfailed")]
    [InlineData("api..failed")]
    public void Rejects_a_malformed_event_name(string eventName)
    {
        var evt = ValidEvent();
        evt["event_name"] = eventName;

        Validator.Validate(evt).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("password")]
    [InlineData("access_token")]
    [InlineData("card_number")]
    [InlineData("API_KEY")]
    public void Rejects_credential_like_fields_at_any_depth(string field)
    {
        var evt = ValidEvent();
        evt["metadata"] = new JsonObject
        {
            ["request"] = new JsonObject { ["headers"] = new JsonObject { [field] = "leaked" } }
        };

        var result = Validator.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.RejectReason.Should().Be(RejectReasons.ForbiddenField);
    }

    [Fact]
    public void Rejects_an_event_over_the_size_limit()
    {
        var options = Options.Create(new IngestionOptions { MaxEventBytes = 512 });
        var validator = new EventValidator(options);

        var evt = ValidEvent();
        evt["metadata"] = new JsonObject { ["blob"] = new string('x', 2000) };

        var result = validator.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.RejectReason.Should().Be(RejectReasons.PayloadTooLarge);
    }
}

public class EventNameTests
{
    [Fact]
    public void Every_catalog_entry_is_well_formed()
    {
        foreach (var name in EventNames.Known)
            EventNames.IsWellFormed(name).Should().BeTrue($"'{name}' is in the catalog");
    }

    [Fact]
    public void Every_catalog_entry_maps_to_a_known_category()
    {
        foreach (var name in EventNames.Known)
            EventCategories.All.Should().Contain(EventNames.CategoryOf(name)!);
    }
}

public class SchemaVersionTests
{
    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.15", true)]
    [InlineData("2.0", false)]
    [InlineData("0.9", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_supported_major_version_is_accepted(string? version, bool expected)
    {
        EventSchema.IsSupported(version).Should().Be(expected);
    }
}
