using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Cinder.Carving;
using FluentAssertions;
using Xunit;

namespace Cinder.Carving.Tests;

/// <summary>
/// What the carver does with a header once it has found one: reject coincidences, take the
/// length from the format, stream instead of buffer, and stay usable on large or hostile input.
/// </summary>
public sealed class FileCarverFormatTests : IDisposable
{
    private const int TimeoutMs = 120_000;
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    private readonly string _out = Directory.CreateTempSubdirectory("cinder-carve-fmt").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_out, recursive: true); } catch { }
    }

    private static CarveSignature Default(string extension) => CarveSignatures.Defaults.Single(s => s.Extension == extension);

    private static async Task<List<CarveHit>> Carve(Stream stream, IReadOnlyList<CarveSignature>? sigs, string? outDir = null, long? unsizedLimit = null)
    {
        var carver = unsizedLimit is { } limit ? new FileCarver(sigs) { UnsizedCarveLimitBytes = limit } : new FileCarver(sigs);
        var hits = new List<CarveHit>();
        await foreach (var h in carver.CarveAsync(stream, outDir))
        {
            hits.Add(h);
        }
        return hits;
    }

    private long BytesWritten() => new DirectoryInfo(_out).EnumerateFiles().Sum(f => f.Length);

    // ---------------------------------------------------- large inputs: no int overflow ----

    [Theory(Timeout = TimeoutMs)]
    [InlineData(3)]     // (int)min(4 GiB, 3 GiB) was negative: new byte[...] threw OverflowException
    [InlineData(5)]     // (int)min(4 GiB, 5 GiB) was 0: the hit was silently dropped
    public async Task A_header_with_gigabytes_after_it_is_carved_without_overflow(long imageGiB)
    {
        // The shape of the old defaults: no footer, no validator, a 4 GiB maximum.
        var footerless = new CarveSignature("RAW", "bin", "RAWHDR!!"u8.ToArray(), null, 4 * GiB);
        await using var image = new SparseStream(imageGiB * GiB);
        image.Put(4096, "RAWHDR!!"u8);

        var hits = await Carve(image, [footerless], _out, unsizedLimit: 64 * 1024);

        var hit = hits.Should().ContainSingle().Subject;
        hit.Offset.Should().Be(4096);
        hit.Length.Should().Be(64 * 1024, "an object with no length and no trailer is carved up to the unsized limit");
        hit.Validated.Should().BeTrue();
        new FileInfo(hit.OutputPath!).Length.Should().Be(64 * 1024);
        image.LargestReadRequest.Should().BeLessThanOrEqualTo(4 * (int)MiB + FileCarver.HeadBytes);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task A_multi_gigabyte_object_is_reported_from_its_header_without_reading_it()
    {
        // A RIFF/AVI that says it is 2.5 GiB, inside a 3 GiB image. Without an output directory
        // nothing needs extracting; with the old code the extent was allocated and read anyway.
        const long declared = 2 * GiB + 512 * MiB;
        var header = new byte[16];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(declared - 8));
        "AVI LIST"u8.CopyTo(header.AsSpan(8));

        await using var image = new SparseStream(3 * GiB);
        image.Put(1 * MiB, header);

        var hits = await Carve(image, [Default("avi")]);

        var hit = hits.Should().ContainSingle().Subject;
        hit.Offset.Should().Be(1 * MiB);
        hit.Length.Should().Be(declared);
        hit.Validated.Should().BeTrue();
        image.BytesRead.Should().BeLessThan(3 * GiB + 64 * MiB, "one pass over the image, plus nothing for the object itself");
    }

    // ------------------------------------------------------------- streaming, not buffering ----

    [Fact(Timeout = TimeoutMs)]
    public async Task An_object_larger_than_the_window_is_streamed_to_disk_in_bounded_reads()
    {
        const int size = 24 * 1024 * 1024;
        await using var image = new SparseStream(64 * MiB);
        image.Put(2 * MiB, BmpHeader(size));
        image.Put(2 * MiB + size - 4, [0xDE, 0xAD, 0xBE, 0xEF]);

        var hits = await Carve(image, [Default("bmp")], _out);

        var hit = hits.Should().ContainSingle().Subject;
        hit.Length.Should().Be(size);
        var file = new FileInfo(hit.OutputPath!);
        file.Length.Should().Be(size);
        await using (var fs = file.OpenRead())
        {
            fs.Position = size - 4;
            var tail = new byte[4];
            fs.ReadExactly(tail);
            tail.Should().Equal(0xDE, 0xAD, 0xBE, 0xEF);
        }

        // Buffering the extent showed up as one read request the size of the object.
        image.LargestReadRequest.Should().BeLessThanOrEqualTo(4 * (int)MiB + FileCarver.HeadBytes,
            "the object is copied through a fixed buffer, never read into one array");
    }

    // ------------------------------------------------- noise: coincidental headers cost nothing ----

    [Fact(Timeout = TimeoutMs)]
    public async Task Random_data_does_not_produce_gigabytes_of_carved_junk()
    {
        // 24 MiB of noise holds ~366 "MZ" and ~366 "BM". The old carver read 200 MiB for each
        // MZ and wrote up to 50 MiB for each BM.
        var noise = new byte[24 * MiB];
        new Random(20261112).NextBytes(noise);
        await using var image = new CountingStream(noise);

        var hits = await Carve(image, null, _out);

        hits.Count.Should().BeLessThanOrEqualTo(4, "a two-byte magic number in noise is not a file");
        BytesWritten().Should().BeLessThan(256 * 1024);
        image.BytesRead.Should().BeLessThan(3L * noise.Length, "rejecting a coincidental header must not cost a read of its maximum length");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Densely_repeated_headers_do_not_multiply_the_output()
    {
        // Hostile: a valid 1 MiB BMP whose pixel area is nothing but more BMP headers claiming
        // 1 MiB each. They lie inside an object already carved and are not carved again.
        var bmp = new byte[1 * MiB];
        for (int i = 0; i + 64 <= bmp.Length; i += 64)
        {
            BmpHeader((int)MiB).CopyTo(bmp, i);
        }
        var image = new byte[4 * MiB];
        bmp.CopyTo(image, 4096);

        var hits = await Carve(new MemoryStream(image), [Default("bmp")], _out);

        hits.Should().ContainSingle().Which.Offset.Should().Be(4096);
        BytesWritten().Should().Be(MiB);
    }

    [Fact(Timeout = 60_000)]
    public async Task Headers_that_each_lead_into_a_long_walk_to_nowhere_do_not_stall_the_carve()
    {
        // Hostile: a JPEG start-of-image every 16 bytes, each followed by a chain of segments
        // that hops through the rest of the pattern and never reaches an end-of-image. Every
        // one of the half-million headers costs a walk of thousands of segments; unbounded,
        // that is billions of reads for 8 MiB of input.
        var pattern = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x0E, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var image = new byte[8 * MiB];
        for (int i = 0; i < image.Length; i += pattern.Length)
        {
            pattern.CopyTo(image, i);
        }

        var hits = await Carve(new MemoryStream(image), [Default("jpg")], _out);

        hits.Should().OnlyContain(h => !h.Validated && h.OutputPath == null);
        hits.Should().Contain(h => h.Note!.Contains("scan allowance"), "once the allowance is spent the rest are reported as not examined");
        Directory.GetFiles(_out).Should().BeEmpty();
    }

    // ------------------------------------------------------------------ built-in rule set ----

    [Fact]
    public void No_two_default_rules_share_a_header()
    {
        var keys = CarveSignatures.Defaults.Select(s => s.HeaderOffset + ":" + Convert.ToHexString(s.Header)).ToList();
        keys.Should().OnlyHaveUniqueItems("ZIP/DOCX and DOC/MSG used to be listed twice, so every such object was carved twice");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task A_zip_is_carved_once_as_a_whole_not_once_per_entry_and_rule()
    {
        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in new[] { "a.txt", "b.txt", "word/document.xml" })
                {
                    using var entry = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
                    entry.Write(Encoding.ASCII.GetBytes(new string('x', 300) + name));
                }
            }
            zip = ms.ToArray();
        }
        var image = new byte[64 * 1024];
        zip.CopyTo(image, 8192);

        var hits = await Carve(new MemoryStream(image), null, _out);

        // Was six hits (three local file headers × the ZIP and DOCX rules), each carved to the
        // rule's maximum.
        var hit = hits.Should().ContainSingle().Subject;
        hit.Extension.Should().Be("zip");
        hit.Offset.Should().Be(8192);
        hit.Length.Should().Be(zip.Length);
        (await File.ReadAllBytesAsync(hit.OutputPath!)).Should().Equal(zip);
    }

    // ------------------------------------------------------- length from the format header ----

    [Fact(Timeout = TimeoutMs)]
    public async Task Bmp_length_comes_from_its_header_and_a_bare_BM_is_not_a_bitmap()
    {
        var bmp = new byte[54 + 30 * 3 * 10 + 20];
        BmpHeader(bmp.Length, width: 30, height: 10).CopyTo(bmp, 0);
        Array.Fill(bmp, (byte)0x7F, 54, bmp.Length - 54);

        var image = new byte[32 * 1024];
        "BM"u8.CopyTo(image.AsSpan(100));                       // just the two letters
        Encoding.ASCII.GetBytes("BMW BMX SUBMIT").CopyTo(image, 300);
        bmp.CopyTo(image, 5000);

        var hits = await Carve(new MemoryStream(image), [Default("bmp")], _out);

        var hit = hits.Should().ContainSingle().Subject;
        hit.Offset.Should().Be(5000);
        hit.Length.Should().Be(bmp.Length);
        hit.Note.Should().Contain("format header");
        (await File.ReadAllBytesAsync(hit.OutputPath!)).Should().Equal(bmp);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Riff_files_start_at_RIFF_and_are_sized_by_the_chunk_length()
    {
        var wav = Riff("WAVE", "fmt ", 16, 1000);
        var avi = Riff("AVI ", "LIST", 64, 5000);
        var image = new byte[64 * 1024];
        wav.CopyTo(image, 1000);
        avi.CopyTo(image, 20_000);
        "RIFF"u8.CopyTo(image.AsSpan(50_000));                  // RIFF with no recognised form

        var hits = await Carve(new MemoryStream(image), null, _out);

        hits.Select(h => (h.Extension, h.Offset, h.Length)).Should().Equal(
            ("wav", 1000L, (long)wav.Length),
            ("avi", 20_000L, (long)avi.Length));
        (await File.ReadAllBytesAsync(hits[0].OutputPath!)).Should().Equal(wav);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Pe_length_is_the_end_of_the_last_section_and_a_bare_MZ_is_not_an_executable()
    {
        var pe = MinimalPe(sectionRawOffset: 0x400, sectionRawSize: 0x1200);
        var image = new byte[64 * 1024];
        Encoding.ASCII.GetBytes("MZ is how DOS executables begin; AMZN; MZ").CopyTo(image, 64);
        pe.CopyTo(image, 0x2000);

        var hits = await Carve(new MemoryStream(image), [Default("exe")], _out);

        var hit = hits.Should().ContainSingle().Subject;
        hit.Offset.Should().Be(0x2000);
        hit.Length.Should().Be(0x1600);
        hit.Validated.Should().BeTrue();
        new FileInfo(hit.OutputPath!).Length.Should().Be(0x1600);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Mp4_starts_four_bytes_before_ftyp_and_ends_after_its_last_box()
    {
        var mp4 = Concat(Box("ftyp", [.. "isom"u8, 0, 0, 2, 0, .. "isommp42"u8]), Box("moov", new byte[200]), Box("mdat", new byte[3000]));
        var image = new byte[32 * 1024];
        mp4.CopyTo(image, 4096);
        Array.Fill(image, (byte)0xEE, 4096 + mp4.Length, 512);     // whatever follows on disk

        var hits = await Carve(new MemoryStream(image), null, _out);

        var hit = hits.Should().ContainSingle().Subject;
        hit.Extension.Should().Be("mp4");
        hit.Offset.Should().Be(4096, "the file begins at the box size, not at the 'ftyp' tag");
        hit.Length.Should().Be(mp4.Length);
        (await File.ReadAllBytesAsync(hit.OutputPath!)).Should().Equal(mp4);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task A_jpeg_with_an_embedded_thumbnail_is_carved_whole_and_once()
    {
        var thumbnail = Concat([0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x04, 0x01, 0x02], [0xFF, 0xDA, 0x00, 0x02, 0x11, 0x22], [0xFF, 0xD9]);
        var app1 = Concat([0xFF, 0xE1, (byte)((thumbnail.Length + 8) >> 8), (byte)(thumbnail.Length + 8)], "Exif\0\0"u8.ToArray(), thumbnail);
        var jpeg = Concat(
            [0xFF, 0xD8],
            app1,
            [0xFF, 0xDB, 0x00, 0x04, 0x05, 0x06],
            [0xFF, 0xDA, 0x00, 0x04, 0x01, 0x02],
            // Scan data: a stuffed FF, a restart marker, then more data.
            [0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD3, 0x56, 0x78, 0x9A],
            [0xFF, 0xD9]);
        var image = new byte[16 * 1024];
        jpeg.CopyTo(image, 2048);

        var hits = await Carve(new MemoryStream(image), null, _out);

        // Stopping at the first FF D9 cut the image at the end of its own thumbnail, and the
        // thumbnail's header was then carved as a second file.
        var hit = hits.Should().ContainSingle().Subject;
        hit.Extension.Should().Be("jpg");
        hit.Offset.Should().Be(2048);
        hit.Length.Should().Be(jpeg.Length);
        (await File.ReadAllBytesAsync(hit.OutputPath!)).Should().Equal(jpeg);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Sqlite_length_is_page_size_times_page_count()
    {
        var db = new byte[3 * 4096];
        "SQLite format 3\0"u8.CopyTo(db);
        BinaryPrimitives.WriteUInt16BigEndian(db.AsSpan(16), 4096);
        db[18] = 1; db[19] = 1; db[21] = 64; db[22] = 32; db[23] = 32;
        BinaryPrimitives.WriteUInt32BigEndian(db.AsSpan(24), 7);     // change counter
        BinaryPrimitives.WriteUInt32BigEndian(db.AsSpan(28), 3);     // size in pages
        BinaryPrimitives.WriteUInt32BigEndian(db.AsSpan(92), 7);     // version-valid-for
        var image = new byte[64 * 1024];
        db.CopyTo(image, 8192);

        var hits = await Carve(new MemoryStream(image), [Default("sqlite")]);

        hits.Should().ContainSingle().Which.Length.Should().Be(3 * 4096);
    }

    // --------------------------------------------------- objects that cannot be completed ----

    [Fact(Timeout = TimeoutMs)]
    public async Task An_object_whose_header_declares_more_than_the_source_holds_is_reported_but_not_written()
    {
        var image = new byte[16 * 1024];
        BmpHeader(1024 * 1024).CopyTo(image, 4096);

        var hits = await Carve(new MemoryStream(image), [Default("bmp")], _out);

        var hit = hits.Should().ContainSingle().Subject;
        hit.Validated.Should().BeFalse();
        hit.OutputPath.Should().BeNull();
        hit.Note.Should().Contain("truncated");
        Directory.GetFiles(_out).Should().BeEmpty();
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task A_built_in_rule_whose_footer_never_arrives_writes_nothing()
    {
        var image = new byte[16 * 1024];
        Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << >> endobj\n").CopyTo(image, 512);

        var hits = await Carve(new MemoryStream(image), [Default("pdf")], _out);

        var hit = hits.Should().ContainSingle().Subject;
        hit.Validated.Should().BeFalse();
        hit.OutputPath.Should().BeNull();
        Directory.GetFiles(_out).Should().BeEmpty("the old carver wrote the rule's maximum length here");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task An_unsized_object_ends_where_the_next_one_of_its_kind_begins()
    {
        var raw = new CarveSignature("RAW", "bin", "HDR!"u8.ToArray(), null, 1024 * 1024);
        var image = new byte[16 * 1024];
        "HDR!"u8.CopyTo(image.AsSpan(1000));
        "HDR!"u8.CopyTo(image.AsSpan(3000));

        var hits = await Carve(new MemoryStream(image), [raw], _out);

        hits.Select(h => (h.Offset, h.Length)).Should().Equal((1000L, 2000L), (3000L, 16 * 1024L - 3000));
    }

    // ------------------------------------------------------------------ slices and offsets ----

    [Fact(Timeout = TimeoutMs)]
    public async Task Objects_at_the_same_offset_in_two_regions_are_written_to_two_files()
    {
        var png = new CarveSignature("PNG", "png",
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82], 1024 * 1024);
        static byte[] Png(byte fill) => Concat([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], Enumerable.Repeat(fill, 40).ToArray(), [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82]);

        var image = new byte[16384];
        Png(0xAA).CopyTo(image, 4096 + 16);
        Png(0xBB).CopyTo(image, 8192 + 16);

        var carver = new SlackUnallocCarver(new FileCarver([png]));
        var hits = new List<CarveHit>();
        await foreach (var h in carver.CarveRegionsAsync(new MemoryStream(image),
            [new CarveRegion("unalloc", 4096, 4096), new CarveRegion("unalloc", 8192, 4096)], _out))
        {
            hits.Add(h);
        }

        hits.Select(h => h.Offset).Should().Equal(4096 + 16, 8192 + 16);
        hits.Select(h => h.OutputPath).Should().OnlyHaveUniqueItems("both used to be named after the region-relative offset");
        (await File.ReadAllBytesAsync(hits[0].OutputPath!))[8].Should().Be(0xAA);
        (await File.ReadAllBytesAsync(hits[1].OutputPath!))[8].Should().Be(0xBB);
        Path.GetFileName(hits[1].OutputPath!).Should().StartWith((8192 + 16).ToString("X12"));
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task A_region_outside_the_image_is_skipped_not_fatal()
    {
        var carver = new SlackUnallocCarver(new FileCarver([Default("bmp")]));
        var hits = new List<CarveHit>();
        await foreach (var h in carver.CarveRegionsAsync(new MemoryStream(new byte[8192]),
            [new CarveRegion("bad", -5, 100), new CarveRegion("bad", 1 << 20, 4096), new CarveRegion("ok", 0, long.MaxValue)]))
        {
            hits.Add(h);
        }
        hits.Should().BeEmpty();
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Carving_starts_from_the_streams_current_position()
    {
        var bmp = new byte[6 * 1024 * 1024];
        BmpHeader(bmp.Length).CopyTo(bmp, 0);
        bmp[^1] = 0x42;
        var image = new byte[8 * 1024 * 1024];
        bmp.CopyTo(image, 1_000_000);

        var stream = new MemoryStream(image) { Position = 600_000 };
        var hits = await Carve(stream, [Default("bmp")], _out);

        // Offsets are relative to where carving began; the bytes must still be the right ones
        // (extraction used to seek as if the stream had started at zero).
        var hit = hits.Should().ContainSingle().Subject;
        hit.Offset.Should().Be(400_000);
        (await File.ReadAllBytesAsync(hit.OutputPath!)).Should().Equal(bmp);
    }

    // ------------------------------------------------------------------------- builders ----

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] BmpHeader(int fileSize, int width = 64, int height = 64)
    {
        var h = new byte[54];
        "BM"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(2), (uint)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(28), 24);
        return h;
    }

    private static byte[] Riff(string form, string firstChunk, int firstChunkSize, int payload)
    {
        var total = 12 + 8 + firstChunkSize + payload;
        var b = new byte[total];
        "RIFF"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(total - 8));
        Encoding.ASCII.GetBytes(form + firstChunk).CopyTo(b, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), (uint)firstChunkSize);
        Array.Fill(b, (byte)0x33, 20, total - 20);
        return b;
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var b = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        payload.CopyTo(b, 8);
        return b;
    }

    private static byte[] MinimalPe(int sectionRawOffset, int sectionRawSize)
    {
        const int peOffset = 0x80;
        const int optionalSize = 0xE0;
        var b = new byte[sectionRawOffset + sectionRawSize];
        "MZ"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x3C), peOffset);
        "PE\0\0"u8.CopyTo(b.AsSpan(peOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(peOffset + 4), 0x14C);            // machine
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(peOffset + 6), 1);                // sections
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(peOffset + 20), optionalSize);
        var optional = b.AsSpan(peOffset + 24);
        BinaryPrimitives.WriteUInt16LittleEndian(optional, 0x10B);                           // PE32
        BinaryPrimitives.WriteUInt32LittleEndian(optional[60..], 0x400);                     // SizeOfHeaders
        BinaryPrimitives.WriteUInt32LittleEndian(optional[92..], 16);                        // NumberOfRvaAndSizes
        var section = b.AsSpan(peOffset + 24 + optionalSize);
        ".text\0\0\0"u8.CopyTo(section);
        BinaryPrimitives.WriteUInt32LittleEndian(section[16..], (uint)sectionRawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(section[20..], (uint)sectionRawOffset);
        return b;
    }

    // --------------------------------------------------------------------------- streams ----

    /// <summary>
    /// A seekable stream of any length that is all zeros except where bytes were put — a
    /// multi-gigabyte image without the memory — and that records how it was read.
    /// </summary>
    private sealed class SparseStream(long length) : Stream
    {
        private readonly List<(long Offset, byte[] Bytes)> _patches = [];
        private long _position;

        public long BytesRead { get; private set; }
        public int LargestReadRequest { get; private set; }

        public void Put(long offset, ReadOnlySpan<byte> bytes) => _patches.Add((offset, bytes.ToArray()));

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            LargestReadRequest = Math.Max(LargestReadRequest, buffer.Length);
            var n = (int)Math.Max(0, Math.Min(buffer.Length, length - _position));
            if (n == 0)
            {
                return 0;
            }
            var dest = buffer[..n];
            dest.Clear();
            foreach (var (patchOffset, bytes) in _patches)
            {
                var from = Math.Max(patchOffset, _position);
                var to = Math.Min(patchOffset + bytes.Length, _position + n);
                if (from < to)
                {
                    bytes.AsSpan((int)(from - patchOffset), (int)(to - from)).CopyTo(dest[(int)(from - _position)..]);
                }
            }
            _position += n;
            BytesRead += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => new(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>In-memory image that counts the bytes read from it.</summary>
    private sealed class CountingStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = base.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        // MemoryStream routes span and async reads of a derived type through the array
        // overload above, so that is the one place to count.
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => new(Read(buffer.Span));
    }
}
