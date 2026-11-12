using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cinder.Imaging.Aff4;
using Cinder.Imaging.Ewf;
using FluentAssertions;
using Xunit;

namespace Cinder.Imaging.Tests;

/// <summary>
/// One test (or a small group) per defect found in the imaging review. Each drives the public
/// surface the way a caller would and fails against the code as it was before the fix.
/// </summary>
public sealed class RegressionTests : IDisposable
{
    private const int MiB = 1024 * 1024;
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-regr").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- 1: the section ceiling must not apply to chunk data ------------------------------

    [Fact]
    public async Task Ewf_reader_opens_a_container_whose_sectors_section_is_larger_than_64_MiB()
    {
        // 70 MiB stored uncompressed is one `sectors` run of ~70 MiB — small next to any real
        // acquisition, where a run holds up to 512 MiB.
        var media = EwfBuilder.Media(70 * MiB);
        var path = Path.Combine(_dir, "big.E01");
        using (var w = new EwfWriter(path, new EwfWriter.Options { Compress = false }))
        {
            w.Write(media);
            w.Finish(MD5.HashData(media), SHA1.HashData(media));
        }

        using var r = EwfReader.Open(path);
        r.MediaSize.Should().Be(media.Length);
        var v = await r.VerifyAsync();
        v.Verified.Should().BeTrue(v.Summary());
    }

    // ---- 2: EnCase writes next/done with a section size of 0 -----------------------------

    [Fact]
    public async Task Ewf_reader_accepts_next_and_done_sections_that_declare_size_zero()
    {
        var media = EwfBuilder.Media(12 * MiB);
        var path = Path.Combine(_dir, "encase.E01");
        List<string> segments;
        using (var w = new EwfWriter(path, new EwfWriter.Options { MaxSegmentBytes = 4 * MiB, Compress = false }))
        {
            w.Write(media);
            w.Finish(MD5.HashData(media), SHA1.HashData(media));
            segments = [.. w.SegmentPaths];
        }

        var patched = segments.SelectMany(ZeroTerminatorSizes).ToList();
        patched.Should().Contain("next").And.Contain("done");

        using var r = EwfReader.Open(path);
        r.SegmentCount.Should().Be(segments.Count);
        var v = await r.VerifyAsync();
        v.Verified.Should().BeTrue(v.Summary());
    }

    /// <summary>Rewrites every next/done descriptor in a segment the way EnCase lays it out: size 0.</summary>
    private static List<string> ZeroTerminatorSizes(string segmentPath)
    {
        var b = File.ReadAllBytes(segmentPath);
        var seen = new List<string>();
        long pos = 13;
        while (true)
        {
            var type = Encoding.ASCII.GetString(b, (int)pos, 16).TrimEnd('\0');
            var next = BitConverter.ToInt64(b, (int)pos + 16);
            if (type is "next" or "done")
            {
                BitConverter.TryWriteBytes(b.AsSpan((int)pos + 24), 0L);
                BitConverter.TryWriteBytes(b.AsSpan((int)pos + 72), EwfWriter.Adler32(b.AsSpan((int)pos, 72)));
                seen.Add(type);
            }
            if (next == pos || next == 0)
            {
                break;
            }
            pos = next;
        }
        File.WriteAllBytes(segmentPath, b);
        return seen;
    }

    // ---- 3: zero-fill is bounded ----------------------------------------------------------

    [Fact(Timeout = 120_000)]
    public async Task Zero_fill_never_runs_past_the_known_length_of_a_source_that_stops_answering()
    {
        const int length = 4 * MiB;
        const int failFrom = 1 * MiB;
        var outPath = Path.Combine(_dir, "gone.dd");

        // Before the fix this loop never ended; the guard turns that into a failure, not a hang.
        using var cts = new CancellationTokenSource();
        var guard = new InlineProgress(p => { if (p.BytesRead > 2L * length) cts.Cancel(); });

        var result = await new InProcessImager().ImageStreamAsync(
            new UnreadableFromStream(length, failFrom),
            new ImageJob("gone", outPath, ImageFormat.Raw, ReadErrorRetries: 0), guard, cts.Token);

        result.BytesWritten.Should().Be(length, "the image is as long as the source, not longer");
        new FileInfo(outPath).Length.Should().Be(length);
        result.BadSectors.Should().Be((length - failFrom) / 512);
    }

