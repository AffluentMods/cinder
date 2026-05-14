using Cinder.Search;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>End-to-end check on the Lucene case index: write a doc, search for it, verify hit.</summary>
public sealed class SearchTests : IDisposable
{
    private readonly string _dir;

    public SearchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cinder-search-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Index_and_search_round_trip()
    {
        using var idx = new CaseIndex(_dir);
        idx.OpenForWrite();
        idx.IndexDocument(new IndexableDoc(
            Id: "1",
            Source: "evtx",
            User: "alice",
            Path: @"C:\Windows\System32\winevt\Logs\Security.evtx",
            Summary: "EventID 4625 — failed logon for alice from 10.0.0.5",
            Text: "alice attempted logon from 10.0.0.5 with a bad password",
            Timestamp: DateTimeOffset.Parse("2026-05-11T18:33:01Z"),
            Tags: ["auth", "logon"]));
        idx.IndexDocument(new IndexableDoc(
            Id: "2",
            Source: "browser",
            User: "alice",
            Path: @"C:\Users\alice\AppData\Local\Google\Chrome\User Data\Default\History",
            Summary: "Visited stackoverflow.com",
            Text: "stackoverflow.com - alice browsing history",
            Timestamp: DateTimeOffset.Parse("2026-05-12T09:00:00Z"),
            Tags: null));
        idx.Commit();

        var hits = idx.Search("logon");
        hits.Should().NotBeEmpty();
        hits[0].Source.Should().Be("evtx");
        hits[0].User.Should().Be("alice");
    }

    [Fact]
    public void Multi_field_query_works_across_text_and_path()
    {
        using var idx = new CaseIndex(_dir);
        idx.OpenForWrite();
        idx.IndexDocument(new IndexableDoc(
            Id: "1", Source: "fs", User: null,
            Path: @"C:\Users\bob\Downloads\evidence",
            Summary: "Suspicious executable",
            Text: "credential dumping tool found in downloads",
            Timestamp: null,
            Tags: ["suspicious", "executable"]));
        idx.Commit();

        // Hit on the text field (analyzed).
        idx.Search("credential").Should().NotBeEmpty();
        // Hit on the path field (analyzed; backslashes split directory components).
        idx.Search("Downloads").Should().NotBeEmpty();
        // Hit on the tags field (analyzed; per-tag tokens).
        idx.Search("suspicious").Should().NotBeEmpty();
    }

    [Fact]
    public void Communication_graph_dedupes_identities()
    {
        var g = new CommunicationGraph();
        g.AddInteraction("alice@x.com", "bob@y.com", "eml", DateTimeOffset.UtcNow, "Hi");
        g.AddInteraction("alice@x.com", "bob@y.com", "eml", DateTimeOffset.UtcNow, "Re: Hi");
        g.AddInteraction("alice@x.com", "carol@z.com", "eml", DateTimeOffset.UtcNow, "FYI");

        g.Nodes.Should().HaveCount(3, "alice, bob, carol");
        g.Edges.Should().HaveCount(3);
        var alice = g.Nodes.First(n => n.Identity == "alice@x.com");
        alice.OutCount.Should().Be(3);
        alice.InCount.Should().Be(0);
    }

    [Fact]
    public void Communication_graph_clear_empties_both_collections()
    {
        var g = new CommunicationGraph();
        g.AddInteraction("a", "b", "src", DateTimeOffset.UtcNow);
        g.Clear();
        g.Nodes.Should().BeEmpty();
        g.Edges.Should().BeEmpty();
    }

    [Fact]
    public void Geo_index_clear_empties_points()
    {
        var idx = new GeoIndex();
        idx.Add(new GeoPoint(37.7, -122.4, DateTimeOffset.UtcNow, "test", "manual", null));
        idx.Count.Should().Be(1);
        idx.Clear();
        idx.Count.Should().Be(0);
    }

    [Fact]
    public void Geo_index_bounds_filter()
    {
        var idx = new GeoIndex();
        idx.Add(new GeoPoint(37.7, -122.4, null, "sf", "exif", null));   // San Francisco
        idx.Add(new GeoPoint(40.7, -74.0,  null, "nyc", "exif", null));  // New York
        idx.Add(new GeoPoint(48.8,   2.3,  null, "paris", "exif", null));

        var westCoast = idx.InBounds(36, -124, 39, -121).ToList();
        westCoast.Should().HaveCount(1);
        westCoast[0].Label.Should().Be("sf");
    }
}
