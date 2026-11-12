using System.Security.Cryptography;
using Cinder.Imaging.Ewf;
using FluentAssertions;
using Xunit;

namespace Cinder.Imaging.Tests;

/// <summary>
/// Second round of review fixes: stale E01 segments, overwriting existing output, and the
/// handle <see cref="EvidenceOpener"/> leaked. Like <see cref="RegressionTests"/>, these use
/// only members that existed before the fixes, so they can be run against the earlier build to
/// show that each one fails there.
/// </summary>
public sealed class RegressionRound2Tests : IDisposable
{
    private const int MiB = 1024 * 1024;
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-r2").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Writes an uncompressed image in 4 MiB segments and returns its segment paths.</summary>
    private List<string> WriteChain(string folder, byte[] media)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, folder)).FullName;
        using var w = new EwfWriter(Path.Combine(dir, "img.E01"), new EwfWriter.Options { MaxSegmentBytes = 4 * MiB, Compress = false });
        w.Write(media);
        w.Finish(MD5.HashData(media), SHA1.HashData(media));
        return [.. w.SegmentPaths];
    }

    private string Assemble(string folder, params string[] sources)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, folder)).FullName;
        for (int i = 0; i < sources.Length; i++)
        {
            File.Copy(sources[i], EwfWriter.SegmentPath(Path.Combine(dir, "img.E01"), i + 1));
        }
        return Path.Combine(dir, "img.E01");
    }

    // ---- 1a: the reader takes only the segments that belong to the image ----------------------

    [Fact]
    public async Task Reader_stops_at_the_segment_that_ends_the_image_and_ignores_an_older_images_tail()
    {
        // What re-imaging over a longer chain used to leave: a new two-segment image followed
        // by segments three and four of the image that was there before.
        var older = WriteChain("older", EwfBuilder.Media(13 * MiB, seed: 1));
        var newer = WriteChain("newer", EwfBuilder.Media(5 * MiB, seed: 2));
        older.Count.Should().BeGreaterThanOrEqualTo(4);
        newer.Count.Should().Be(2);
        var first = Assemble("mixed", newer[0], newer[1], older[2], older[3]);
        EwfReader.DiscoverSegments(first).Should().HaveCount(4, "the stale files are found by name");

        using var r = EwfReader.Open(first);
        r.SegmentCount.Should().Be(2, "the image ended with segment two's `done`");
        r.MediaSize.Should().Be(5 * MiB);
        var v = await r.VerifyAsync();
        v.Verified.Should().BeTrue(v.Summary());
    }

    [Fact]
    public void Reader_does_not_take_a_segment_from_another_acquisition_as_the_next_one()
    {
        // Same position in the chain, right segment number, but a different set identifier:
        // segment two of some other image sitting where this image's segment two should be.
        var mine = WriteChain("mine", EwfBuilder.Media(13 * MiB, seed: 3));
        var theirs = WriteChain("theirs", EwfBuilder.Media(13 * MiB, seed: 4));
        var first = Assemble("swapped", mine[0], theirs[1], mine[2]);

        using var r = EwfReader.Open(first);
        r.SegmentCount.Should().Be(1, "the foreign segment and everything after it are left out");

        // The media past segment one is missing, and says so — it is not filled in with the
        // other image's chunks.
        using var s = r.OpenStream();
        var act = () => s.CopyTo(Stream.Null);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Reader_does_not_take_a_segment_whose_header_carries_the_wrong_number()
    {
        var chain = WriteChain("gap", EwfBuilder.Media(13 * MiB, seed: 5));
        // Segment three renamed into segment two's place.
        var first = Assemble("misnumbered", chain[0], chain[2]);

        using var r = EwfReader.Open(first);
        r.SegmentCount.Should().Be(1);
    }

    // ---- 1b: the writer never starts on top of an existing chain ------------------------------

    [Theory]
    [InlineData(1)]   // the image itself
    [InlineData(3)]   // only a later segment — the tail an earlier, longer image left
    public void Ewf_writer_refuses_to_start_when_a_segment_of_the_target_chain_exists(int existingSegment)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "refuse-" + existingSegment)).FullName;
        var first = Path.Combine(dir, "img.E01");
        var existing = EwfWriter.SegmentPath(first, existingSegment);
        var evidence = EwfBuilder.Media(4096, seed: 9);
        File.WriteAllBytes(existing, evidence);

        var act = () => new EwfWriter(first);
        act.Should().Throw<IOException>().WithMessage("*already exist*");

        File.ReadAllBytes(existing).Should().Equal(evidence, "the existing file is not touched");
        Directory.GetFiles(dir).Should().Equal(existing);
    }

    // ---- overwrite is never implicit, for any format ------------------------------------------

    [Theory]
    [InlineData(ImageFormat.Raw)]
    [InlineData(ImageFormat.Ewf)]
    [InlineData(ImageFormat.Aff4)]
    [InlineData(ImageFormat.Vhd)]
    [InlineData(ImageFormat.Vhdx)]
    public async Task Imaging_does_not_replace_an_existing_image_unless_asked(ImageFormat format)
    {
        var src = Path.Combine(_dir, "src.bin");
        await File.WriteAllBytesAsync(src, EwfBuilder.Media(2 * MiB));
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "exists-" + format)).FullName;
        var outPath = Path.Combine(dir, "image" + format.DefaultExtension());

        var evidence = EwfBuilder.Media(8192, seed: 11);
        await File.WriteAllBytesAsync(outPath, evidence);
        await File.WriteAllTextAsync(outPath + ".log.json", "the earlier acquisition's log");

        var act = () => new InProcessImager().ImageAsync(new ImageJob(src, outPath, format));
        (await act.Should().ThrowAsync<IOException>()).WithMessage("*already exist*");

        (await File.ReadAllBytesAsync(outPath)).Should().Equal(evidence);
        (await File.ReadAllTextAsync(outPath + ".log.json")).Should().Be("the earlier acquisition's log");
        Directory.GetFiles(dir).Should().BeEquivalentTo([outPath, outPath + ".log.json"], "nothing was created either");
    }

    [Theory]
    [InlineData(ImageFormat.Raw)]
    [InlineData(ImageFormat.Ewf)]
    [InlineData(ImageFormat.Aff4)]
    [InlineData(ImageFormat.Vhdx)]
    public async Task Imaging_does_not_write_over_the_unfinished_image_of_an_earlier_run_unless_asked(ImageFormat format)
    {
        var src = Path.Combine(_dir, "src2.bin");
        await File.WriteAllBytesAsync(src, EwfBuilder.Media(2 * MiB));
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "partial-" + format)).FullName;
        var outPath = Path.Combine(dir, "image" + format.DefaultExtension());

        // What a failed acquisition leaves: possibly the only copy of a disk that has since died.
        var recovered = EwfBuilder.Media(8192, seed: 12);
        await File.WriteAllBytesAsync(outPath + ".partial", recovered);

        var act = () => new InProcessImager().ImageAsync(new ImageJob(src, outPath, format));
        (await act.Should().ThrowAsync<IOException>()).WithMessage("*already exist*");

        (await File.ReadAllBytesAsync(outPath + ".partial")).Should().Equal(recovered);
        File.Exists(outPath).Should().BeFalse();
    }

    [Fact]
    public async Task Re_imaging_a_shorter_image_over_a_longer_e01_chain_is_refused_and_leaves_the_chain_intact()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "reimage")).FullName;
        var outPath = Path.Combine(dir, "image.E01");
        var longSrc = Path.Combine(_dir, "long.bin");
        var shortSrc = Path.Combine(_dir, "short.bin");
        var longData = EwfBuilder.Media(13 * MiB, seed: 21);
        await File.WriteAllBytesAsync(longSrc, longData);
        await File.WriteAllBytesAsync(shortSrc, EwfBuilder.Media(5 * MiB, seed: 22));

        var first = await new InProcessImager().ImageAsync(new ImageJob(longSrc, outPath, ImageFormat.Ewf, CompressionLevel: 0, SegmentSizeMiB: 4));
        var before = Directory.GetFiles(dir).ToDictionary(p => p, p => new FileInfo(p).Length);

        var act = () => new InProcessImager().ImageAsync(new ImageJob(shortSrc, outPath, ImageFormat.Ewf, CompressionLevel: 0, SegmentSizeMiB: 4));
        await act.Should().ThrowAsync<IOException>();

        Directory.GetFiles(dir).ToDictionary(p => p, p => new FileInfo(p).Length).Should().Equal(before);
        using var r = EwfReader.Open(outPath);
        r.RecordedMd5.Should().Be(first.Md5);
        (await r.VerifyAsync()).Verified.Should().BeTrue("the original image is still whole");
    }

    // ---- 3: EvidenceOpener gives the file back when detection fails ---------------------------

    [Theory]
    [InlineData("vhdxfile")]
    [InlineData("conectix")]
    public void Evidence_opener_does_not_keep_a_file_open_when_it_cannot_make_sense_of_it(string magic)
    {
        // The right magic and nothing behind it: the virtual-disk parser throws from inside.
        var path = Path.Combine(_dir, magic + ".bin");
        var bytes = Enumerable.Repeat((byte)0xFF, MiB).ToArray();
        System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);

        var act = () => EvidenceOpener.Open(path);
        act.Should().Throw<Exception>();

        // No GC has run in between, so a handle that was leaked is still held here.
        var exclusive = () => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
        exclusive.Should().NotThrow("the evidence file must not stay locked after a failed open");
    }
}
