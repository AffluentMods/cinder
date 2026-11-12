using System.Text;
using Cinder.Hex;
using FluentAssertions;
using Xunit;

namespace Cinder.Hex.Tests;

/// <summary>
/// Searching on from a given offset, and backwards from one — what "find next" and "find
/// previous" need once a result list has been capped. The contract under test: resuming or
/// reversing returns exactly the hits the full search returns, nothing extra and nothing lost,
/// and a page says whether it ended at the cap or at the end of the range.
/// </summary>
public sealed class HexSearchPagingTests
{
    private const int TimeoutMs = 60_000;
    private const int Window = 1 << 20;     // the search's window size

    private static Task Guarded(Action body) => Task.Run(body);

    private static List<HexSearchHit> All(IHexBuffer buffer, HexSearchOptions options) => HexSearch.Search(buffer, options).ToList();

    /// <summary>A buffer with <paramref name="marker"/> written at each of <paramref name="offsets"/>.</summary>
    private static ByteArrayHexBuffer WithMarkers(int length, string marker, params int[] offsets)
        => new(Data(length, marker, offsets));

    private static byte[] Data(int length, string marker, params int[] offsets)
    {
        var data = new byte[length];
        Array.Fill(data, (byte)'.');
        var bytes = Encoding.ASCII.GetBytes(marker);
        foreach (var o in offsets)
        {
            bytes.CopyTo(data, o);
        }
        return data;
    }

    // ------------------------------------------------------------ beyond the cap, forwards ----

    [Fact(Timeout = TimeoutMs)]
    public Task Paging_forward_past_the_cap_returns_every_hit_once_and_says_when_it_is_done() => Guarded(() =>
    {
        // 2,500 matches spread over three windows; the app's cap is 1,000.
        var offsets = Enumerable.Range(0, 2500).Select(i => 17 + i * 1021).ToArray();
        using var buffer = WithMarkers(3 * Window, "NEEDLE", offsets);
        var options = new HexSearchOptions(HexSearchKind.Ascii, "needle", CaseSensitive: false);

        var first = HexSearch.FindNext(buffer, options, fromOffset: 0, maxHits: 1000);
        first.Hits.Should().HaveCount(1000);
        first.ReachedEnd.Should().BeFalse("it stopped at the cap, with 1,500 matches still ahead");
        first.StoppedAtCap.Should().BeTrue();
        first.ContinueFrom.Should().Be(first.Hits[^1].Offset + 1);

        var second = HexSearch.FindNext(buffer, options, first.ContinueFrom!.Value, 1000);
        second.Hits.Should().HaveCount(1000);
        second.ReachedEnd.Should().BeFalse();

        var third = HexSearch.FindNext(buffer, options, second.ContinueFrom!.Value, 1000);
        third.Hits.Should().HaveCount(500);
        third.ReachedEnd.Should().BeTrue("the search ran to the end of the buffer");
        third.ContinueFrom.Should().BeNull();

        first.Hits.Concat(second.Hits).Concat(third.Hits).Select(h => h.Offset)
            .Should().Equal(offsets.Select(o => (long)o), "no hit repeated, none skipped");
    });

    [Fact(Timeout = TimeoutMs)]
    public Task A_page_that_ends_exactly_at_the_cap_is_followed_by_an_empty_page_that_reached_the_end() => Guarded(() =>
    {
        using var buffer = WithMarkers(4096, "XY", Enumerable.Range(0, 10).Select(i => i * 100).ToArray());
        var options = new HexSearchOptions(HexSearchKind.Ascii, "XY");

        var page = HexSearch.FindNext(buffer, options, 0, maxHits: 10);
        page.Hits.Should().HaveCount(10);
        page.ReachedEnd.Should().BeFalse("at the cap the search has not looked further, so it cannot claim there is nothing more");

        var next = HexSearch.FindNext(buffer, options, page.ContinueFrom!.Value, 10);
        next.Hits.Should().BeEmpty();
        next.ReachedEnd.Should().BeTrue();
    });

