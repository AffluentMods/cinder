using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Cinder.Imaging.Ewf;
using FluentAssertions;
using Xunit;

namespace Cinder.Imaging.Tests;

/// <summary>
/// The writer is checked two ways: Cinder's own reader must decode the media back byte for
/// byte, and the on-disk structure must carry the Adler-32 checksums the format requires —
/// which the reader does not validate, but libewf, FTK Imager and EnCase do.
/// </summary>
public sealed class EwfWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-ewfw").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Adler32_matches_the_reference_vector()
    {
        EwfWriter.Adler32(Encoding.ASCII.GetBytes("Wikipedia")).Should().Be(0x11E60398u);
        EwfWriter.Adler32([]).Should().Be(1u);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Round_trips_through_the_reader_with_recorded_digests(bool compress)
    {
        var media = EwfBuilder.Media(3 * 1024 * 1024 + 512 * 5);   // not a whole number of chunks
        var path = Path.Combine(_dir, "rt.E01");

        using (var w = new EwfWriter(path, new EwfWriter.Options
        {
            Compress = compress,
            SectorsPerChunk = 16,
            CaseNumber = "C-42",
            EvidenceNumber = "E-7",
            Examiner = "alice",
            Description = "round trip",
        }))
        {
            // Feed in awkward sizes so chunk boundaries never line up with writes.
            var pos = 0;
            var step = 7000;
            while (pos < media.Length)
            {
                var n = Math.Min(step, media.Length - pos);
                w.Write(media.AsSpan(pos, n));
                pos += n;
                step = step == 7000 ? 13 : 7000;
            }
            w.Finish(MD5.HashData(media), SHA1.HashData(media));
            w.SegmentPaths.Should().HaveCount(1);
        }

        using var r = EwfReader.Open(path);
        r.MediaSize.Should().Be(media.Length);
        r.BytesPerSector.Should().Be(512);
        r.SectorsPerChunk.Should().Be(16);
        r.RecordedMd5.Should().Be(Convert.ToHexStringLower(MD5.HashData(media)));
        r.RecordedSha1.Should().Be(Convert.ToHexStringLower(SHA1.HashData(media)));
        r.CaseDescription.Should().Contain("alice").And.Contain("C-42");

        await using var s = r.OpenStream();
        var back = new byte[media.Length];
        var filled = 0;
        while (filled < back.Length)
        {
            var n = await s.ReadAsync(back.AsMemory(filled));
            if (n == 0) break;
            filled += n;
        }
        filled.Should().Be(media.Length);
        back.Should().Equal(media);
        r.DamagedChunks.Should().BeEmpty();

        var v = await r.VerifyAsync();
        v.Verified.Should().BeTrue();
    }

    [Fact]
    public async Task Splits_into_segments_the_reader_discovers_in_order()
    {
        var media = EwfBuilder.Media(12 * 1024 * 1024);
        var path = Path.Combine(_dir, "multi.E01");

        using (var w = new EwfWriter(path, new EwfWriter.Options
        {
            MaxSegmentBytes = 4 * 1024 * 1024,
            Compress = false,          // incompressible random data: force ~3+ segments
        }))
        {
            w.Write(media);
            w.Finish(MD5.HashData(media), SHA1.HashData(media));
            w.SegmentPaths.Count.Should().BeGreaterThan(2);
            w.SegmentPaths[1].Should().EndWith(".E02");
            foreach (var p in w.SegmentPaths)
            {
                new FileInfo(p).Length.Should().BeLessThanOrEqualTo(4 * 1024 * 1024);
            }
        }

        EwfReader.DiscoverSegments(path).Should().HaveCount(Directory.GetFiles(_dir, "multi.E*").Length);

        using var r = EwfReader.Open(path);
        r.SegmentCount.Should().BeGreaterThan(2);
        r.MediaSize.Should().Be(media.Length);
        var v = await r.VerifyAsync();
        v.Verified.Should().BeTrue($"multi-segment chain must hash back to the recorded digest: {v.Summary()}");
    }

    [Fact]
    public void Every_section_descriptor_and_geometry_block_carries_a_valid_adler32()
    {
        var media = EwfBuilder.Media(256 * 1024);
        var path = Path.Combine(_dir, "sums.E01");
        using (var w = new EwfWriter(path))
        {
            w.Write(media);
            w.AddBadSectors(10, 2);
            w.Finish(MD5.HashData(media), SHA1.HashData(media));
        }

        var bytes = File.ReadAllBytes(path);
        var pos = 13L;
        var seen = new List<string>();
        while (true)
        {
            var d = bytes.AsSpan((int)pos, 76);
            var type = Encoding.ASCII.GetString(d[..16]).TrimEnd('\0');
            seen.Add(type);
            BitConverter.ToUInt32(d[72..]).Should().Be(EwfWriter.Adler32(d[..72]), $"descriptor '{type}' at {pos}");
            var next = BitConverter.ToInt64(d[16..]);
            var size = BitConverter.ToInt64(d[24..]);
            var data = bytes.AsSpan((int)pos + 76, (int)(size - 76));

            switch (type)
            {
                case "volume":
                case "data":
                    data.Length.Should().Be(1052);
                    BitConverter.ToUInt32(data[1048..]).Should().Be(EwfWriter.Adler32(data[..1048]));
                    BitConverter.ToUInt32(data[4..]).Should().Be(8u);          // 256 KiB / 32 KiB chunks
                    BitConverter.ToUInt64(data[16..]).Should().Be(512UL);       // sectors
                    break;
                case "table":
                case "table2":
                    BitConverter.ToUInt32(data[20..]).Should().Be(EwfWriter.Adler32(data[..20]));
                    var n = (int)BitConverter.ToUInt32(data);
                    BitConverter.ToUInt32(data[(24 + n * 4)..]).Should().Be(EwfWriter.Adler32(data.Slice(24, n * 4)));
                    break;
                case "digest":
                    data.Length.Should().Be(80);
                    BitConverter.ToUInt32(data[76..]).Should().Be(EwfWriter.Adler32(data[..76]));
                    break;
                case "hash":
                    data.Length.Should().Be(36);
                    BitConverter.ToUInt32(data[32..]).Should().Be(EwfWriter.Adler32(data[..32]));
                    break;
                case "error2":
                    BitConverter.ToUInt32(data).Should().Be(1u);
                    BitConverter.ToUInt32(data[516..]).Should().Be(EwfWriter.Adler32(data[..516]));
                    BitConverter.ToUInt32(data[520..]).Should().Be(10u);
                    BitConverter.ToUInt32(data[524..]).Should().Be(2u);
                    break;
            }

            if (next == pos) break;
            pos = next;
        }

        seen.Should().ContainInOrder("header2", "header2", "header", "volume", "sectors", "table", "table2", "digest", "hash", "error2", "done");
    }

    [Fact]
    public void Stored_chunks_end_with_their_adler32_and_compressed_chunks_are_zlib()
    {
        var zeros = new byte[64 * 1024];                    // compresses to almost nothing
        var noise = EwfBuilder.Media(64 * 1024);            // does not compress: stored + checksum
        var path = Path.Combine(_dir, "mix.E01");
        using (var w = new EwfWriter(path, new EwfWriter.Options { SectorsPerChunk = 128 }))   // 64 KiB chunks
        {
            w.Write(zeros);
            w.Write(noise);
            w.Finish(null, null);
        }

        using var r = EwfReader.Open(path);
        r.ChunkCount.Should().Be(2);
        var offsets = r.ChunkOffsets;
        (offsets[0] < 0).Should().BeTrue("all-zero chunk is compressed (flag in the sign bit)");
        (offsets[1] < 0).Should().BeFalse("random chunk is stored");

        var bytes = File.ReadAllBytes(path);
        var stored = (int)(offsets[1] & 0x7FFFFFFFFFFFFFFF);
        BitConverter.ToUInt32(bytes, stored + noise.Length).Should().Be(EwfWriter.Adler32(noise));
        bytes[(int)(offsets[0] & 0x7FFFFFFFFFFFFFFF)].Should().Be(0x78, "zlib header");
    }

    [Fact]
    public async Task Imager_writes_ewf_with_bad_sectors_in_error2_and_reader_agrees_with_the_hash()
    {
        var data = EwfBuilder.Media(2 * 1024 * 1024);
        const long badStart = 1_048_576 + 4096;
        var source = new FaultyStream(data, badStart, 1024);
        var path = Path.Combine(_dir, "img.E01");

        var result = await new InProcessImager().ImageStreamAsync(source,
            new ImageJob("faulty", path, ImageFormat.Ewf, ReadErrorRetries: 1, ExaminerName: "bob"));

        result.BadSectors.Should().Be(2);
        var expected = (byte[])data.Clone();
        Array.Clear(expected, (int)badStart, 1024);
        result.Md5.Should().Be(Convert.ToHexStringLower(MD5.HashData(expected)));

        using var r = EwfReader.Open(path);
        r.RecordedMd5.Should().Be(result.Md5);
        (await r.VerifyAsync()).Verified.Should().BeTrue();
        File.Exists(path + ".log.json").Should().BeTrue();
        File.Exists(path + ".sha256").Should().BeFalse("EWF carries its digests inside");

        var bytes = await File.ReadAllBytesAsync(path);
        Encoding.ASCII.GetString(bytes).Should().Contain("error2");
    }

    [Fact]
    public async Task Raw_to_ewf_conversion_verifies_its_own_output()
    {
        var data = EwfBuilder.Media(1024 * 1024 + 512);
        var raw = Path.Combine(_dir, "in.dd");
        await File.WriteAllBytesAsync(raw, data);
        var e01 = Path.Combine(_dir, "out.E01");

        var r = await ImageConverter.RawToEwfAsync(raw, e01, "carol", compressionLevel: 2, segmentSizeMiB: 4);
        r.MatchesRecorded.Should().BeTrue();
        r.RecordedMd5.Should().Be(Convert.ToHexStringLower(MD5.HashData(data)));
        r.DamagedChunks.Should().Be(0);

        // And the reverse direction reproduces the raw bytes.
        var back = Path.Combine(_dir, "back.dd");
        var r2 = await ImageConverter.EwfToRawAsync(e01, back);
        r2.MatchesRecorded.Should().BeTrue();
        (await File.ReadAllBytesAsync(back)).Should().Equal(data);
    }

    [Fact]
    public void Segment_names_follow_encase_order()
    {
        EwfWriter.SegmentPath(@"C:\x\img.E01", 1).Should().Be(@"C:\x\img.E01");
        EwfWriter.SegmentPath(@"C:\x\img.E01", 2).Should().EndWith("img.E02");
        EwfWriter.SegmentPath(@"C:\x\img.E01", 99).Should().EndWith("img.E99");
        EwfWriter.SegmentPath(@"C:\x\img.E01", 100).Should().EndWith("img.EAA");
        EwfWriter.SegmentPath(@"C:\x\img.E01", 126).Should().EndWith("img.EBA");
    }
}
