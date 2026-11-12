using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Cinder.Sidecar.Tests;

public sealed class SidecarJsonTests
{
    private static JsonNode? Field(string json) => JsonNode.Parse($"{{\"v\": {json}}}")!["v"];

    [Theory]
    [InlineData("\"text\"", "text")]
    [InlineData("\"\"", "")]
    [InlineData("42", "42")]
    [InlineData("true", "true")]
    [InlineData("null", null)]
    [InlineData("[1,2]", null)]
    [InlineData("{\"a\":1}", null)]
    public void String_never_throws_on_a_field_of_the_wrong_kind(string json, string? expected)
        => SidecarJson.String(Field(json)).Should().Be(expected);

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("-7", -7L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("12.0", 12L)]
    [InlineData("1.2e1", 12L)]
    [InlineData("\" 15 \"", 15L)]
    public void Int64_reads_integers_however_they_are_written(string json, long expected)
        => SidecarJson.Int64(Field(json)).Should().Be(expected);

    [Theory]
    [InlineData("null")]
    [InlineData("12.5")]
    [InlineData("1e30")]
    [InlineData("9223372036854775808")]
    [InlineData("\"many\"")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("[1]")]
    public void Int64_is_null_for_anything_that_is_not_an_integer_that_fits(string json)
        => SidecarJson.Int64(Field(json)).Should().BeNull();

    [Fact]
    public void Int32_refuses_to_truncate()
    {
        SidecarJson.Int32(Field("2147483647")).Should().Be(int.MaxValue);
        SidecarJson.Int32(Field("2147483648")).Should().BeNull("a wrapped or clamped number is an invented number");
        SidecarJson.Int32(Field("-2147483649")).Should().BeNull();
    }

    [Fact]
    public void Values_built_in_code_are_read_like_parsed_ones()
    {
        var built = new JsonObject { ["i"] = 5, ["l"] = 6L, ["s"] = "x", ["b"] = false, ["d"] = 2.5 };

        SidecarJson.Int32(built["i"]).Should().Be(5);
        SidecarJson.Int64(built["l"]).Should().Be(6);
        SidecarJson.String(built["s"]).Should().Be("x");
        SidecarJson.Boolean(built["b"]).Should().BeFalse();
        SidecarJson.Double(built["d"]).Should().Be(2.5);
        SidecarJson.Int32(built["missing"]).Should().BeNull();
    }

    [Fact]
    public void Boolean_is_only_a_json_boolean()
    {
        SidecarJson.Boolean(Field("true")).Should().BeTrue();
        SidecarJson.Boolean(Field("false")).Should().BeFalse();
        SidecarJson.Boolean(Field("1")).Should().BeNull();
        SidecarJson.Boolean(Field("\"true\"")).Should().BeNull();
        SidecarJson.Boolean(Field("null")).Should().BeNull();
    }

    [Fact]
    public void A_time_with_an_offset_is_an_instant_in_utc()
    {
        var time = SidecarJson.Time(Field("\"2021-03-04T05:06:07.123456+02:00\""));

        time.Should().NotBeNull();
        time!.Value.ZoneKnown.Should().BeTrue();
        time.Value.Value.Should().Be(new DateTimeOffset(2021, 3, 4, 3, 6, 7, TimeSpan.Zero).AddTicks(1234560));
        time.Value.Value.Offset.Should().Be(TimeSpan.Zero);
        SidecarJson.Time(Field("\"2021-03-04T05:06:07Z\""))!.Value.ZoneKnown.Should().BeTrue();
    }

    [Fact]
    public void A_time_without_an_offset_keeps_its_reading_and_is_flagged_not_relabelled()
    {
        var time = SidecarJson.Time(Field("\"2021-03-04T05:06:07\""));

        time.Should().NotBeNull();
        time!.Value.ZoneKnown.Should().BeFalse("the source recorded no zone");
        // The wall-clock digits are untouched whatever zone this machine is in.
        time.Value.Value.DateTime.Should().Be(new DateTime(2021, 3, 4, 5, 6, 7));
    }

    [Fact]
    public void A_source_that_is_utc_by_definition_may_say_so()
    {
        var time = SidecarJson.Time(Field("\"2021-03-04 05:06:07.500000\""), utcByDefinition: true);

        time!.Value.ZoneKnown.Should().BeTrue();
        time.Value.Value.Should().Be(new DateTimeOffset(2021, 3, 4, 5, 6, 7, 500, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"whenever\"")]
    [InlineData("\"30000-01-01T00:00:00+00:00\"")]
    [InlineData("\"2021-13-45T99:99:99+00:00\"")]
    [InlineData("1614834367")]
    [InlineData("{}")]
    public void A_time_that_cannot_be_determined_is_null_never_a_minimum_date(string json)
    {
        SidecarJson.Time(Field(json)).Should().BeNull();
        SidecarJson.Instant(Field(json)).Should().BeNull();
    }

    [Fact]
    public void Arrays_are_read_leniently()
    {
        var node = JsonNode.Parse("""{"rows": [{"a": 1}, 5, null, {"a": 2}], "names": ["x", 3, null, ["y"]], "not": "an array"}""")!;

        SidecarJson.Objects(node["rows"]).Should().HaveCount(2);
        SidecarJson.Objects(node["not"]).Should().BeEmpty();
        SidecarJson.Objects(node["missing"]).Should().BeEmpty();
        SidecarJson.Strings(node["names"]).Should().Equal("x", "3");
        SidecarJson.Strings(node["not"]).Should().BeEmpty();
    }
}