    [Fact(Timeout = TimeoutMs)]
    public Task No_match_at_all_is_an_empty_page_that_reached_the_end() => Guarded(() =>
    {
        using var buffer = WithMarkers(4096, "XY");
        var options = new HexSearchOptions(HexSearchKind.Ascii, "XY");

        var page = HexSearch.FindNext(buffer, options, 0, 1000);
        page.Hits.Should().BeEmpty();
        page.ReachedEnd.Should().BeTrue();
        page.StoppedAtCap.Should().BeFalse();
        page.ContinueFrom.Should().BeNull();
        HexSearch.FindPrevious(buffer, options, long.MaxValue, 1000).ReachedEnd.Should().BeTrue();
        HexSearch.FindNext(buffer, options, fromOffset: 1_000_000, 1000).ReachedEnd.Should().BeTrue("starting past the end");
        HexSearch.FindPrevious(buffer, options, beforeOffset: 0, 1000).ReachedEnd.Should().BeTrue("nothing lies before the start");
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Find_next_from_the_last_capped_hit_reaches_matches_the_capped_list_never_held() => Guarded(() =>
    {
        // The scenario from the find bar: the list holds the first 1,000 hits; "next" on the
        // last one used to wrap to the first although more matches follow.
        var offsets = Enumerable.Range(0, 1003).Select(i => i * 50).ToArray();
        using var buffer = WithMarkers(64 * 1024, "ab", offsets);
        var options = new HexSearchOptions(HexSearchKind.Ascii, "ab");

        var capped = HexSearch.FindNext(buffer, options, 0, 1000);
        var lastShown = capped.Hits[^1];

        var beyond = HexSearch.FindNext(buffer, options, lastShown.Offset + 1, 1000);
        beyond.Hits.Select(h => h.Offset).Should().Equal(50_000, 50_050, 50_100);
        beyond.ReachedEnd.Should().BeTrue("now it is right to wrap around");
    });

    // ----------------------------------------------------------- where a search starts ----

    [Fact(Timeout = TimeoutMs)]
    public Task Starting_offsets_are_inclusive_forwards_and_exclusive_backwards() => Guarded(() =>
    {
        using var buffer = WithMarkers(1000, "MARK", 100, 500);
        var options = new HexSearchOptions(HexSearchKind.Ascii, "MARK");

        HexSearch.SearchFrom(buffer, options, 100).Select(h => h.Offset).Should().Equal(100, 500);
        // A match that begins before the starting offset is behind it, even if it overlaps it.
        HexSearch.SearchFrom(buffer, options, 101).Select(h => h.Offset).Should().Equal(500);
        HexSearch.SearchFrom(buffer, options, 501).Should().BeEmpty();
        HexSearch.SearchFrom(buffer, options, -5).Select(h => h.Offset).Should().Equal(100, 500);

        HexSearch.SearchBackward(buffer, options, 500).Select(h => h.Offset).Should().Equal(100);
        HexSearch.SearchBackward(buffer, options, 501).Select(h => h.Offset).Should().Equal(500, 100);
        HexSearch.SearchBackward(buffer, options, 100).Should().BeEmpty();
        HexSearch.SearchBackward(buffer, options, long.MaxValue).Select(h => h.Offset).Should().Equal(500, 100);
    });

    [Fact(Timeout = TimeoutMs)]
    public Task A_match_straddling_a_window_boundary_is_found_from_either_direction() => Guarded(() =>
    {
        // Across the first 1 MiB boundary of a scan from 0, and across where the boundaries of
        // a scan resumed from 300, or a backward scan from the end, fall.
        using var buffer = WithMarkers(3 * Window + 500, "STRADDLE",
            Window - 3,                      // forward grid from 0
            300 + Window - 4,                // forward grid from 300
            2 * Window + 490,                // backward grid from the end (its windows end at 3 MiB + 493 - k MiB)
            2 * Window + 250);
        var options = new HexSearchOptions(HexSearchKind.Ascii, "STRADDLE");
        var all = All(buffer, options).Select(h => h.Offset).ToList();
        all.Should().HaveCount(4);

        HexSearch.SearchFrom(buffer, options, 300).Select(h => h.Offset).Should().Equal(all);
        HexSearch.SearchFrom(buffer, options, Window - 3).Select(h => h.Offset).Should().Equal(all);
        HexSearch.SearchFrom(buffer, options, Window - 2).Select(h => h.Offset).Should().Equal(all.Skip(1));
        HexSearch.SearchBackward(buffer, options, long.MaxValue).Select(h => h.Offset).Should().Equal(Enumerable.Reverse(all));
        HexSearch.SearchBackward(buffer, options, Window).Select(h => h.Offset).Should().Equal(Window - 3L);
        HexSearch.SearchBackward(buffer, options, 2 * Window + 251).Select(h => h.Offset).Should().Equal(Enumerable.Reverse(all.Take(3)));
    });

    // --------------------------------------------------------------------- backwards ----

    [Fact(Timeout = TimeoutMs)]
    public Task Paging_backward_walks_the_same_hits_in_reverse() => Guarded(() =>
    {
        var offsets = Enumerable.Range(0, 2500).Select(i => 17 + i * 1021).ToArray();
        using var buffer = WithMarkers(3 * Window, "NEEDLE", offsets);
        var options = new HexSearchOptions(HexSearchKind.Ascii, "NEEDLE");

        var seen = new List<long>();
        long before = long.MaxValue;
        var pages = 0;
        while (true)
        {
            var page = HexSearch.FindPrevious(buffer, options, before, maxHits: 700);
            seen.AddRange(page.Hits.Select(h => h.Offset));
            pages++;
            if (page.ReachedEnd)
            {
                page.ContinueFrom.Should().BeNull();
                break;
            }
            page.Hits.Should().HaveCount(700);
            before = page.ContinueFrom!.Value;
            before.Should().Be(page.Hits[^1].Offset);
        }

        pages.Should().Be(4);
        seen.Should().Equal(offsets.Reverse().Select(o => (long)o));
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Find_previous_from_the_first_hit_of_a_capped_list_reaches_the_matches_at_the_end() => Guarded(() =>
    {
        // "Previous" on the first hit should go to the last match in the file, which a list
        // capped at 1,000 does not contain.
        var offsets = Enumerable.Range(0, 1200).Select(i => i * 40).ToArray();
        using var buffer = WithMarkers(64 * 1024, "ab", offsets);
        var options = new HexSearchOptions(HexSearchKind.Ascii, "ab");

        var before = HexSearch.FindPrevious(buffer, options, beforeOffset: 0, maxHits: 1);
        before.Hits.Should().BeEmpty();
        before.ReachedEnd.Should().BeTrue("so wrap: search back from the end");

        var wrapped = HexSearch.FindPrevious(buffer, options, long.MaxValue, 1);
        wrapped.Hits.Single().Offset.Should().Be(1199 * 40);
        wrapped.ReachedEnd.Should().BeFalse();
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Overlapping_matches_are_all_found_in_both_directions() => Guarded(() =>
    {
        using var buffer = new ByteArrayHexBuffer(Encoding.ASCII.GetBytes("xaaaaax"));
        var options = new HexSearchOptions(HexSearchKind.Ascii, "aa");

        HexSearch.Search(buffer, options).Select(h => h.Offset).Should().Equal(1, 2, 3, 4);
        HexSearch.SearchBackward(buffer, options, long.MaxValue).Select(h => h.Offset).Should().Equal(4, 3, 2, 1);
        HexSearch.SearchBackward(buffer, options, 3).Select(h => h.Offset).Should().Equal(2, 1);
        HexSearch.SearchFrom(buffer, options, 3).Select(h => h.Offset).Should().Equal(3, 4);
    });

    // ------------------------------------------------- same options, same guarantees ----

    [Theory(Timeout = TimeoutMs)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public Task Resumed_and_backward_searches_return_exactly_the_hits_of_the_full_search(int seed) => Guarded(() =>
    {
        // Two-symbol data makes short patterns frequent and overlapping; 2.5 windows of it
        // puts hits on and around every boundary.
        var random = new Random(seed);
        var data = new byte[2 * Window + Window / 2 + seed * 13];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(random.Next(2) == 0 ? 'a' : 'B');
        }
        using var buffer = new ByteArrayHexBuffer(data);
        var query = Encoding.ASCII.GetString(data, Window - 6, 12).ToLowerInvariant();

        foreach (var options in new[]
        {
            new HexSearchOptions(HexSearchKind.Ascii, query, CaseSensitive: false),
            new HexSearchOptions(HexSearchKind.Ascii, query, CaseSensitive: false, StartOffset: 4321, EndOffset: 2 * Window + 99),
            new HexSearchOptions(HexSearchKind.Hex, Convert.ToHexString(data, Window - 6, 12)),
        })
        {
            var all = All(buffer, options);
            all.Should().NotBeEmpty();

            foreach (var at in new long[] { 0, 1, 4321, Window - 7, Window - 6, Window - 5, Window, Window + 1, 2 * Window - 1, 2 * Window + 98, data.Length, long.MaxValue })
            {
                HexSearch.SearchFrom(buffer, options, at).Should().Equal(all.Where(h => h.Offset >= at), $"forward from {at}");
                HexSearch.SearchBackward(buffer, options, at).Should().Equal(all.Where(h => h.Offset < at).Reverse(), $"backward from {at}");
            }
        }
    });

    [Fact(Timeout = TimeoutMs)]
    public Task A_resumed_regex_search_keeps_the_window_grid_of_the_full_search() => Guarded(() =>
    {
        // Regex windows do not overlap: a match across a window boundary is not found, and
        // where the boundaries fall is part of what "the same results" means. Range start 100
        // puts them at 100 + k MiB.
        const int rangeStart = 100;
        const int resumeAt = 500;
        var onFullGridBoundary = rangeStart + Window - 3;        // straddles the real grid: never found
        var onResumedGridBoundary = resumeAt + Window - 3;       // inside window 1 of the real grid: found
        using var buffer = WithMarkers(3 * Window, "id=1234", 200, 900, onFullGridBoundary, onResumedGridBoundary, 2 * Window + 7777);
        var options = new HexSearchOptions(HexSearchKind.Regex, "id=[0-9]{4}", StartOffset: rangeStart);

        var all = All(buffer, options).Select(h => h.Offset).ToList();
        all.Should().Equal(200, 900, onResumedGridBoundary, 2 * Window + 7777);

        // Resuming must not re-anchor the windows at the resume point: that would lose the
        // third hit (and could just as well find one the full search cannot).
        HexSearch.SearchFrom(buffer, options, resumeAt).Select(h => h.Offset).Should().Equal(all.Skip(1));
        HexSearch.FindNext(buffer, options, resumeAt, 2).Hits.Select(h => h.Offset).Should().Equal(900, onResumedGridBoundary);
        HexSearch.SearchFrom(buffer, options, onResumedGridBoundary + 1).Select(h => h.Offset).Should().Equal(2L * Window + 7777);

        HexSearch.SearchBackward(buffer, options, long.MaxValue).Select(h => h.Offset).Should().Equal(Enumerable.Reverse(all));
        HexSearch.SearchBackward(buffer, options, onResumedGridBoundary).Select(h => h.Offset).Should().Equal(900, 200);
        HexSearch.FindPrevious(buffer, options, 2 * Window + 7777, 1).Hits.Single().Should().Be(new HexSearchHit(onResumedGridBoundary, 7));
    });

    [Fact(Timeout = TimeoutMs)]
    public Task The_range_and_the_kind_rules_of_the_options_still_apply() => Guarded(() =>
    {
        var data = Data(4096, "MZ", 10, 1000, 2000, 3000);
        Encoding.ASCII.GetBytes("mz").CopyTo(data, 1500);
        using var buffer = new ByteArrayHexBuffer(data);

        // A hit must lie wholly inside [StartOffset, EndOffset): 3000..3001 does not fit before 3001.
        var ranged = new HexSearchOptions(HexSearchKind.Ascii, "MZ", StartOffset: 11, EndOffset: 3001);
        HexSearch.SearchFrom(buffer, ranged, 0).Select(h => h.Offset).Should().Equal(1000, 2000);
        HexSearch.SearchBackward(buffer, ranged, long.MaxValue).Select(h => h.Offset).Should().Equal(2000, 1000);
        HexSearch.FindNext(buffer, ranged, 1001, 5).Hits.Select(h => h.Offset).Should().Equal(2000);

        // Hex is exact bytes in both directions, whatever CaseSensitive says.
        var hex = new HexSearchOptions(HexSearchKind.Hex, "4D5A", CaseSensitive: false);
        HexSearch.SearchBackward(buffer, hex, long.MaxValue).Select(h => h.Offset).Should().Equal(3000, 2000, 1000, 10);
        var text = new HexSearchOptions(HexSearchKind.Ascii, "MZ", CaseSensitive: false);
        HexSearch.SearchBackward(buffer, text, long.MaxValue).Select(h => h.Offset).Should().Equal(3000, 2000, 1500, 1000, 10);
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Backward_search_copes_with_a_buffer_that_returns_short_reads() => Guarded(() =>
    {
        var data = new byte[Window + 5000];
        Encoding.ASCII.GetBytes("cinder").CopyTo(data, 123);
        Encoding.ASCII.GetBytes("cinder").CopyTo(data, Window - 2);
        using var buffer = new DribblingBuffer(data, maxPerRead: 4099);

        HexSearch.SearchBackward(buffer, new HexSearchOptions(HexSearchKind.Ascii, "cinder"), long.MaxValue)
            .Select(h => h.Offset).Should().Equal(Window - 2, 123);
    });

    [Fact(Timeout = TimeoutMs)]
    public Task A_cap_must_be_positive_and_cancellation_is_observed() => Guarded(() =>
    {
        using var buffer = WithMarkers(4096, "XY", 5);
        var options = new HexSearchOptions(HexSearchKind.Ascii, "XY");

        ((Action)(() => HexSearch.FindNext(buffer, options, 0, 0))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => HexSearch.FindPrevious(buffer, options, 0, -1))).Should().Throw<ArgumentOutOfRangeException>();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        ((Action)(() => HexSearch.FindNext(buffer, options, 0, 10, cts.Token))).Should().Throw<OperationCanceledException>();
        ((Action)(() => HexSearch.FindPrevious(buffer, options, long.MaxValue, 10, cts.Token))).Should().Throw<OperationCanceledException>();
    });

    /// <summary>Honours only part of each read.</summary>
    private sealed class DribblingBuffer(byte[] bytes, int maxPerRead) : IHexBuffer
    {
        public long Length => bytes.Length;
        public string DisplayName => "(dribbling buffer)";
        public bool IsReadOnly => true;

        public int Read(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset >= bytes.Length)
            {
                return 0;
            }
            var n = (int)Math.Min(Math.Min(destination.Length, maxPerRead), bytes.Length - offset);
            bytes.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }

        public void Dispose() { }
    }
}
