using System.IO.Compression;
using System.Security.Cryptography;
using Cinder.Imaging.Aff4;
using FluentAssertions;
using Xunit;

namespace Cinder.Imaging.Tests;

/// <summary>
/// The opener must route every container to its reader by magic rather than extension, and
/// every reader must survive a corrupted container: wrong bytes become zero-filled damaged
/// chunks or a clean <see cref="InvalidDataException"/>, never an unbounded allocation or an
/// unexpected exception type.
/// </summary>
public sealed class EvidenceOpenerHardeningTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-open").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(ImageFormat.Vhd)]
    [InlineData(ImageFormat.Vhdx)]
    public async Task Virtual_disks_open_as_media_by_magic_even_when_renamed(ImageFormat format)
    {
        var data = EwfBuilder.Media(1024 * 1024);
        var src = Path.Combine(_dir, "src.dd");
        await File.WriteAllBytesAsync(src, data);
        var outPath = Path.Combine(_dir, "disk" + format.DefaultExtension());
        await new InProcessImager().ImageAsync(new ImageJob(src, outPath, format));

        // Rename so only the magic can identify it.
        var renamed = Path.Combine(_dir, "evidence.bin");
        File.Move(outPath, renamed);

        EvidenceOpener.VirtualDiskKind(renamed).Should().Be(format == ImageFormat.Vhd ? "vhd" : "vhdx");
        await using var s = EvidenceOpener.Open(renamed);
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

        // The companion the imager wrote is over the media, which is what the opener returns.
        var sums = await File.ReadAllTextAsync(outPath + ".sha256");
        sums.Should().StartWith(Convert.ToHexStringLower(SHA256.HashData(data)));
    }

    [Fact]
    public void Raw_files_are_not_mistaken_for_virtual_disks_or_containers()
    {
        var raw = Path.Combine(_dir, "raw.dd");
        File.WriteAllBytes(raw, EwfBuilder.Media(64 * 1024));
        EvidenceOpener.VirtualDiskKind(raw).Should().BeNull();
        EvidenceOpener.IsAff4(raw).Should().BeFalse();
        EvidenceOpener.IsEwf(raw).Should().BeFalse();
        using var s = EvidenceOpener.Open(raw);
        s.Should().BeOfType<FileStream>();
    }

    [Fact]
    public void Corrupted_aff4_bevies_become_damaged_chunks_not_exceptions()
    {
        var media = EwfBuilder.Media(200 * 1024);
        var path = Path.Combine(_dir, "ok.aff4");
        using (var w = new Aff4Writer(path, new Aff4Writer.Options { ChunkSize = 4096, ChunksPerSegment = 8 }))
        {
            w.Write(media);
            w.Finish(MD5.HashData(media), null, null);
        }

        // Rewrite the container with the second bevy's bytes scrambled (stored ZIP members, so
        // the payload is at a fixed place — but go through the archive API to be safe).
        var broken = Path.Combine(_dir, "broken.aff4");
        using (var src = ZipFile.OpenRead(path))
        using (var dst = ZipFile.Open(broken, ZipArchiveMode.Create))
        {
            dst.Comment = src.Comment;
            foreach (var e in src.Entries)
            {
                using var i = e.Open();
                var bytes = new byte[e.Length];
                var filled = 0;
                while (filled < bytes.Length)
                {
                    var n = i.Read(bytes, filled, bytes.Length - filled);
                    if (n <= 0) break;
                    filled += n;
                }
                if (e.FullName.EndsWith("/00000001", StringComparison.Ordinal))
                {
                    var rnd = new Random(9);
                    for (int k = 0; k < bytes.Length; k += 3) bytes[k] = (byte)rnd.Next(256);
                }
                var o = dst.CreateEntry(e.FullName, CompressionLevel.NoCompression);
                using var os = o.Open();
                os.Write(bytes);
            }
        }

        using var r = Aff4Reader.Open(broken);
        using var s = r.OpenStream();
        var back = new byte[media.Length];
        var got = 0;
        while (got < back.Length)
        {
            var n = s.Read(back, got, back.Length - got);
            if (n == 0) break;
            got += n;
        }
        got.Should().Be(media.Length, "damaged chunks are zero-filled, so the stream keeps its length");
        r.DamagedChunks.Should().NotBeEmpty();
        back.AsSpan(0, 8 * 4096).ToArray().Should().Equal(media.AsSpan(0, 8 * 4096).ToArray(), "the first bevy is intact");
    }

    [Fact]
    public void Hostile_turtle_and_geometry_are_rejected_cleanly()
    {
        var path = Path.Combine(_dir, "hostile.aff4");
        const string vol = "aff4://00000000-0000-0000-0000-000000000001";
        const string stream = "aff4://00000000-0000-0000-0000-000000000002";
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.Comment = vol;
            Add(zip, "container.description", vol);
            Add(zip, "version.txt", "major=1\nminor=0\ntool=x\n");
            Add(zip, Aff4Writer.MemberName(stream, "00000000"), "not a bevy");
            Add(zip, Aff4Writer.MemberName(stream, "00000000.index"), "\0\0\0\0\0\0\0\0\0\0\0\0");
            Add(zip, "information.turtle", $"""
                @prefix aff4: <http://aff4.org/Schema#> .
                <{stream}> a aff4:ImageStream ;
                    aff4:chunkSize 9999999999 ;
                    aff4:chunksInSegment 1 ;
                    aff4:size 5 .
                """);
        }

        var act = () => Aff4Reader.Open(path);
        act.Should().Throw<InvalidDataException>().WithMessage("*implausible*");

        // Unterminated literal, missing dots, garbage — the parser must end, not loop.
        var junk = Path.Combine(_dir, "junk.aff4");
        using (var zip = ZipFile.Open(junk, ZipArchiveMode.Create))
        {
            Add(zip, "container.description", vol);
            Add(zip, "information.turtle", "<a> <b> \"unterminated ;;; ,,, <<<>>> ^^ @@ . . .");
        }
        var act2 = () => Aff4Reader.Open(junk);
        act2.Should().Throw<InvalidDataException>();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        var e = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var s = e.Open();
        s.Write(System.Text.Encoding.UTF8.GetBytes(text));
    }
}
