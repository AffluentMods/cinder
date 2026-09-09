using System.Text;
using Cinder.Hex;
using FluentAssertions;
using Xunit;

namespace Cinder.Hex.Tests;

public sealed class HexSearchTests
{
    // Every test here runs through Guarded(), which gives it a hard timeout.
    //
    // HexSearch previously never terminated: the loop relied on getting a short read to exit,
    // and no IHexBuffer implementation ever produces one, so a search that found fewer hits
    // than the caller's cap spun forever. That hung the test host instead of failing it —
    // `dotnet test` aborted with "Test host process crashed" and CI had no usable signal.
    // Running each body on a timed task turns a regression of that shape back into a red test.
    private const int TimeoutMs = 30_000;

    private static Task Guarded(Action body) => Task.Run(body);

    [Fact(Timeout = TimeoutMs)]
    public Task Finds_ascii_substring() => Guarded(() =>
    {
        var data = Encoding.ASCII.GetBytes("hello cruel cinder world");
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "cinder")).ToList();
        hits.Should().ContainSingle();
        hits[0].Offset.Should().Be(12);
        hits[0].Length.Should().Be(6);
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Finds_overlapping_hits() => Guarded(() =>
    {
        var data = Encoding.ASCII.GetBytes("aaaa");
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "aa")).ToList();
        hits.Should().HaveCount(3); // 0, 1, 2
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Finds_hex_pattern() => Guarded(() =>
    {
        byte[] data = [0x01, 0x02, 0x89, 0x50, 0x4E, 0x47, 0xFF];
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Hex, "89 50 4E 47")).ToList();
        hits.Should().ContainSingle();
        hits[0].Offset.Should().Be(2);
        hits[0].Length.Should().Be(4);
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Case_insensitive_ascii() => Guarded(() =>
    {
        var data = Encoding.ASCII.GetBytes("Cinder");
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "CINDER", CaseSensitive: false)).ToList();
        hits.Should().ContainSingle();
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Case_sensitive_ascii_does_not_match_different_case() => Guarded(() =>
    {
        var data = Encoding.ASCII.GetBytes("Cinder");
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "CINDER", CaseSensitive: true)).ToList();
        hits.Should().BeEmpty();
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Utf16le_finds_string() => Guarded(() =>
    {
        var data = Encoding.Unicode.GetBytes("\0\0Cinder\0Pad");
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Utf16Le, "Cinder")).ToList();
        hits.Should().ContainSingle();
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Mmap_buffer_round_trips() => Guarded(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"cinder-mmap-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3]);
            using var mmap = new MmapHexBuffer(path);
            mmap.Length.Should().Be(8);
            var buf = new byte[4];
            var read = mmap.Read(0, buf);
            read.Should().Be(4);
            buf.Should().Equal(0x89, 0x50, 0x4E, 0x47);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Mmap_buffer_searches_a_real_file_to_completion() => Guarded(() =>
    {
        // The mmap buffer is what production actually uses. Exercise the full search path
        // against it, not just ByteArrayHexBuffer.
        var path = Path.Combine(Path.GetTempPath(), $"cinder-mmap-search-{Guid.NewGuid():N}.bin");
        try
        {
            var data = new byte[(1 << 20) + 9000];
            Encoding.ASCII.GetBytes("cinder").CopyTo(data, (1 << 20) - 2);
            File.WriteAllBytes(path, data);

            using var mmap = new MmapHexBuffer(path);
            var hits = HexSearch.Search(mmap, new HexSearchOptions(HexSearchKind.Ascii, "cinder")).ToList();
            hits.Should().ContainSingle();
            hits[0].Offset.Should().Be((1 << 20) - 2);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    });

    // ---- termination / boundary regression coverage ----

    [Fact(Timeout = TimeoutMs)]
    public Task Search_terminates_when_pattern_is_absent() => Guarded(() =>
    {
        // The original loop spun forever here: the tail window shrank to `overlap` bytes,
        // the advance went to zero, and `pos` stopped moving.
        var data = Encoding.ASCII.GetBytes(new string('x', 4096));
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "cinder")).ToList();
        hits.Should().BeEmpty();
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Search_terminates_on_buffer_larger_than_one_window() => Guarded(() =>
    {
        // Two full 1 MiB windows plus a tail, so the advance/terminate path runs several times.
        var data = new byte[(1 << 20) * 2 + 4321];
        Encoding.ASCII.GetBytes("cinder").CopyTo(data, 5);
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "cinder")).ToList();
        hits.Should().ContainSingle();
        hits[0].Offset.Should().Be(5);
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Finds_match_spanning_a_window_boundary_exactly_once() => Guarded(() =>
    {
        // Straddle the 1 MiB window edge: three bytes before it, three after.
        const int chunk = 1 << 20;
        var data = new byte[chunk + 4096];
        Encoding.ASCII.GetBytes("cinder").CopyTo(data, chunk - 3);

        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "cinder")).ToList();

        hits.Should().ContainSingle("a boundary-spanning match must be found, and only once");
        hits[0].Offset.Should().Be(chunk - 3);
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Finds_match_at_the_very_end_of_the_buffer() => Guarded(() =>
    {
        var data = Encoding.ASCII.GetBytes("padding-padding-cinder");
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "cinder")).ToList();
        hits.Should().ContainSingle();
        hits[0].Offset.Should().Be(data.Length - 6);
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Tolerates_a_buffer_that_returns_short_reads() => Guarded(() =>
    {
        // IHexBuffer.Read is allowed to return fewer bytes than requested. Search must fill
        // its window across short reads rather than silently shrinking it.
        const int chunk = 1 << 20;
        var data = new byte[chunk + 2048];
        Encoding.ASCII.GetBytes("cinder").CopyTo(data, chunk - 2);

        using var buf = new ShortReadHexBuffer(data, maxPerRead: 7919);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "cinder")).ToList();
        hits.Should().ContainSingle();
        hits[0].Offset.Should().Be(chunk - 2);
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Respects_start_and_end_offsets() => Guarded(() =>
    {
        var data = Encoding.ASCII.GetBytes("cinder ---- cinder ---- cinder");
        using var buf = new ByteArrayHexBuffer(data);

        var all = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "cinder")).ToList();
        all.Should().HaveCount(3);

        var windowed = HexSearch.Search(buf,
            new HexSearchOptions(HexSearchKind.Ascii, "cinder", StartOffset: 1, EndOffset: 24)).ToList();
        windowed.Should().ContainSingle();
        windowed[0].Offset.Should().Be(12);
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Pattern_longer_than_the_buffer_yields_nothing() => Guarded(() =>
    {
        using var buf = new ByteArrayHexBuffer(Encoding.ASCII.GetBytes("ab"));
        HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "abcdef")).Should().BeEmpty();
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Empty_and_malformed_queries_yield_nothing() => Guarded(() =>
    {
        using var buf = new ByteArrayHexBuffer(Encoding.ASCII.GetBytes("whatever"));
        HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "")).Should().BeEmpty();
        HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Hex, "")).Should().BeEmpty();
        HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Hex, "ABC")).Should().BeEmpty();     // odd nibble count
        HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Hex, "ZZ")).Should().BeEmpty();      // not hex
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Oversized_hex_query_is_rejected_rather_than_blowing_the_stack() => Guarded(() =>
    {
        using var buf = new ByteArrayHexBuffer(Encoding.ASCII.GetBytes("whatever"));
        var huge = new string('A', (1 << 16) + 2);
        HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Hex, huge)).Should().BeEmpty();
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Cancellation_stops_the_search() => Guarded(() =>
    {
        var data = new byte[(1 << 20) * 4];
        using var buf = new ByteArrayHexBuffer(data);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Ascii, "cinder"), cts.Token).ToList();
        act.Should().Throw<OperationCanceledException>();
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Regex_search_finds_matches_and_terminates() => Guarded(() =>
    {
        var data = Encoding.ASCII.GetBytes("id=4211 and id=9002 done");
        using var buf = new ByteArrayHexBuffer(data);
        var hits = HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Regex, "id=[0-9]{4}")).ToList();
        hits.Should().HaveCount(2);
        hits[0].Offset.Should().Be(0);
        hits[0].Length.Should().Be(7);
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Invalid_regex_yields_nothing_instead_of_throwing() => Guarded(() =>
    {
        using var buf = new ByteArrayHexBuffer(Encoding.ASCII.GetBytes("abc"));
        HexSearch.Search(buf, new HexSearchOptions(HexSearchKind.Regex, "([unclosed")).Should().BeEmpty();
    });

    /// <summary>
    /// An <see cref="IHexBuffer"/> that deliberately honours only part of each read, to exercise
    /// the short-read handling the real implementations don't happen to trigger.
    /// </summary>
    private sealed class ShortReadHexBuffer(byte[] bytes, int maxPerRead) : IHexBuffer
    {
        public long Length => bytes.Length;
        public string DisplayName => "(short-read buffer)";
        public bool IsReadOnly => true;

        public int Read(long offset, Span<byte> destination)
        {
            if (offset >= bytes.Length)
            {
                return 0;
            }
            var available = (int)Math.Min(Math.Min(destination.Length, maxPerRead), bytes.Length - offset);
            bytes.AsSpan((int)offset, available).CopyTo(destination);
            return available;
        }

        public void Dispose() { }
    }
}