    [Fact(Timeout = 120_000)]
    public async Task An_error_at_the_end_of_a_device_is_the_end_of_the_source_not_bad_sectors()
    {
        // Raw devices on Windows fail a read that reaches past the last sector instead of
        // returning a short or empty read. 3 MiB + 4 KiB so the last block straddles the end.
        var data = EwfBuilder.Media(3 * MiB + 4096);
        var outPath = Path.Combine(_dir, "device.dd");

        using var cts = new CancellationTokenSource();
        var guard = new InlineProgress(p => { if (p.BytesRead > 2L * data.Length) cts.Cancel(); });

        var result = await new InProcessImager().ImageStreamAsync(
            new ErrorPastEndStream(data),
            new ImageJob("device", outPath, ImageFormat.Raw, ReadErrorRetries: 0), guard, cts.Token);

        result.BytesWritten.Should().Be(data.Length);
        result.BadSectors.Should().Be(0);
        (await File.ReadAllBytesAsync(outPath)).Should().Equal(data);
    }

    // ---- 4: a stored AFF4 chunk is returned as stored ---------------------------------------

    [Theory]
    [InlineData("pyaff4")]
    [InlineData("Evimetry 4.2")]
    public void Aff4_stored_chunk_that_is_itself_a_zlib_stream_is_read_back_raw(string tool)
    {
        // Chunk 0 of the media is the first 4096 bytes of somebody's zlib file. It does not
        // compress, so a standard writer stores it raw (index length == chunk size).
        const int cs = 4096;
        var inner = EwfBuilder.CompressibleMedia(16384, seed: 7);
        var chunk0 = Zlib(inner).AsSpan(0, cs).ToArray();
        var chunk1 = EwfBuilder.CompressibleMedia(cs, seed: 99);
        var media = chunk0.Concat(chunk1).ToArray();

        var path = Path.Combine(_dir, "stored.aff4");
        Aff4Fixture.Write(path, cs, media.Length, [chunk0, Zlib(chunk1)], tool: tool);

        using var r = Aff4Reader.Open(path);
        using var s = r.OpenStream();
        var back = ReadAll(s);
        back.AsSpan(0, cs).ToArray().Should().Equal(chunk0, "a stored chunk is evidence as it stands, never something to inflate");
        back.AsSpan(cs).ToArray().Should().Equal(chunk1);
        r.DamagedChunks.Should().BeEmpty();
    }

    // ---- 5: data that cannot be located is not zeros ----------------------------------------

