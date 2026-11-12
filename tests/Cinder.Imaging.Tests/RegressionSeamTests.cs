using System.Security.Cryptography;
using System.Text.Json;
using Cinder.Imaging.Ewf;
using FluentAssertions;
using Xunit;

namespace Cinder.Imaging.Tests;

/// <summary>
/// Regression tests for the review findings whose fix added something to check against: the
/// abort threshold, the separate source-damage counts, the write-blocker and mounter seams, and
/// the writer's partial-suffix and unrecordable-sector reporting.
/// </summary>
public sealed class RegressionSeamTests : IDisposable
{
    private const int MiB = 1024 * 1024;
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-seam").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- 3: a source that has gone away aborts the job ----------------------------------------

    [Theory(Timeout = 120_000)]
    [InlineData(ImageFormat.Raw)]
    [InlineData(ImageFormat.Ewf)]
    public async Task A_run_of_entirely_unreadable_blocks_aborts_and_the_log_says_incomplete(ImageFormat format)
    {
        // Length unknown, so nothing but the abort threshold can stop the zero-fill.
        var outPath = Path.Combine(_dir, "abort" + format.DefaultExtension());
        var imager = new InProcessImager { MaxConsecutiveFailedBlocks = 3 };

        var act = () => imager.ImageStreamAsync(
            new UnknownLengthStream(new UnreadableFromStream(long.MaxValue, 2 * MiB)),
            new ImageJob("dropped", outPath, format, CompressionLevel: 0, ReadErrorRetries: 0));

        var thrown = (await act.Should().ThrowAsync<AcquisitionAbortedException>()).Which;
        thrown.Message.Should().Contain("INCOMPLETE");
        thrown.BytesAcquired.Should().Be(5L * MiB, "two readable blocks, then three unreadable ones");
        thrown.PartialPaths.Should().Equal(outPath + InProcessImager.PartialSuffix);

        File.Exists(outPath).Should().BeFalse();
        File.Exists(outPath + InProcessImager.PartialSuffix).Should().BeTrue("what was read before the source failed is kept");

        using var log = JsonDocument.Parse(await File.ReadAllTextAsync(outPath + ".log.json"));
        log.RootElement.GetProperty("complete").GetBoolean().Should().BeFalse();
        log.RootElement.GetProperty("status").GetString().Should().Be("incomplete");
        log.RootElement.GetProperty("error").GetString().Should().Contain("INCOMPLETE");
        log.RootElement.GetProperty("bytes_written").GetInt64().Should().Be(5L * MiB);
        log.RootElement.GetProperty("bad_sectors").GetInt64().Should().Be(3L * MiB / 512);
    }

    [Fact]
    public async Task Scattered_bad_blocks_do_not_trip_the_abort()
    {
        // Unreadable blocks that are not consecutive are damage, not a dead source.
        var data = EwfBuilder.Media(6 * MiB);
        var outPath = Path.Combine(_dir, "scattered.dd");
        var imager = new InProcessImager { MaxConsecutiveFailedBlocks = 2 };

        var result = await imager.ImageStreamAsync(
            new BadBlocksStream(data, [1, 3, 5]),
            new ImageJob("scattered", outPath, ImageFormat.Raw, ReadErrorRetries: 0));

        result.BytesWritten.Should().Be(data.Length);
        result.BadSectors.Should().Be(3L * MiB / 512);
        File.Exists(outPath).Should().BeTrue();
    }

    // ---- 6: source damage has its own channel -------------------------------------------------

    [Fact]
    public async Task Conversion_reports_source_and_output_damage_separately()
    {
        var media = EwfBuilder.Media(4096 * 4);
        var e01 = Path.Combine(_dir, "damaged.E01");
        await File.WriteAllBytesAsync(e01, EwfBuilder.Build(media,
            new EwfBuilder.Options { Compress = true, CorruptChunkIndex = 1, IncludeDigest = false }));

        foreach (var verify in new[] { true, false })
        {
            var r = await ImageConverter.ConvertAsync(e01, Path.Combine(_dir, $"damaged-{verify}.dd"), ImageFormat.Raw, verify: verify);
            r.SourceDamagedChunks.Should().Be(1);
            r.OutputDamagedChunks.Should().Be(0);
            r.DamagedChunks.Should().Be(1);
            r.MatchesRecorded.Should().BeFalse();
            r.SourceMatchesRecorded.Should().BeFalse();
            r.Image.SourceDamagedChunks.Should().Be(1);
        }
    }

