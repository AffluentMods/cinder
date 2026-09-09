using System.Text;
using Cinder.Carving;
using FluentAssertions;
using Xunit;

namespace Cinder.Carving.Tests;

/// <summary>
/// Coverage for the header/footer carver. The window-boundary cases matter most: the carver
/// re-scans an overlap tail so a straddling header is still matched, and the arithmetic that
/// decides which window "owns" a hit is what stops the same object being carved twice.
/// </summary>
public sealed class FileCarverTests
{
    private const int TimeoutMs = 60_000;
    private const int ChunkSize = 4 << 20;

    private static readonly CarveSignature Png = new(
        "PNG", "png",
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
        [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82],
        10 * 1024 * 1024);

    private static readonly CarveSignature Gif = new(
        "GIF", "gif", "GIF8"u8.ToArray(), [0x00, 0x3B], 1024 * 1024);

    // ------------------------------------------------------------------ basics ----

    [Fact(Timeout = TimeoutMs)]
    public async Task Carves_a_single_object_between_header_and_footer()
    {
        var image = new byte[4096];
        var blob = MakePng(payload: 100);
        blob.CopyTo(image, 512);

        var hits = await CarveAll(image, [Png]);

        hits.Should().ContainSingle();
        hits[0].Offset.Should().Be(512);
        hits[0].Length.Should().Be(blob.Length);
        hits[0].Label.Should().Be("PNG");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Carves_multiple_objects_in_offset_order()
    {
        var image = new byte[8192];
        MakePng(50).CopyTo(image, 100);
        MakePng(80).CopyTo(image, 2000);
        MakePng(30).CopyTo(image, 6000);

        var hits = await CarveAll(image, [Png]);

        hits.Should().HaveCount(3);
        hits.Select(h => h.Offset).Should().BeInAscendingOrder();
        hits.Select(h => h.Offset).Should().Equal(100, 2000, 6000);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Carved_bytes_match_the_source_exactly()
    {
        var image = new byte[4096];
        var blob = MakePng(payload: 256);
        blob.CopyTo(image, 300);

        var dir = Directory.CreateTempSubdirectory("cinder-carve-out");
        try
        {
            var hits = await CarveAll(image, [Png], dir.FullName);
            hits.Should().ContainSingle();
            hits[0].OutputPath.Should().NotBeNull();

            var written = await File.ReadAllBytesAsync(hits[0].OutputPath!);
            written.Should().Equal(blob);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { }
        }
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Multiple_signatures_are_all_matched()
    {
        var image = new byte[8192];
        MakePng(40).CopyTo(image, 200);
        MakeGif(40).CopyTo(image, 4000);

        var hits = await CarveAll(image, [Png, Gif]);

        hits.Should().HaveCount(2);
        hits.Select(h => h.Label).Should().BeEquivalentTo(["PNG", "GIF"]);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task An_object_with_no_footer_is_capped_at_the_signature_max_length()
    {
        var noFooter = new CarveSignature("RAW", "bin", "HDR!"u8.ToArray(), null, 64);
        var image = new byte[4096];
        "HDR!"u8.ToArray().CopyTo(image, 1000);

        var hits = await CarveAll(image, [noFooter]);

        hits.Should().ContainSingle();
        hits[0].Length.Should().Be(64);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task A_failing_validator_marks_the_hit_and_suppresses_the_write()
    {
        var rejecting = new CarveSignature(
            "PNG", "png", Png.Header, Png.Footer, Png.MaxLengthBytes, Validator: _ => false);

        var image = new byte[4096];
        MakePng(64).CopyTo(image, 100);

        var dir = Directory.CreateTempSubdirectory("cinder-carve-reject");
        try
        {
            var hits = await CarveAll(image, [rejecting], dir.FullName);
            hits.Should().ContainSingle();
            hits[0].Validated.Should().BeFalse();
            hits[0].OutputPath.Should().BeNull();
            Directory.GetFiles(dir.FullName).Should().BeEmpty();
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { }
        }
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Empty_input_yields_nothing()
    {
        (await CarveAll([], [Png])).Should().BeEmpty();
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Image_with_no_matches_yields_nothing_and_terminates()
    {
        (await CarveAll(new byte[ChunkSize + 5000], [Png])).Should().BeEmpty();
    }

    // -------------------------------------------------------- window boundaries ----

    [Fact(Timeout = TimeoutMs)]
    public async Task Finds_a_header_straddling_a_window_boundary_exactly_once()
    {
        // Header starts 3 bytes before the 4 MiB window edge, so it is split across windows.
        var image = new byte[ChunkSize + 65536];
        var blob = MakePng(payload: 512);
        blob.CopyTo(image, ChunkSize - 3);

        var hits = await CarveAll(image, [Png]);

        hits.Should().ContainSingle("a straddling header must be carved once, not zero or twice");
        hits[0].Offset.Should().Be(ChunkSize - 3);
        hits[0].Length.Should().Be(blob.Length);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Does_not_duplicate_a_hit_sitting_inside_the_overlap_tail()
    {
        // The old carver re-scanned the retained tail without tracking ownership, so every hit
        // landing in the last maxHeaderLength bytes of a window was reported twice.
        var image = new byte[ChunkSize + 65536];
        var blob = MakePng(payload: 128);
        blob.CopyTo(image, ChunkSize - 6);

        var hits = await CarveAll(image, [Png]);

        hits.Should().ContainSingle();
        hits[0].Offset.Should().Be(ChunkSize - 6);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Finds_hits_in_both_the_first_and_second_window()
    {
        var image = new byte[ChunkSize * 2 + 4096];
        MakePng(64).CopyTo(image, 1024);
        MakePng(64).CopyTo(image, ChunkSize + 1024);
        MakePng(64).CopyTo(image, ChunkSize * 2 + 512);

        var hits = await CarveAll(image, [Png]);

        hits.Should().HaveCount(3);
        hits.Select(h => h.Offset).Should().Equal(1024, ChunkSize + 1024, ChunkSize * 2L + 512);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Finds_an_object_that_extends_past_the_end_of_its_window()
    {
        // Header near the end of window 1, footer well inside window 2: extraction has to
        // reach past the window rather than truncating at it.
        var image = new byte[ChunkSize * 2];
        var blob = MakePng(payload: 200_000);
        blob.CopyTo(image, ChunkSize - 1000);

        var hits = await CarveAll(image, [Png]);

        hits.Should().ContainSingle();
        hits[0].Offset.Should().Be(ChunkSize - 1000);
        hits[0].Length.Should().Be(blob.Length);
    }

    // ------------------------------------------------------------- stream shapes ----

    [Fact(Timeout = TimeoutMs)]
    public async Task Works_over_a_stream_that_returns_short_reads()
    {
        var image = new byte[ChunkSize + 8192];
        var blob = MakePng(payload: 100);
        blob.CopyTo(image, ChunkSize - 4);

        var carver = new FileCarver([Png]);
        await using var stream = new ShortReadStream(image, maxPerRead: 6151);
        var hits = new List<CarveHit>();
        await foreach (var h in carver.CarveAsync(stream))
        {
            hits.Add(h);
        }

        hits.Should().ContainSingle();
        hits[0].Offset.Should().Be(ChunkSize - 4);
        hits[0].Length.Should().Be(blob.Length);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Carves_from_a_non_seekable_stream_instead_of_returning_empty_blobs()
    {
        // Non-seekable sources previously produced hits of length 0 that were never written —
        // which is what SlackUnallocCarver's slice looked like.
        var image = new byte[8192];
        var blob = MakePng(payload: 128);
        blob.CopyTo(image, 512);

        var carver = new FileCarver([Png]);
        await using var stream = new NonSeekableStream(image);
        var hits = new List<CarveHit>();
        await foreach (var h in carver.CarveAsync(stream))
        {
            hits.Add(h);
        }

        hits.Should().ContainSingle();
        hits[0].Offset.Should().Be(512);
        hits[0].Length.Should().Be(blob.Length);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Slack_region_carving_reports_image_absolute_offsets_with_real_lengths()
    {
        var image = new byte[16384];
        var blob = MakePng(payload: 96);
        blob.CopyTo(image, 9000);

        var carver = new SlackUnallocCarver(new FileCarver([Png]));
        await using var stream = new MemoryStream(image);

        var hits = new List<CarveHit>();
        await foreach (var h in carver.CarveRegionsAsync(stream, [new CarveRegion("unalloc", 8192, 4096)]))
        {
            hits.Add(h);
        }

        hits.Should().ContainSingle();
        hits[0].Offset.Should().Be(9000, "offsets must be image-absolute, not region-relative");
        hits[0].Length.Should().Be(blob.Length, "a zero length here means nothing was actually extracted");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Cancellation_stops_the_carve()
    {
        var image = new byte[ChunkSize * 2];
        var carver = new FileCarver([Png]);
        await using var stream = new MemoryStream(image);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () =>
        {
            await foreach (var _ in carver.CarveAsync(stream, ct: cts.Token)) { }
        };
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact(Timeout = TimeoutMs)]
    public Task Rejects_a_signature_with_an_empty_header()
    {
        var act = () => new FileCarver([new CarveSignature("bad", "bin", [], null, 100)]);
        act.Should().Throw<ArgumentException>();
        return Task.CompletedTask;
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Default_signature_set_loads_and_carves()
    {
        var image = new byte[8192];
        MakePng(64).CopyTo(image, 1000);

        var hits = await CarveAll(image, null);
        hits.Should().Contain(h => h.Label == "PNG" && h.Offset == 1000);
    }

    // ------------------------------------------------------------------ helpers ----

    private static async Task<List<CarveHit>> CarveAll(byte[] image, IReadOnlyList<CarveSignature>? sigs, string? outDir = null)
    {
        var carver = new FileCarver(sigs);
        await using var stream = new MemoryStream(image);
        var hits = new List<CarveHit>();
        await foreach (var h in carver.CarveAsync(stream, outDir))
        {
            hits.Add(h);
        }
        return hits;
    }

    private static byte[] MakePng(int payload)
    {
        var header = Png.Header;
        var footer = Png.Footer!;
        var b = new byte[header.Length + payload + footer.Length];
        header.CopyTo(b, 0);
        for (int i = 0; i < payload; i++)
        {
            b[header.Length + i] = (byte)(i % 251 + 1);   // never 0x00, so no accidental footers
        }
        footer.CopyTo(b, header.Length + payload);
        return b;
    }

    private static byte[] MakeGif(int payload)
    {
        var header = Gif.Header;
        var footer = Gif.Footer!;
        var b = new byte[header.Length + payload + footer.Length];
        header.CopyTo(b, 0);
        for (int i = 0; i < payload; i++)
        {
            b[header.Length + i] = (byte)(i % 250 + 2);
        }
        footer.CopyTo(b, header.Length + payload);
        return b;
    }

    /// <summary>Honours only part of each read, exercising the short-read handling.</summary>
    private sealed class ShortReadStream(byte[] data, int maxPerRead) : MemoryStream(data, writable: false)
    {
        public override int Read(Span<byte> buffer)
            => base.Read(buffer[..Math.Min(buffer.Length, maxPerRead)]);

        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(count, maxPerRead));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, maxPerRead)], ct);
    }

    /// <summary>Forward-only stream, the shape a piped or sliced source presents.</summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = Math.Min(buffer.Length, data.Length - _pos);
            if (n <= 0)
            {
                return 0;
            }
            data.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