    [Fact]
    public void Aff4_reader_refuses_a_map_whose_stream_data_is_not_in_the_container()
    {
        const int cs = 4096;
        var media = EwfBuilder.CompressibleMedia(3 * cs);
        var path = Path.Combine(_dir, "nostream.aff4");
        Aff4Fixture.Write(path, cs, media.Length, Chunks(media, cs), includeBevies: false);

        var act = () => Aff4Reader.Open(path);
        act.Should().Throw<InvalidDataException>().WithMessage("*not in this container*");

        // And through the opener every tool uses.
        var act2 = () => EvidenceOpener.Open(path);
        act2.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Aff4_reader_refuses_a_map_range_whose_target_is_not_listed()
    {
        const int cs = 4096;
        var media = EwfBuilder.CompressibleMedia(3 * cs);
        var path = Path.Combine(_dir, "badtarget.aff4");
        Aff4Fixture.Write(path, cs, media.Length, Chunks(media, cs), targetId: 5);

        var act = () => Aff4Reader.Open(path);
        act.Should().Throw<InvalidDataException>().WithMessage("*target #5*");
    }

    [Theory]
    [InlineData("aff4://33333333-3333-3333-3333-333333333333/data", "aff4%3A%2F%2F33333333-3333-3333-3333-333333333333/data")]
    [InlineData("aff4://11111111-1111-1111-1111-111111111111/disk/data", "disk/data")]
    public void Aff4_reader_finds_bevies_under_the_member_namings_other_writers_use(string streamUrn, string memberPrefix)
    {
        // Path slashes kept with only the scheme encoded, and names relative to the volume URN.
        const int cs = 4096;
        var media = EwfBuilder.CompressibleMedia(5 * cs + 123);
        var path = Path.Combine(_dir, "naming.aff4");
        Aff4Fixture.Write(path, cs, media.Length, Chunks(media, cs), streamUrn: streamUrn, memberPrefix: memberPrefix);

        using var r = Aff4Reader.Open(path);
        using var s = r.OpenStream();
        ReadAll(s).Should().Equal(media);
        r.DamagedChunks.Should().BeEmpty();
    }

    // ---- 6: a damaged source is never a successful conversion --------------------------------

    [Fact]
    public async Task Conversion_of_a_damaged_container_with_no_recorded_hash_is_not_reported_as_a_success()
    {
        var media = EwfBuilder.Media(4096 * 4);
        var e01 = Path.Combine(_dir, "damaged-nohash.E01");
        await File.WriteAllBytesAsync(e01, EwfBuilder.Build(media,
            new EwfBuilder.Options { Compress = true, CorruptChunkIndex = 1, IncludeDigest = false }));

        var r = await ImageConverter.ConvertAsync(e01, Path.Combine(_dir, "damaged-nohash.dd"), ImageFormat.Raw);

        r.DamagedChunks.Should().BeGreaterThan(0);
        r.MatchesRecorded.Should().BeFalse("the output holds zeros where the source chunk could not be decoded");
        r.SourceMatchesRecorded.Should().BeFalse();
    }

    // ---- 7: the recorded hash is the hash of the stored media --------------------------------

    [Fact]
    public async Task Ewf_image_of_a_source_with_a_ragged_tail_verifies_against_its_recorded_hash()
    {
        var data = EwfBuilder.Media(MiB + 100);
        var padded = data.Concat(new byte[412]).ToArray();   // up to the next 512-byte sector
        var raw = Path.Combine(_dir, "ragged.dd");
        await File.WriteAllBytesAsync(raw, data);
        var e01 = Path.Combine(_dir, "ragged.E01");

        var r = await ImageConverter.RawToEwfAsync(raw, e01);
        r.MatchesRecorded.Should().BeTrue();

        using (var reader = EwfReader.Open(e01))
        {
            reader.MediaSize.Should().Be(padded.Length);
            reader.RecordedMd5.Should().Be(Convert.ToHexStringLower(MD5.HashData(padded)), "what ewfverify and FTK Imager will compute");
            reader.RecordedSha1.Should().Be(Convert.ToHexStringLower(SHA1.HashData(padded)));
            var v = await reader.VerifyAsync();
            v.Verified.Should().BeTrue(v.Summary());
        }

        using var log = JsonDocument.Parse(await File.ReadAllTextAsync(e01 + ".log.json"));
        log.RootElement.GetProperty("logical_source_bytes").GetInt64().Should().Be(data.Length);
        log.RootElement.GetProperty("padding_bytes").GetInt64().Should().Be(412);
        log.RootElement.GetProperty("image_media_bytes").GetInt64().Should().Be(padded.Length);
        log.RootElement.GetProperty("logical_md5").GetString().Should().Be(Convert.ToHexStringLower(MD5.HashData(data)));
        log.RootElement.GetProperty("md5").GetString().Should().Be(Convert.ToHexStringLower(MD5.HashData(padded)));
    }

    [Theory]
    [InlineData(ImageFormat.Vhd)]
    [InlineData(ImageFormat.Vhdx)]
    public async Task Virtual_disk_of_a_source_with_a_ragged_tail_reproduces_its_digest(ImageFormat format)
    {
        var data = EwfBuilder.Media(2 * MiB + 100);
        var padded = data.Concat(new byte[412]).ToArray();
        var raw = Path.Combine(_dir, "ragged-vd.dd");
        await File.WriteAllBytesAsync(raw, data);

        var r = await ImageConverter.ConvertAsync(raw, Path.Combine(_dir, "ragged" + format.DefaultExtension()), format);

        r.MatchesRecorded.Should().BeTrue();
        r.Image.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(padded)));
        r.Image.BytesWritten.Should().Be(data.Length);
    }

    // ---- 8: overlapping table sections ------------------------------------------------------

    [Fact]
    public void Ewf_reader_rejects_overlapping_table_sections_before_allocating_for_them()
    {
        // 32 table sections 100 bytes apart, each claiming everything up to the end of an
        // 8 MiB file: ~2 million entries apiece, 64 million between them.
        const int fileLength = 8 * MiB;
        const int tables = 32;
        var f = new byte[fileLength];
        new byte[] { 0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00, 1, 1, 0, 0, 0 }.CopyTo(f, 0);

        void Descriptor(long at, string type, long next, long size)
        {
            Encoding.ASCII.GetBytes(type).CopyTo(f, at);
            BitConverter.TryWriteBytes(f.AsSpan((int)at + 16), next);
            BitConverter.TryWriteBytes(f.AsSpan((int)at + 24), size);
        }

        long pos = 13;
        Descriptor(pos, "volume", pos + 76 + 1052, 76 + 1052);
        BitConverter.TryWriteBytes(f.AsSpan((int)pos + 76 + 4), 1u);
        BitConverter.TryWriteBytes(f.AsSpan((int)pos + 76 + 8), 64u);
        BitConverter.TryWriteBytes(f.AsSpan((int)pos + 76 + 12), 512u);
        BitConverter.TryWriteBytes(f.AsSpan((int)pos + 76 + 16), 64UL);
        pos += 76 + 1052;
        for (int i = 0; i < tables; i++)
        {
            long dataStart = pos + 76, dataLen = fileLength - dataStart;
            Descriptor(pos, "table", pos + 100, 76 + dataLen);
            BitConverter.TryWriteBytes(f.AsSpan((int)dataStart), (uint)((dataLen - 24) / 4));
            pos += 100;
        }
        Descriptor(pos, "done", pos, 76);

        var stream = new MemoryStream(f);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = () => new EwfReader(stream);
        act.Should().Throw<InvalidDataException>().WithMessage("*overlap*");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().BeLessThan(64L * MiB, "one table's worth at most, not one per section (it was 805 MB)");
    }

    // ---- 9: the AFF4 writer follows the stored-chunk convention ------------------------------

    [Fact]
    public void Aff4_writer_never_stores_a_compressed_chunk_at_exactly_the_chunk_size()
    {
        const int cs = 4096;
        var ambiguous = ChunkDeflatingToExactly(cs);
        var noise = EwfBuilder.Media(cs, seed: 5);                   // does not compress at all
        var text = EwfBuilder.CompressibleMedia(cs, seed: 6);
        var tail = EwfBuilder.CompressibleMedia(1000, seed: 8);      // short final chunk
        byte[][] plain = [noise, ambiguous, text, tail];
        var media = plain.SelectMany(c => c).ToArray();

        var path = Path.Combine(_dir, "convention.aff4");
        using (var w = new Aff4Writer(path, new Aff4Writer.Options { ChunkSize = cs }))
        {
            w.Write(media);
            w.Finish(MD5.HashData(media), SHA1.HashData(media), null);
        }

        byte[] index, bevy;
        using (var zip = ZipFile.OpenRead(path))
        {
            index = ReadAll(zip.Entries.Single(e => e.FullName.EndsWith("/00000000.index", StringComparison.Ordinal)).Open());
            bevy = ReadAll(zip.Entries.Single(e => e.FullName.EndsWith("/00000000", StringComparison.Ordinal)).Open());
        }

        (index.Length / 12).Should().Be(plain.Length);
        var lengths = new List<int>();
        for (int i = 0; i < plain.Length; i++)
        {
            var offset = (int)BitConverter.ToInt64(index, i * 12);
            var length = BitConverter.ToInt32(index, i * 12 + 8);
            lengths.Add(length);
            var stored = bevy.AsSpan(offset, length).ToArray();

            // The rule every AFF4 reader applies.
            var decoded = length == cs ? stored : Inflate(stored);
            decoded.Should().Equal(plain[i], $"chunk {i} (stored length {length}) must decode by the standard rule");
        }
        lengths[0].Should().Be(cs, "an incompressible chunk is stored raw");
        lengths[1].Should().Be(cs, "a chunk that would deflate to exactly the chunk size is stored raw too");
        lengths[2].Should().BeLessThan(cs - 16);
        lengths[3].Should().NotBe(cs);

        using var r = Aff4Reader.Open(path);
        using var s = r.OpenStream();
        ReadAll(s).Should().Equal(media);
        r.DamagedChunks.Should().BeEmpty();
    }

    [Fact]
    public void Aff4_reader_still_inflates_full_size_chunks_in_containers_from_the_first_cinder_writer()
    {
        // The first Cinder writer compressed every chunk and never stored one raw, so in its
        // containers (tool=Cinder, no stored-chunk marker) a chunk-size payload is zlib.
        const int cs = 4096;
        var ambiguous = ChunkDeflatingToExactly(cs);
        var packed = Zlib(ambiguous);
        packed.Length.Should().Be(cs);

        var legacy = Path.Combine(_dir, "legacy.aff4");
        Aff4Fixture.Write(legacy, cs, cs, [packed], tool: "Cinder");
        using (var r = Aff4Reader.Open(legacy))
        using (var s = r.OpenStream())
        {
            ReadAll(s).Should().Equal(ambiguous);
        }

        // The same bytes from any other writer are a stored chunk.
        var standard = Path.Combine(_dir, "standard.aff4");
        Aff4Fixture.Write(standard, cs, cs, [packed], tool: "pyaff4");
        using (var r = Aff4Reader.Open(standard))
        using (var s = r.OpenStream())
        {
            ReadAll(s).Should().Equal(packed);
        }
    }

    // ---- 10: nothing unfinished at the output path -------------------------------------------

    [Theory(Timeout = 120_000)]
    [InlineData(ImageFormat.Raw)]
    [InlineData(ImageFormat.Ewf)]
    [InlineData(ImageFormat.Aff4)]
    [InlineData(ImageFormat.Vhd)]
    [InlineData(ImageFormat.Vhdx)]
    public async Task A_cancelled_acquisition_leaves_nothing_behind(ImageFormat format)
    {
        var src = Path.Combine(_dir, "cancel-src.bin");
        await File.WriteAllBytesAsync(src, EwfBuilder.Media(8 * MiB));
        var outDir = Directory.CreateDirectory(Path.Combine(_dir, "cancel-" + format)).FullName;
        var outPath = Path.Combine(outDir, "image" + format.DefaultExtension());

        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.BytesRead >= 3L * MiB) cts.Cancel(); });

        var act = () => new InProcessImager().ImageAsync(new ImageJob(src, outPath, format, CompressionLevel: 0), progress, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        File.Exists(outPath).Should().BeFalse("a cancelled image must not sit where a finished one would");
        Directory.GetFiles(outDir).Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_acquisition_keeps_its_data_under_a_partial_name_and_logs_it_as_incomplete()
    {
        var outPath = Path.Combine(_dir, "failed.dd");
        var act = () => new InProcessImager().ImageStreamAsync(
            new BreaksAfterStream(2 * MiB, new InvalidDataException("container structure is broken at 2 MiB")),
            new ImageJob("broken", outPath, ImageFormat.Raw));
        await act.Should().ThrowAsync<InvalidDataException>();

        File.Exists(outPath).Should().BeFalse();
        File.Exists(outPath + ".sha256").Should().BeFalse();
        new FileInfo(outPath + ".partial").Length.Should().Be(2 * MiB);

        using var log = JsonDocument.Parse(await File.ReadAllTextAsync(outPath + ".log.json"));
        log.RootElement.GetProperty("complete").GetBoolean().Should().BeFalse();
        log.RootElement.GetProperty("status").GetString().Should().Be("incomplete");
        log.RootElement.GetProperty("error").GetString().Should().Contain("broken at 2 MiB");
        log.RootElement.GetProperty("segments").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(outPath + ".partial");
    }

    [Fact]
    public async Task A_finished_multi_segment_image_has_its_real_names_and_no_partial_files()
    {
        var data = EwfBuilder.Media(12 * MiB);
        var src = Path.Combine(_dir, "multi-src.bin");
        await File.WriteAllBytesAsync(src, data);
        var outDir = Directory.CreateDirectory(Path.Combine(_dir, "multi")).FullName;
        var outPath = Path.Combine(outDir, "image.E01");

        var result = await new InProcessImager().ImageAsync(new ImageJob(src, outPath, ImageFormat.Ewf, CompressionLevel: 0, SegmentSizeMiB: 4));

        Directory.GetFiles(outDir, "*.partial").Should().BeEmpty();
        Directory.GetFiles(outDir, "image.E??").Length.Should().BeGreaterThan(2);
        using var r = EwfReader.Open(outPath);
        r.RecordedMd5.Should().Be(result.Md5);
        (await r.VerifyAsync()).Verified.Should().BeTrue();

        using var log = JsonDocument.Parse(await File.ReadAllTextAsync(outPath + ".log.json"));
        log.RootElement.GetProperty("complete").GetBoolean().Should().BeTrue();
        log.RootElement.GetProperty("segments").EnumerateArray().Select(e => e.GetString()).Should().OnlyContain(p => File.Exists(p!));
    }

    // ---- 12: damage recovery works whatever the device's sector size -------------------------

    [Fact]
    public async Task A_bad_sector_on_a_4K_native_device_costs_that_sector_not_the_whole_block()
    {
        // A 4Kn drive refuses any read that is not a whole number of 4096-byte sectors, so a
        // fallback that only knows 512-byte reads fails on every sector of the block.
        var data = EwfBuilder.Media(2 * MiB);
        const long bad = MiB + 8 * 4096;
        var outPath = Path.Combine(_dir, "4kn.dd");

        var result = await new InProcessImager().ImageStreamAsync(
            new FourKnStream(data, bad),
            new ImageJob("4kn", outPath, ImageFormat.Raw, ReadErrorRetries: 0));

        var expected = (byte[])data.Clone();
        Array.Clear(expected, (int)bad, 4096);
        (await File.ReadAllBytesAsync(outPath)).Should().Equal(expected);
        result.BadSectors.Should().Be(8, "one 4096-byte sector, counted in 512-byte units");
    }

    // ---- 14: a zeroed digest field is not a digest -------------------------------------------

    [Fact]
    public async Task A_zero_filled_sha1_field_means_no_sha1_was_recorded()
    {
        var media = EwfBuilder.Media(32 * 1024);
        var container = EwfBuilder.Build(media, new EwfBuilder.Options { ForcedSha1 = new byte[20] });

        using var reader = new EwfReader(new MemoryStream(container));
        reader.RecordedSha1.Should().BeNull();
        reader.RecordedMd5.Should().NotBeNull();

        var v = await reader.VerifyAsync();
        v.Sha1Match.Should().BeNull();
        v.Md5Match.Should().BeTrue();
        v.Verified.Should().BeTrue(v.Summary());
    }

    [Fact]
    public async Task Ewf_writer_given_only_an_md5_produces_a_container_that_verifies()
    {
        var media = EwfBuilder.Media(256 * 1024);
        var md5Only = Path.Combine(_dir, "md5only.E01");
        using (var w = new EwfWriter(md5Only))
        {
            w.Write(media);
            w.Finish(MD5.HashData(media), null);
        }
        using (var r = EwfReader.Open(md5Only))
        {
            r.RecordedSha1.Should().BeNull();
            var v = await r.VerifyAsync();
            v.Verified.Should().BeTrue(v.Summary());
        }

        var noHash = Path.Combine(_dir, "nohash.E01");
        using (var w = new EwfWriter(noHash))
        {
            w.Write(media);
            w.Finish(null, null);
        }
        using (var r = EwfReader.Open(noHash))
        {
            r.RecordedMd5.Should().BeNull();
            r.RecordedSha1.Should().BeNull();
            var v = await r.VerifyAsync();
            v.Verified.Should().BeFalse();
            v.Summary().Should().StartWith("Unverifiable");
        }
    }

    // ---- 15: error2 never records a bad sector at the wrong place ----------------------------

    [Fact]
    public void Bad_sectors_beyond_the_32_bit_range_are_not_recorded_at_a_clamped_location()
    {
        var media = EwfBuilder.Media(64 * 1024);
        var path = Path.Combine(_dir, "far.E01");
        using (var w = new EwfWriter(path))
        {
            w.Write(media);
            w.AddBadSectors(10, 2);
            w.AddBadSectors(5_000_000_000L, 3);       // 2.3 TiB into a 512-byte-sector disk
            w.Finish(MD5.HashData(media), SHA1.HashData(media));
        }

        var entries = ReadError2(path);
        entries.Should().Equal((10u, 2u));
        entries.Should().NotContain(e => e.First == uint.MaxValue);
    }

    internal static List<(uint First, uint Count)> ReadError2(string path)
    {
        var b = File.ReadAllBytes(path);
        var result = new List<(uint, uint)>();
        long pos = 13;
        while (true)
        {
            var type = Encoding.ASCII.GetString(b, (int)pos, 16).TrimEnd('\0');
            var next = BitConverter.ToInt64(b, (int)pos + 16);
            if (type == "error2")
            {
                var data = (int)pos + 76;
                var n = (int)BitConverter.ToUInt32(b, data);
                for (int i = 0; i < n; i++)
                {
                    result.Add((BitConverter.ToUInt32(b, data + 520 + i * 8), BitConverter.ToUInt32(b, data + 524 + i * 8)));
                }
            }
            if (next == pos || next == 0)
            {
                break;
            }
            pos = next;
        }
        return result;
    }

    // ---- helpers ----------------------------------------------------------------------------

    internal static byte[] Zlib(ReadOnlySpan<byte> plain)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(plain);
        }
        return ms.ToArray();
    }

    private static byte[] Inflate(byte[] packed)
    {
        using var z = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
        return ReadAll(z);
    }

    internal static byte[] ReadAll(Stream s)
    {
        using (s)
        {
            var ms = new MemoryStream();
            var buf = new byte[1 << 16];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }
    }

    /// <summary>Stored form of each chunk as a standard writer would produce it (zlib, raw if it does not shrink).</summary>
    private static List<byte[]> Chunks(byte[] media, int chunkSize)
    {
        var list = new List<byte[]>();
        for (int off = 0; off < media.Length; off += chunkSize)
        {
            var plain = media.AsSpan(off, Math.Min(chunkSize, media.Length - off));
            var packed = Zlib(plain);
            list.Add(plain.Length == chunkSize && packed.Length >= chunkSize - 16 ? plain.ToArray() : packed);
        }
        return list;
    }

    /// <summary>
    /// A chunk whose zlib form is exactly <paramref name="chunkSize"/> bytes: mostly noise with
    /// just enough zeros at the end. Such chunks are common on real disks (media that is nearly,
    /// but not quite, incompressible); the search only makes one to order.
    /// </summary>
    internal static byte[] ChunkDeflatingToExactly(int chunkSize)
    {
        for (int seed = 1; seed < 400; seed++)
        {
            var noise = EwfBuilder.Media(chunkSize, seed);
            for (int k = chunkSize - 200; k <= chunkSize; k++)
            {
                var d = new byte[chunkSize];
                noise.AsSpan(0, k).CopyTo(d);
                if (Zlib(d).Length == chunkSize)
                {
                    return d;
                }
            }
        }
        throw new InvalidOperationException("No chunk deflating to exactly the chunk size was found.");
    }
}

