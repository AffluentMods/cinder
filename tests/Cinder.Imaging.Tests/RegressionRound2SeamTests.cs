using System.Security.Cryptography;
using System.Text.Json;
using Cinder.Imaging.Aff4;
using Cinder.Imaging.Ewf;
using FluentAssertions;
using Xunit;

namespace Cinder.Imaging.Tests;

/// <summary>
/// Second-round fixes checked through what they added: the overwrite switches, the reader's
/// account of the segments it left out, and the loop mounter's command seam.
/// </summary>
public sealed class RegressionRound2SeamTests : IDisposable
{
    private const int MiB = 1024 * 1024;
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-r2s").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static EwfWriter.Options Segmented(bool overwrite = false, string? partialSuffix = null) =>
        new() { MaxSegmentBytes = 4 * MiB, Compress = false, Overwrite = overwrite, PartialSuffix = partialSuffix };

    private static void Write(string first, byte[] media, EwfWriter.Options options, out List<string> segments, out List<string> removed)
    {
        using var w = new EwfWriter(first, options);
        w.Write(media);
        w.Finish(MD5.HashData(media), SHA1.HashData(media));
        segments = [.. w.SegmentPaths];
        removed = [.. w.RemovedStaleSegments];
    }

    // ---- 1: the reader says what it left out ---------------------------------------------------

    [Fact]
    public void Reader_reports_the_segments_it_ignored_and_why()
    {
        var olderDir = Directory.CreateDirectory(Path.Combine(_dir, "older")).FullName;
        var newerDir = Directory.CreateDirectory(Path.Combine(_dir, "newer")).FullName;
        Write(Path.Combine(olderDir, "img.E01"), EwfBuilder.Media(13 * MiB, seed: 1), Segmented(), out var older, out _);
        Write(Path.Combine(newerDir, "img.E01"), EwfBuilder.Media(5 * MiB, seed: 2), Segmented(), out var newer, out _);

        // Stale tail after a finished image.
        File.Copy(older[2], EwfWriter.SegmentPath(newer[0], 3));
        File.Copy(older[3], EwfWriter.SegmentPath(newer[0], 4));
        using (var r = EwfReader.Open(newer[0]))
        {
            r.EndsWithDone.Should().BeTrue();
            r.IgnoredSegments.Should().HaveCount(2);
            r.IgnoredSegments[0].Should().Contain("img.E03").And.Contain("the image ends at the segment before it");
        }

        // A foreign segment where the chain expected its own: the chain is short, and says so.
        File.Copy(newer[1], EwfWriter.SegmentPath(older[0], 2), overwrite: true);
        using (var r = EwfReader.Open(older[0]))
        {
            r.SegmentCount.Should().Be(1);
            r.EndsWithDone.Should().BeFalse("segment one ends with `next`; its successor is missing");
            r.IgnoredSegments[0].Should().Contain("img.E02").And.Contain("set identifier");
        }
    }

    [Fact]
    public void Reader_of_a_clean_chain_ignores_nothing()
    {
        var first = Path.Combine(_dir, "clean.E01");
        Write(first, EwfBuilder.Media(9 * MiB), Segmented(), out var segments, out _);

        using var r = EwfReader.Open(first);
        r.SegmentCount.Should().Be(segments.Count);
        r.IgnoredSegments.Should().BeEmpty();
        r.EndsWithDone.Should().BeTrue();
    }

