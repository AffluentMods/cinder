using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Cinder.Imaging.Aff4;
using FluentAssertions;
using Xunit;

namespace Cinder.Imaging.Tests;

public sealed class Aff4Tests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-aff4").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Round_trips_through_the_reader_with_digests_and_metadata(bool compress)
    {
        var media = EwfBuilder.Media(2 * 1024 * 1024 + 4321);   // ragged tail, several bevies
        var path = Path.Combine(_dir, "rt.aff4");
        using (var w = new Aff4Writer(path, new Aff4Writer.Options
        {
            Compress = compress,
            ChunkSize = 4096,
            ChunksPerSegment = 64,
            CaseNumber = "C-9",
            Examiner = "dana",
            Description = "round \"trip\"\nwith newline",
        }))
        {
            var pos = 0;
            while (pos < media.Length)
            {
                var n = Math.Min(7777, media.Length - pos);
                w.Write(media.AsSpan(pos, n));
                pos += n;
            }
            w.Finish(MD5.HashData(media), SHA1.HashData(media), SHA256.HashData(media));
        }

        Aff4Reader.IsAff4(path).Should().BeTrue();
        using var r = Aff4Reader.Open(path);
        r.Size.Should().Be(media.Length);
        r.RecordedMd5.Should().Be(Convert.ToHexStringLower(MD5.HashData(media)));
        r.RecordedSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(media)));
        r.Metadata["caseNumber"].Should().Be("C-9");
        r.Metadata["examiner"].Should().Be("dana");
        r.Metadata["caseDescription"].Should().Be("round \"trip\"\nwith newline");

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

        // Random access across a bevy boundary.
        s.Position = 64 * 4096 - 100;
        var window = new byte[300];
        s.Read(window).Should().Be(300);
        window.Should().Equal(media.AsSpan(64 * 4096 - 100, 300).ToArray());

        // Structure a third-party reader relies on.
        using var zip = ZipFile.OpenRead(path);
        zip.Comment.Should().Be(r.VolumeUrn);
        zip.GetEntry("container.description").Should().NotBeNull();
        zip.GetEntry("information.turtle").Should().NotBeNull();
        zip.Entries.Should().Contain(e => e.FullName.StartsWith("aff4%3A%2F%2F") && e.FullName.EndsWith("/00000000"));
        zip.Entries.Should().Contain(e => e.FullName.EndsWith("/00000000.index"));
        zip.Entries.Should().Contain(e => e.FullName.EndsWith("/map"));
    }

    [Fact]
    public async Task Evidence_opener_and_imager_handle_aff4()
    {
        var data = EwfBuilder.Media(1024 * 1024);
        var src = Path.Combine(_dir, "src.dd");
        await File.WriteAllBytesAsync(src, data);
        var out1 = Path.Combine(_dir, "img.aff4");

        var result = await new InProcessImager().ImageAsync(new ImageJob(src, out1, ImageFormat.Aff4, ExaminerName: "eve", CaseNumber: "C-1"));
        result.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(data)));
        File.Exists(out1 + ".log.json").Should().BeTrue();
        File.Exists(out1 + ".sha256").Should().BeFalse("AFF4 carries its digests inside");

        EvidenceOpener.IsAff4(out1).Should().BeTrue();
        EvidenceOpener.IsEwf(out1).Should().BeFalse();
        await using var s = EvidenceOpener.Open(out1);
        s.Length.Should().Be(data.Length);
        var back = new byte[data.Length];
        var filled = 0;
        while (filled < back.Length)
        {
            var n = await s.ReadAsync(back.AsMemory(filled));
            if (n == 0) break;
            filled += n;
        }
        back.Should().Equal(data);

        // AFF4 → raw through the imager's source opener too.
        var raw = Path.Combine(_dir, "back.dd");
        var r2 = await new InProcessImager().ImageAsync(new ImageJob(out1, raw, ImageFormat.Raw));
        (await File.ReadAllBytesAsync(raw)).Should().Equal(data);
        r2.Md5.Should().Be(result.Md5);
    }

    [Fact]
    public void Reads_unencoded_member_names_and_rdflib_style_turtle()
    {
        // A container as pyaff4 0.3x writes it: unencoded member names, bare integer literals,
        // no Image object — just a Map over a stream.
        var media = EwfBuilder.Media(10_000);
        var path = Path.Combine(_dir, "v11.aff4");
        const string vol = "aff4://11111111-1111-1111-1111-111111111111";
        const string map = "aff4://22222222-2222-2222-2222-222222222222";
        const string stream = "aff4://33333333-3333-3333-3333-333333333333";

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.Comment = vol;
            Add(zip, "container.description", Encoding.UTF8.GetBytes(vol));
            Add(zip, "version.txt", Encoding.ASCII.GetBytes("major=1\nminor=1\ntool=pyaff4\n"));

            var bevy = new MemoryStream();
            var index = new MemoryStream();
            for (int off = 0; off < media.Length; off += 4096)
            {
                var start = bevy.Length;
                var chunk = media.AsSpan(off, Math.Min(4096, media.Length - off));
                using (var z = new ZLibStream(bevy, CompressionLevel.Optimal, leaveOpen: true)) z.Write(chunk);
                index.Write(BitConverter.GetBytes(start));
                index.Write(BitConverter.GetBytes((int)(bevy.Length - start)));
            }
            Add(zip, stream + "/00000000", bevy.ToArray());
            Add(zip, stream + "/00000000.index", index.ToArray());

            var range = new byte[28];
            BitConverter.GetBytes((long)media.Length).CopyTo(range, 8);
            Add(zip, map + "/map", range);
            Add(zip, map + "/idx", Encoding.UTF8.GetBytes(stream + "\n"));

            Add(zip, "information.turtle", Encoding.UTF8.GetBytes($"""
                @prefix aff4: <http://aff4.org/Schema#> .
                @prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .
                @prefix xsd: <http://www.w3.org/2001/XMLSchema#> .

                <{map}> a aff4:Map ;
                    aff4:dependentStream <{stream}> ;
                    aff4:size {media.Length} ;
                    aff4:stored <{vol}> .

                <{stream}> a aff4:ImageStream ;
                    aff4:chunkSize 4096 ;
                    aff4:chunksInSegment 2048 ;
                    aff4:compressionMethod <https://www.ietf.org/rfc/rfc1950.txt> ;
                    aff4:size {media.Length} ;
                    aff4:stored <{vol}> ;
                    aff4:version 1 .
                """));
        }

        using var r = Aff4Reader.Open(path);
        r.ImageUrn.Should().Be(map);
        r.Size.Should().Be(media.Length);
        using var s = r.OpenStream();
        var back = new byte[media.Length];
        s.Read(back).Should().Be(media.Length);
        back.Should().Equal(media);
    }

    [Fact]
    public void Snappy_and_lz4_decoders_match_the_reference_implementations()
    {
        // Vectors produced by python-snappy and lz4.block (store_size=False) over the same input.
        var expectedSha = "b7af7ca825f35bfa74502e09e9819e9b04f1c8504089b188bec76046317fc7a5";
        const int length = 2568;
        var snappy = Convert.FromBase64String("iBSwVGhlIHF1aWNrIGJyb3duIGZveCBqdW1wcyBvdmVyIHRoZSBsYXp5IGRvZy4g/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0A/i0Aai0A9AUBAAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0BBQkNERUZHSElKS0xNTk9QUVJTVFVWV1hZWltcXV5fYGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn+AgYKDhIWGh4iJiouMjY6PkJGSk5SVlpeYmZqbnJ2en6ChoqOkpaanqKmqq6ytrq+wsbKztLW2t7i5uru8vb6/wMHCw8TFxsfIycrLzM3Oz9DR0tPU1dbX2Nna29zd3t/g4eLj5OXm5+jp6uvs7e7v8PHy8/T19vf4+fr7/P3+/wABAgMEBf4AAf4AAf4AAf4AAf4AAf4AAf4AAeYAAQ==");
        var lz4 = Convert.FromBase64String("/x5UaGUgcXVpY2sgYnJvd24gZm94IGp1bXBzIG92ZXIgdGhlIGxhenkgZG9nLiAtAP///////87/8QABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4fICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj9AQUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVpbXF1eX2BhYmNkZWZnaGlqa2xtbm9wcXJzdHV2d3h5ent8fX5/gIGCg4SFhoeIiYqLjI2Oj5CRkpOUlZaXmJmam5ydnp+goaKjpKWmp6ipqqusra6vsLGys7S1tre4ubq7vL2+v8DBwsPExcbHyMnKy8zNzs/Q0dLT1NXW19jZ2tvc3d7f4OHi4+Tl5ufo6err7O3u7/Dx8vP09fb3+Pn6+/z9/v8AAf/pUPv8/f7/");

        var a = new byte[length];
        SnappyDecoder.Decode(snappy, a).Should().Be(length);
        Convert.ToHexStringLower(SHA256.HashData(a)).Should().Be(expectedSha);

        var b = new byte[length];
        Lz4Decoder.Decode(lz4, b).Should().Be(length);
        Convert.ToHexStringLower(SHA256.HashData(b)).Should().Be(expectedSha);

        // Hostile input must not escape the output buffer.
        var act = () => SnappyDecoder.Decode(new byte[] { 0x10, 0xFC, 0xFF, 0xFF, 0xFF }, new byte[16]);
        act.Should().Throw<InvalidDataException>();
        var act2 = () => Lz4Decoder.Decode(new byte[] { 0x0F, 0x01, 0xFF, 0xFF }, new byte[16]);
        act2.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(ImageFormat.Vhd)]
    [InlineData(ImageFormat.Vhdx)]
    public async Task Imager_writes_virtual_disks_that_discutils_reads_back(ImageFormat format)
    {
        var data = EwfBuilder.Media(3 * 1024 * 1024 + 512);
        var src = Path.Combine(_dir, "src.dd");
        await File.WriteAllBytesAsync(src, data);
        var outPath = Path.Combine(_dir, "disk" + format.DefaultExtension());

        var result = await new InProcessImager().ImageAsync(new ImageJob(src, outPath, format));
        result.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(data)));

        using var file = File.OpenRead(outPath);
        using DiscUtils.VirtualDisk disk = format == ImageFormat.Vhd
            ? new DiscUtils.Vhd.Disk(file, DiscUtils.Streams.Ownership.None)
            : new DiscUtils.Vhdx.Disk(file, DiscUtils.Streams.Ownership.None);
        disk.Capacity.Should().Be(data.Length);
        var back = new byte[data.Length];
        disk.Content.Position = 0;
        var filled = 0;
        while (filled < back.Length)
        {
            var n = disk.Content.Read(back, filled, back.Length - filled);
            if (n <= 0) break;
            filled += n;
        }
        back.Should().Equal(data);
    }

    private static void Add(ZipArchive zip, string name, byte[] data)
    {
        var e = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var s = e.Open();
        s.Write(data);
    }
}
