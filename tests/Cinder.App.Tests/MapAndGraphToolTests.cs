using System.Xml.Linq;
using Cinder.App.ViewModels.Tools;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

public sealed class MapAndGraphToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-map-" + Guid.NewGuid().ToString("N"));

    public MapAndGraphToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Kml_writes_longitude_before_latitude_and_escapes_evidence_text()
    {
        var path = Path.Combine(_dir, "out.kml");
        var when = new DateTimeOffset(2024, 3, 5, 14, 7, 9, TimeSpan.Zero);
        MapTool.WriteKml(path,
        [
            new MapRow(51.5007, -0.1246, when, "2024-03-05 14:07:09 UTC", "GPS fix", "IMG <1> & \"two\".jpg", "photo EXIF"),
            new MapRow(-33.8568, 151.2153, null, "2023-01-01 10:00:00 (no time zone)", "camera clock", "", "photo EXIF"),
        ]);

        // Parsing at all proves the label was escaped: a raw "<1>" would be a malformed document.
        var doc = XDocument.Load(path);
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var marks = doc.Descendants(k + "Placemark").ToList();
        marks.Should().HaveCount(2);

        marks[0].Element(k + "name")!.Value.Should().Be("IMG <1> & \"two\".jpg");
        marks[0].Descendants(k + "coordinates").Single().Value.Should().Be("-0.1246,51.5007,0", "KML order is longitude,latitude");
        marks[0].Descendants(k + "when").Single().Value.Should().Be("2024-03-05T14:07:09Z");

        // A camera-clock time has no zone, so it must not be exported as a UTC instant.
        marks[1].Descendants(k + "when").Should().BeEmpty();
        marks[1].Element(k + "description")!.Value.Should().Contain("camera clock");
        marks[1].Element(k + "name")!.Value.Should().Be("photo EXIF", "an unlabelled point falls back to its source");
    }

    [Theory]
    [InlineData("alice@example.com", "alice@example.com")]
    [InlineData("  Alice Example <alice@example.com> ", "alice@example.com")]
    [InlineData("\"Example, Alice\" <alice@example.com>", "alice@example.com")]
    [InlineData("\"alice@example.com\"", "alice@example.com")]
    [InlineData("Bob <b<ob> <bob@example.com>", "bob@example.com")]
    public void Address_is_extracted_from_both_header_forms(string header, string expected) =>
        GraphTool.ExtractAddress(header).Should().Be(expected);

    [Fact]
    public void A_manual_map_point_outside_the_globe_is_refused()
    {
        var tool = new MapTool { Latitude = 123, Longitude = 10 };
        tool.AddPointCommand.Execute(null);
        tool.Points.Should().BeEmpty();
        tool.StatusLine.Should().Contain("latitude");

        tool.Latitude = 48.8584;
        tool.Longitude = 2.2945;
        tool.Label = "Tower";
        tool.AddPointCommand.Execute(null);
        tool.Points.Should().ContainSingle();
        tool.Points[0].Timestamp.Should().BeNull("a hand-entered point has no event time; 'now' would look like one");
    }

    [Fact]
    public void A_manual_graph_link_has_no_invented_timestamp_and_the_person_filter_narrows_messages()
    {
        var tool = new GraphTool { From = "a@x.test", To = "b@x.test" };
        tool.AddEdgeCommand.Execute(null);
        tool.From = "c@x.test";
        tool.To = "b@x.test";
        tool.AddEdgeCommand.Execute(null);

        tool.Edges.Should().HaveCount(2);
        tool.Edges.Should().OnlyContain(e => e.SentUtc == "");
        tool.Nodes[0].Identity.Should().Be("b@x.test", "the most connected person is listed first");

        tool.SelectedNode = tool.Nodes.Single(n => n.Identity == "a@x.test");
        tool.Edges.Should().ContainSingle().Which.From.Should().Be("a@x.test");

        tool.ShowAllCommand.Execute(null);
        tool.Edges.Should().HaveCount(2);
    }
}