    // ---- 1: the writer's overwrite switch -------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData(".partial")]
    public async Task Ewf_writer_asked_to_overwrite_a_longer_chain_removes_the_segments_it_did_not_reuse(string? partialSuffix)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "overwrite" + partialSuffix)).FullName;
        var first = Path.Combine(dir, "img.E01");
        Write(first, EwfBuilder.Media(13 * MiB, seed: 1), Segmented(), out var older, out _);
        older.Count.Should().BeGreaterThanOrEqualTo(4);

        var media = EwfBuilder.Media(5 * MiB, seed: 2);
        Write(first, media, Segmented(overwrite: true, partialSuffix), out var newer, out var removed);

        newer.Count.Should().Be(2);
        removed.Should().Equal(older.Skip(2), "every stale segment is deleted, and reported");
        Directory.GetFiles(dir).Should().BeEquivalentTo(newer);

        using var r = EwfReader.Open(first);
        r.SegmentCount.Should().Be(2);
        r.IgnoredSegments.Should().BeEmpty();
        (await r.VerifyAsync()).Verified.Should().BeTrue();
    }

    [Fact]
    public void Ewf_writer_refusal_names_the_files_and_covers_partial_names()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "refuse")).FullName;
        var first = Path.Combine(dir, "img.E01");
        var leftover = EwfWriter.SegmentPath(first, 2) + ".partial";
        File.WriteAllBytes(leftover, [1, 2, 3]);

        var act = () => new EwfWriter(first, new EwfWriter.Options { PartialSuffix = ".partial" });
        act.Should().Throw<OutputExistsException>().Which.ExistingPaths.Should().Equal(leftover);
        Directory.GetFiles(dir).Should().Equal(leftover);
    }

    [Fact]
    public void Aff4_writer_refuses_an_existing_file_unless_asked_to_overwrite()
    {
        var path = Path.Combine(_dir, "exists.aff4");
        File.WriteAllBytes(path, [9, 9, 9]);

        var act = () => new Aff4Writer(path);
        act.Should().Throw<OutputExistsException>().Which.ExistingPaths.Should().Equal(path);
        File.ReadAllBytes(path).Should().Equal(9, 9, 9);

        var media = EwfBuilder.CompressibleMedia(64 * 1024);
        using (var w = new Aff4Writer(path, new Aff4Writer.Options { Overwrite = true }))
        {
            w.Write(media);
            w.Finish(MD5.HashData(media), null, null);
        }
        using var r = Aff4Reader.Open(path);
        r.Size.Should().Be(media.Length);
    }

    // ---- the job's Overwrite flag ----------------------------------------------------------------

    [Theory]
    [InlineData(ImageFormat.Raw)]
    [InlineData(ImageFormat.Ewf)]
    [InlineData(ImageFormat.Aff4)]
    [InlineData(ImageFormat.Vhd)]
    [InlineData(ImageFormat.Vhdx)]
    public async Task Imaging_with_overwrite_replaces_the_image_and_its_leftovers(ImageFormat format)
    {
        var data = EwfBuilder.Media(2 * MiB);
        var src = Path.Combine(_dir, "src.bin");
        await File.WriteAllBytesAsync(src, data);
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "ow-" + format)).FullName;
        var outPath = Path.Combine(dir, "image" + format.DefaultExtension());
        await File.WriteAllBytesAsync(outPath, [1, 2, 3]);
        await File.WriteAllBytesAsync(outPath + ".partial", [4, 5, 6]);

        // Refused by default, and the refusal says which files and which switch.
        var refused = () => new InProcessImager().ImageAsync(new ImageJob(src, outPath, format));
        var thrown = (await refused.Should().ThrowAsync<OutputExistsException>()).Which;
        thrown.ExistingPaths.Should().BeEquivalentTo([outPath, outPath + ".partial"]);
        if (format != ImageFormat.Ewf)
        {
            thrown.Message.Should().Contain("ImageJob.Overwrite");
        }

        var result = await new InProcessImager().ImageAsync(new ImageJob(src, outPath, format, Overwrite: true));

        result.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(data)));
        File.Exists(outPath + ".partial").Should().BeFalse();
        await using var s = EvidenceOpener.Open(outPath);
        var back = new MemoryStream();
        await s.CopyToAsync(back);
        back.ToArray().Should().Equal(data);

        using var log = JsonDocument.Parse(await File.ReadAllTextAsync(outPath + ".log.json"));
        log.RootElement.GetProperty("overwrite").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Imaging_with_overwrite_over_a_longer_e01_chain_leaves_one_clean_image_and_logs_what_it_removed()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "ow-chain")).FullName;
        var outPath = Path.Combine(dir, "image.E01");
        var longSrc = Path.Combine(_dir, "long.bin");
        var shortSrc = Path.Combine(_dir, "short.bin");
        await File.WriteAllBytesAsync(longSrc, EwfBuilder.Media(13 * MiB, seed: 21));
        var shortData = EwfBuilder.Media(5 * MiB, seed: 22);
        await File.WriteAllBytesAsync(shortSrc, shortData);

        await new InProcessImager().ImageAsync(new ImageJob(longSrc, outPath, ImageFormat.Ewf, CompressionLevel: 0, SegmentSizeMiB: 4));
        Directory.GetFiles(dir, "image.E??").Length.Should().BeGreaterThanOrEqualTo(4);

        var result = await new InProcessImager().ImageAsync(
            new ImageJob(shortSrc, outPath, ImageFormat.Ewf, CompressionLevel: 0, SegmentSizeMiB: 4, Overwrite: true));

        Directory.GetFiles(dir, "image.E??").Should().HaveCount(2);
        using var r = EwfReader.Open(outPath);
        r.IgnoredSegments.Should().BeEmpty();
        r.MediaSize.Should().Be(shortData.Length);
        r.RecordedMd5.Should().Be(result.Md5);
        (await r.VerifyAsync()).Verified.Should().BeTrue();

        using var log = JsonDocument.Parse(await File.ReadAllTextAsync(outPath + ".log.json"));
        log.RootElement.GetProperty("removed_stale_segments").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Conversion_passes_the_overwrite_choice_through()
    {
        var data = EwfBuilder.Media(MiB);
        var src = Path.Combine(_dir, "conv.dd");
        await File.WriteAllBytesAsync(src, data);
        var outPath = Path.Combine(_dir, "conv.E01");
        await File.WriteAllBytesAsync(outPath, [7]);

        var refused = () => ImageConverter.ConvertAsync(src, outPath, ImageFormat.Ewf);
        await refused.Should().ThrowAsync<OutputExistsException>();

        var r = await ImageConverter.ConvertAsync(src, outPath, ImageFormat.Ewf, overwrite: true);
        r.MatchesRecorded.Should().BeTrue();
    }

    // ---- 2: the loop mounter ---------------------------------------------------------------------