    [Fact]
    public async Task Imaging_from_a_damaged_container_reports_the_damage_in_the_result_and_the_log()
    {
        var media = EwfBuilder.Media(4096 * 4);
        var e01 = Path.Combine(_dir, "damaged-src.E01");
        await File.WriteAllBytesAsync(e01, EwfBuilder.Build(media, new EwfBuilder.Options { Compress = true, CorruptChunkIndex = 2 }));
        var outPath = Path.Combine(_dir, "from-damaged.dd");

        // Straight through the imager's own opener — the path the acquisition tool takes.
        var result = await new InProcessImager().ImageAsync(new ImageJob(e01, outPath, ImageFormat.Raw));

        result.SourceDamagedChunks.Should().Be(1);
        result.BadSectors.Should().Be(0, "nothing was unreadable on the medium; the container was damaged");
        using var log = JsonDocument.Parse(await File.ReadAllTextAsync(outPath + ".log.json"));
        log.RootElement.GetProperty("source_damaged_chunks").GetInt64().Should().Be(1);

        await using var opened = EvidenceOpener.Open(e01);
        var damage = opened.Should().BeAssignableTo<IEvidenceDamageSource>().Subject;
        damage.DamagedChunks.Should().BeEmpty("nothing has been read yet");
        opened.CopyTo(Stream.Null);
        damage.DamagedChunks.Should().Equal(2);
    }

    // ---- 10: the writer's own partial naming ----------------------------------------------------

    [Fact]
    public void Ewf_writer_with_a_partial_suffix_only_takes_its_real_names_on_finish()
    {
        var media = EwfBuilder.Media(9 * MiB);
        var path = Path.Combine(_dir, "suffix.E01");
        var options = new EwfWriter.Options { Compress = false, MaxSegmentBytes = 4 * MiB, PartialSuffix = ".partial" };

        using (var abandoned = new EwfWriter(path, options))
        {
            abandoned.Write(media);
            abandoned.WorkingPaths.Should().OnlyContain(p => p.EndsWith(".partial") && File.Exists(p));
        }
        Directory.GetFiles(_dir, "suffix.E??").Should().BeEmpty("a writer disposed before Finish leaves no segment files");
        foreach (var leftover in Directory.GetFiles(_dir, "suffix.*.partial"))
        {
            File.Delete(leftover);
        }

        using (var w = new EwfWriter(path, options))
        {
            w.Write(media);
            w.Finish(MD5.HashData(media), SHA1.HashData(media));
            w.SegmentPaths.Count.Should().BeGreaterThan(1);
            w.SegmentPaths.Should().OnlyContain(p => File.Exists(p));
        }
        Directory.GetFiles(_dir, "suffix.*.partial").Should().BeEmpty();
        using var r = EwfReader.Open(path);
        r.MediaSize.Should().Be(media.Length);
    }

    // ---- 11: the write blocker only claims what it confirmed ------------------------------------

#pragma warning disable CA1416 // the class is Linux-only in production; here its commands are faked
    [Fact]
    public void Write_blocker_is_not_engaged_when_blockdev_fails()
    {
        var blocker = new LinuxBlockdevWriteBlocker((file, args) => (file, args[0]) switch
        {
            ("lsblk", _) => (0, "sda disk\nsdb disk\nsr0 rom\n"),
            ("blockdev", "--setro") => args[1] == "/dev/sdb" ? (1, "") : (0, ""),   // e.g. not root
            ("blockdev", "--getro") => (0, "1\n"),
            _ => (0, ""),
        });

        blocker.TryEngage().Should().BeFalse();
        blocker.IsActive.Should().BeFalse("a disk is still writable");
        blocker.FailedDevices.Should().Equal("sdb");
        blocker.ProtectedDevices.Should().BeEquivalentTo("sda");
    }