/// <summary>Reports synchronously, on the imager's own thread, so a test can react between blocks.</summary>
internal sealed class InlineProgress(Action<ImageJobProgress> report) : IProgress<ImageJobProgress>
{
    public void Report(ImageJobProgress value) => report(value);
}

/// <summary>Hand-built AFF4 containers, laid out the way other tools write them.</summary>
internal static class Aff4Fixture
{
    internal const string Volume = "aff4://11111111-1111-1111-1111-111111111111";
    internal const string Map = "aff4://22222222-2222-2222-2222-222222222222";
    internal const string DefaultStream = "aff4://33333333-3333-3333-3333-333333333333";

    /// <param name="path">Container to create.</param>
    /// <param name="chunkSize">Declared chunk size.</param>
    /// <param name="size">Declared media size.</param>
    /// <param name="storedChunks">Each chunk exactly as it sits in the bevy.</param>
    /// <param name="tool">The <c>tool=</c> line of version.txt.</param>
    /// <param name="streamUrn">URN of the image stream.</param>
    /// <param name="memberPrefix">ZIP name prefix of the stream's members; defaults to the URN verbatim.</param>
    /// <param name="includeBevies">False leaves the stream's data out of the container.</param>
    /// <param name="targetId">Target index the single map range refers to (0 is the stream).</param>
    internal static void Write(string path, int chunkSize, long size, IReadOnlyList<byte[]> storedChunks,
        string tool = "pyaff4", string streamUrn = DefaultStream, string? memberPrefix = null,
        bool includeBevies = true, uint targetId = 0)
    {
        memberPrefix ??= streamUrn;
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.Comment = Volume;
        Add(zip, "container.description", Encoding.UTF8.GetBytes(Volume));
        Add(zip, "version.txt", Encoding.ASCII.GetBytes($"major=1\nminor=0\ntool={tool}\n"));

        if (includeBevies)
        {
            var bevy = new MemoryStream();
            var index = new MemoryStream();
            foreach (var chunk in storedChunks)
            {
                index.Write(BitConverter.GetBytes(bevy.Length));
                index.Write(BitConverter.GetBytes(chunk.Length));
                bevy.Write(chunk);
            }
            Add(zip, memberPrefix + "/00000000", bevy.ToArray());
            Add(zip, memberPrefix + "/00000000.index", index.ToArray());
        }

        var range = new byte[28];
        BitConverter.GetBytes(size).CopyTo(range, 8);
        BitConverter.GetBytes(targetId).CopyTo(range, 24);
        Add(zip, Map + "/map", range);
        Add(zip, Map + "/idx", Encoding.UTF8.GetBytes(streamUrn + "\n"));

        Add(zip, "information.turtle", Encoding.UTF8.GetBytes(
            "@prefix aff4: <http://aff4.org/Schema#> .\n" +
            $"<{Map}> a aff4:Map ; aff4:dependentStream <{streamUrn}> ; aff4:size {size} ; aff4:stored <{Volume}> .\n" +
            $"<{streamUrn}> a aff4:ImageStream ; aff4:chunkSize {chunkSize} ; aff4:chunksInSegment 2048 ; " +
            $"aff4:compressionMethod <https://www.ietf.org/rfc/rfc1950.txt> ; aff4:size {size} ; aff4:stored <{Volume}> .\n"));
    }