#pragma warning disable CA1416 // Linux-only in production; its commands are faked here
    /// <summary>Records every command and answers from a script keyed on the program name.</summary>
    private sealed class FakeCommands(Func<string, IReadOnlyList<string>, LinuxLoopMounter.CommandResult> answer)
    {
        public List<string> Calls { get; } = [];

        public Task<LinuxLoopMounter.CommandResult> Run(string file, IReadOnlyList<string> args, CancellationToken ct)
        {
            Calls.Add(file + " " + string.Join(' ', args));
            return Task.FromResult(answer(file, args));
        }
    }

    private static LinuxLoopMounter.CommandResult Ok(string stdout = "") => new(0, stdout, "");
    private static LinuxLoopMounter.CommandResult Fail(int code, string stderr) => new(code, "", stderr);

    [Fact]
    public async Task Loop_device_is_detached_and_the_mount_point_removed_when_the_mount_fails()
    {
        var root = Path.Combine(_dir, "mnt-fail");
        var commands = new FakeCommands((file, args) => file switch
        {
            "losetup" when args[0] == "--read-only" => Ok("/dev/loop7\n"),
            "blkid" => Ok("ntfs\n"),
            "mount" => Fail(32, "mount: wrong fs type, bad option, bad superblock on /dev/loop7"),
            _ => Ok(),
        });
        var mounter = new LinuxLoopMounter(root, commands.Run);

        var act = () => mounter.MountReadOnlyAsync("/evidence/disk.dd");
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*bad superblock*");

        commands.Calls.Should().Contain("losetup -d /dev/loop7", "a failed mount must not leave the loop device attached");
        commands.Calls.Last().Should().Be("losetup -d /dev/loop7");
        Directory.GetDirectories(root).Should().BeEmpty("the unused mount point is removed");
    }

    [Theory]
    [InlineData("ext4", "ro,noload,noexec,nosuid,nodev")]
    [InlineData("xfs", "ro,norecovery,noexec,nosuid,nodev")]
    [InlineData("ntfs", "ro,noexec,nosuid,nodev")]
    [InlineData("vfat", "ro,noexec,nosuid,nodev")]
    public async Task Mount_options_fit_the_file_system(string fileSystem, string expectedOptions)
    {
        var root = Path.Combine(_dir, "mnt-" + fileSystem);
        var commands = new FakeCommands((file, args) => file switch
        {
            "losetup" => Ok("/dev/loop3\n"),
            "blkid" => Ok(fileSystem + "\n"),
            _ => Ok(),
        });
        var mounter = new LinuxLoopMounter(root, commands.Run);

        var mounted = await mounter.MountReadOnlyAsync("/evidence/disk.dd");

        mounted.LoopDevice.Should().Be("/dev/loop3");
        commands.Calls.Should().Contain("losetup --read-only --find --show /evidence/disk.dd");
        commands.Calls.Where(c => c.StartsWith("mount ")).Should().Equal($"mount -o {expectedOptions} /dev/loop3 {mounted.MountPoint}");
        commands.Calls.Should().NotContain(c => c.StartsWith("losetup -d"));

        await mounter.UnmountAsync(mounted);
        commands.Calls.TakeLast(2).Should().Equal($"umount {mounted.MountPoint}", "losetup -d /dev/loop3");
        Directory.Exists(mounted.MountPoint).Should().BeFalse();
    }

    [Fact]
    public async Task An_option_the_kernel_rejects_falls_back_to_the_next_read_only_candidate()
    {
        // blkid cannot say what it is; the driver that claims it does not know `noload`.
        var root = Path.Combine(_dir, "mnt-fallback");
        var commands = new FakeCommands((file, args) => file switch
        {
            "losetup" => Ok("/dev/loop5\n"),
            "blkid" => Fail(2, ""),
            "mount" => args[1].Contains("noload") ? Fail(32, "mount: unknown mount option 'noload'") : Ok(),
            _ => Ok(),
        });
        var mounter = new LinuxLoopMounter(root, commands.Run);

        var mounted = await mounter.MountReadOnlyAsync("/evidence/disk.dd");

        var attempts = commands.Calls.Where(c => c.StartsWith("mount ")).ToList();
        attempts.Should().HaveCount(2);
        attempts[0].Should().Contain("-o ro,noload,");
        attempts[1].Should().Contain("-o ro,norecovery,");
        attempts.Should().OnlyContain(c => c.Contains("-o ro,"), "no attempt is ever anything but read-only");
        Directory.Exists(mounted.MountPoint).Should().BeTrue();
    }

    [Fact]
    public void Every_mount_candidate_for_every_file_system_is_read_only()
    {
        foreach (var fs in new string?[] { null, "", "ext2", "ext3", "ext4", "xfs", "btrfs", "f2fs", "ntfs", "ntfs3", "vfat", "exfat", "iso9660", "udf", "hfsplus", "reiserfs", "zfs_member" })
        {
            var candidates = LinuxLoopMounter.ReadOnlyOptionsFor(fs);
            candidates.Should().NotBeEmpty();
            foreach (var options in candidates)
            {
                var parts = options.Split(',');
                parts[0].Should().Be("ro", $"file system '{fs}'");
                parts.Should().NotContain("rw").And.Contain("noexec").And.Contain("nosuid").And.Contain("nodev");
            }
            if (fs is not ("ext3" or "ext4" or null or "" or "reiserfs" or "zfs_member"))
            {
                candidates.Should().NotContain(o => o.Contains("noload"), $"only ext3/ext4 accept noload, not '{fs}'");
            }
        }
    }
#pragma warning restore CA1416
}