    [Fact]
    public void Write_blocker_is_not_engaged_when_the_read_only_flag_does_not_read_back()
    {
        var blocker = new LinuxBlockdevWriteBlocker((file, args) => (file, args[0]) switch
        {
            ("lsblk", _) => (0, "sda disk\n"),
            ("blockdev", "--getro") => (0, "0\n"),   // setro "succeeded" but the device is still read-write
            _ => (0, ""),
        });

        blocker.TryEngage().Should().BeFalse();
        blocker.IsActive.Should().BeFalse();
        blocker.FailedDevices.Should().Equal("sda");
    }

    [Fact]
    public void Write_blocker_engages_when_every_disk_is_confirmed_read_only_and_releases_them()
    {
        var calls = new List<string>();
        var blocker = new LinuxBlockdevWriteBlocker((file, args) =>
        {
            calls.Add(file + " " + string.Join(' ', args));
            return (file, args[0]) switch
            {
                ("lsblk", _) => (0, "sda disk\nnvme0n1 disk\nsr0 rom\n"),
                ("blockdev", "--getro") => (0, "1\n"),
                _ => (0, ""),
            };
        });

        blocker.TryEngage().Should().BeTrue();
        blocker.IsActive.Should().BeTrue();
        blocker.FailedDevices.Should().BeEmpty();
        calls.Should().Contain("blockdev --setro /dev/sda").And.Contain("blockdev --setro /dev/nvme0n1");
        calls.Should().NotContain(c => c.Contains("sr0"), "optical drives are not acquisition targets");

        blocker.TryDisengage().Should().BeTrue();
        blocker.IsActive.Should().BeFalse();
        calls.Should().Contain("blockdev --setrw /dev/sda").And.Contain("blockdev --setrw /dev/nvme0n1");
    }
#pragma warning restore CA1416

    // ---- 13: the mount path is data, never script -----------------------------------------------

    [Fact]
    public async Task Windows_mounter_hands_powershell_the_path_as_a_value_not_as_command_text()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // powershell.exe is the thing under test
        }

        // A file name built to break out of a quoted string or run a subexpression.
        const string hostile = @"C:\evidence\a'; Write-Output INJECTED; $(Write-Output SUBEXPR) `"".vhd";
        var command = $"[Console]::Out.Write($env:{WindowsImageMounter.ImagePathVariable})";

        var psi = WindowsImageMounter.BuildPowerShell(command, hostile);
        psi.ArgumentList.Should().NotContain(a => a.Contains("evidence"), "the path must not be on the command line");
        psi.ArgumentList.Should().EndWith(command, "nothing may follow -Command's script");
        psi.Environment[WindowsImageMounter.ImagePathVariable].Should().Be(hostile);

        var echoed = await WindowsImageMounter.RunPowerShellAsync(command, hostile, CancellationToken.None);
        echoed.Should().Be(hostile, "PowerShell received the path intact and executed none of it");
    }

    // ---- 15: unrecordable bad sectors are counted, not misplaced --------------------------------

    [Fact]
    public void Ewf_writer_counts_the_bad_sectors_error2_cannot_hold()
    {
        var media = EwfBuilder.Media(64 * 1024);
        var path = Path.Combine(_dir, "far.E01");
        using var w = new EwfWriter(path);
        w.Write(media);
        w.AddBadSectors(10, 2);
        w.AddBadSectors(4_294_967_294L, 5);       // straddles sector 2^32: two fit, three do not
        w.AddBadSectors(5_000_000_000L, 3);       // wholly beyond
        w.Finish(MD5.HashData(media), SHA1.HashData(media));

        w.BadSectorsNotRecorded.Should().Be(6);
        RegressionTests.ReadError2(path).Should().Equal((10u, 2u), (4_294_967_294u, 2u));
    }
}

/// <summary>Hides the length of the stream it wraps, like a device that will not report one.</summary>
internal sealed class UnknownLengthStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Every read touching one of the listed 1 MiB blocks fails; the rest read normally.</summary>
internal sealed class BadBlocksStream(byte[] data, int[] badBlocks) : Stream
{
    private const int Block = 1024 * 1024;
    private long _pos;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _pos; set => _pos = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = (int)Math.Min(count, data.Length - _pos);
        if (n <= 0) return 0;
        for (var b = _pos / Block; b <= (_pos + n - 1) / Block; b++)
        {
            if (badBlocks.Contains((int)b)) throw new IOException("unreadable");
        }
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