    private static void Add(ZipArchive zip, string name, byte[] data)
    {
        var e = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var s = e.Open();
        s.Write(data);
    }
}

/// <summary>Reads normally up to <c>failFrom</c>; every read reaching past it throws, as a dropped device does.</summary>
internal sealed class UnreadableFromStream(long length, long failFrom) : Stream
{
    private long _pos;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _pos; set => _pos = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // A device that has gone fails every request, including one past its old end.
        if (count > 0 && _pos + count > failFrom) throw new IOException("the device is not ready");
        var n = (int)Math.Min(count, length - _pos);
        if (n <= 0) return 0;
        Array.Fill(buffer, (byte)0xAB, offset, n);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => length + offset };
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A raw device as Windows presents it: a read that reaches past the last sector fails outright.</summary>
internal sealed class ErrorPastEndStream(byte[] data) : Stream
{
    private long _pos;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _pos; set => _pos = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;
        if (_pos + count > data.Length) throw new IOException("The drive cannot find the sector requested.");
        data.AsSpan((int)_pos, count).CopyTo(buffer.AsSpan(offset));
        _pos += count;
        return count;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => Length + offset };
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A 4K-native drive with one unreadable 4096-byte sector: only whole, aligned 4 KiB reads are accepted.</summary>
internal sealed class FourKnStream(byte[] data, long badSector) : Stream
{
    private long _pos;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _pos; set => _pos = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos % 4096 != 0 || count % 4096 != 0) throw new IOException("The parameter is incorrect.");
        var n = (int)Math.Min(count, data.Length - _pos);
        if (n <= 0) return 0;
        if (_pos < badSector + 4096 && _pos + n > badSector) throw new IOException("Data error (cyclic redundancy check).");
        data.AsSpan((int)_pos, n).CopyTo(buffer.AsSpan(offset));
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => Length + offset };
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Yields <c>goodBytes</c> and then fails every read with the given exception.</summary>
internal sealed class BreaksAfterStream(long goodBytes, Exception failure) : Stream
{
    private long _pos;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _pos; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = (int)Math.Min(count, goodBytes - _pos);
        if (n <= 0) throw failure;
        Array.Fill(buffer, (byte)0x5A, offset, n);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
