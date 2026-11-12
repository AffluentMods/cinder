using System.Security.Cryptography;
using Cinder.Artifacts.Linux;
using Cinder.Artifacts.Windows;
using Cinder.Filesystems;
using Cinder.Imaging;
using Cinder.Memory;
using FluentAssertions;
using Xunit;

namespace Cinder.Sidecar.Tests;

// The adapters against the real Python workers, for the parts that need no optional native
// library. The mapping tests elsewhere prove what the C# side does with a given reply; these
// prove the replies really have that shape, so the two sides cannot drift apart unnoticed.
// (Several small classes, so that xunit runs them in parallel.)

/// <summary>A scratch directory that is removed with the test.</summary>
public abstract class WorkerContractTestBase : IDisposable
{
    protected string Dir { get; } = Directory.CreateTempSubdirectory("cinder-sidecar-tests").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a worker may still be letting go of a file.
        }
        catch (UnauthorizedAccessException)
        {
        }
        GC.SuppressFinalize(this);
    }
}

public sealed class ImagerContractTests : WorkerContractTestBase
{
    [PythonFact]
    public async Task Imaging_then_verifying_through_the_real_worker()
    {
        var source = Path.Combine(Dir, "source.bin");
        var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 700);
        await File.WriteAllBytesAsync(source, data);
        var output = Path.Combine(Dir, "évidence 日本.dd");
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(data));
        var progress = new RecordingProgress<ImageJobProgress>();

        var imager = new SidecarDiskImager(() => PythonEnvironment.Module("imager.imager_worker"));
        var report = await imager.ImageDetailedAsync(new ImageJob(source, output, ImageFormat.Raw), progress).WaitAsync(Workers.Deadline);

        // The job is serialised with camelCase keys and a numeric format; the worker has to take that.
        report.Result.BytesWritten.Should().Be(data.Length);
        report.Result.Sha256.Should().Be(expectedSha);
        report.Result.BadSectors.Should().Be(0);
        report.Result.OutputPath.Should().Be(output, "a non-ASCII path survives both directions");
        report.Complete.Should().BeTrue();
        report.SourceSize.Should().Be(data.Length);
        report.BadRanges.Should().BeEmpty();
        report.IsFaithfulCopy.Should().BeTrue();
        report.DigestFiles.Should().Equal(output + ".sha256", output + ".md5");
        (await File.ReadAllBytesAsync(output)).Should().Equal(data);
        progress.Reports.Should().Contain(p => p.Phase == "imaging" && p.TotalBytes == data.Length, "the worker's own notifications are relayed");
        progress.Reports[^1].Phase.Should().Be("done");
        progress.Reports[^1].BytesRead.Should().Be(data.Length);

        var verifier = new ImageVerifier(() => PythonEnvironment.Module("imager.imager_worker"));
        var verified = await verifier.VerifyDetailedAsync(output).WaitAsync(Workers.Deadline);
        verified.Status.Should().Be(ImageVerificationStatus.Verified);
        verified.ExpectedSha256.Should().Be(expectedSha);
        verified.BytesVerified.Should().Be(data.Length);

        // The source has no companion digest: there is nothing to verify it against.
        var unreferenced = await verifier.VerifyDetailedAsync(source).WaitAsync(Workers.Deadline);
        unreferenced.Status.Should().Be(ImageVerificationStatus.NoReference);
        unreferenced.Match.Should().BeNull();
        unreferenced.Reason.Should().Contain("no reference hash");
        unreferenced.ActualSha256.Should().Be(expectedSha);

        // One flipped byte in the image.
        data[12345] ^= 0xFF;
        await File.WriteAllBytesAsync(output, data);
        var tampered = await verifier.VerifyDetailedAsync(output).WaitAsync(Workers.Deadline);
        tampered.Status.Should().Be(ImageVerificationStatus.Mismatch);
        tampered.Reason.Should().NotBeNullOrEmpty();
    }
}

public sealed class ArtifactWorkerContractTests : WorkerContractTestBase
{
    [PythonFact]
    public async Task A_format_the_worker_cannot_write_is_an_error_not_a_mislabelled_raw_image()
    {
        var source = Path.Combine(Dir, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[4096]);
        var output = Path.Combine(Dir, "evidence.vhd");

        var imager = new SidecarDiskImager(() => PythonEnvironment.Module("imager.imager_worker"));
        var act = () => imager.ImageAsync(new ImageJob(source, output, ImageFormat.Vhd)).WaitAsync(Workers.Deadline);

        (await act.Should().ThrowAsync<SidecarException>()).WithMessage("*not implemented*");
        File.Exists(output).Should().BeFalse();
    }

    [PythonFact]
    public async Task Auth_log_through_the_real_worker()
    {
        var root = Path.Combine(Dir, "evidence");
        var logDir = Directory.CreateDirectory(Path.Combine(root, "var", "log")).FullName;
        var log = Path.Combine(logDir, "auth.log");
        await File.WriteAllLinesAsync(log,
        [
            "Dec 31 23:59:58 web01 sshd[2]: Failed password for invalid user admin from 203.0.113.9 port 4242 ssh2",
            "Jan  1 00:00:05 web01 sshd[3]: Accepted password for alice from 10.0.0.3 port 3 ssh2",
            "Xyz 99 04:05:06 web01 sshd[4]: Accepted password for bob from 10.0.0.4 port 4 ssh2",
            "2021-03-04T05:06:07.123456+02:00 web01 sshd[5]: Accepted publickey for carol from 2001:db8::1 port 5 ssh2",
        ]);
        File.SetLastWriteTimeUtc(log, new DateTime(2019, 1, 2, 12, 0, 0, DateTimeKind.Utc));

        var sidecar = new LinuxArtifactSidecar(() => PythonEnvironment.Module("linux.linux_worker"));
        var result = await sidecar.LoadAuthLogAsync(log, root).WaitAsync(Workers.Deadline);

        result.Refused.Should().BeEmpty();
        result.Entries.Should().HaveCount(4);
        var december = result.Entries[0];
        december.Timestamp!.Value.DateTime.Should().Be(new DateTime(2018, 12, 31, 23, 59, 58), "the year comes from the file's mtime, with rollover");
        december.YearInferred.Should().BeTrue();
        december.TimeZoneKnown.Should().BeFalse();
        december.User.Should().Be("admin");
        december.RemoteHost.Should().Be("203.0.113.9");
        result.Entries[1].Timestamp!.Value.Year.Should().Be(2019);
        result.Entries[2].Timestamp.Should().BeNull();
        result.Entries[2].TimestampRaw.Should().Be("Xyz 99 04:05:06");
        result.Entries[3].TimeZoneKnown.Should().BeTrue();
        result.Entries[3].Timestamp.Should().Be(new DateTimeOffset(2021, 3, 4, 3, 6, 7, TimeSpan.Zero).AddTicks(1234560));
        result.Entries[3].YearInferred.Should().BeFalse();
        result.YearSource.Should().Be("file_mtime");
        result.YearReference.Should().Be(new DateTimeOffset(2019, 1, 2, 12, 0, 0, TimeSpan.Zero));

        // The same file, claimed to live under a root it is not inside: refused, not read.
        var elsewhere = Directory.CreateDirectory(Path.Combine(Dir, "other-root")).FullName;
        var outside = await sidecar.LoadAuthLogAsync(log, elsewhere).WaitAsync(Workers.Deadline);
        outside.Entries.Should().BeEmpty();
        outside.Refused.Should().ContainSingle().Which.Reason.Should().Contain("outside the evidence root");
        outside.IsComplete.Should().BeFalse();
    }

    [PythonFact]
    public async Task Prefetch_through_the_real_worker()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Dir, "Prefetch")).FullName;
        // A version 23 (Vista / 7) header: version, "SCCA", name at 0x10, hash at 0x4C,
        // last run at 0x80, run count at 0x98.
        var header = new byte[0x200];
        BitConverter.TryWriteBytes(header.AsSpan(0), 23);
        "SCCA"u8.CopyTo(header.AsSpan(4));
        System.Text.Encoding.Unicode.GetBytes("CALC.EXE").CopyTo(header.AsSpan(0x10));
        BitConverter.TryWriteBytes(header.AsSpan(0x4C), 0xDEADBEEF);
        var lastRun = new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero);
        BitConverter.TryWriteBytes(header.AsSpan(0x80), lastRun.ToFileTime());
        BitConverter.TryWriteBytes(header.AsSpan(0x98), 7);
        await File.WriteAllBytesAsync(Path.Combine(dir, "CALC.EXE-11111111.pf"), header);
        await File.WriteAllBytesAsync(Path.Combine(dir, "CHROME.EXE-22222222.pf"), [.. "MAM\u0004"u8, .. new byte[64]]);

        var result = await new WindowsArtifactSidecar(() => PythonEnvironment.Module("windows.win_worker"))
            .LoadPrefetchAsync(dir).WaitAsync(Workers.Deadline);

        var calc = result.Entries.Should().ContainSingle().Subject;
        calc.ExecutableName.Should().Be("CALC.EXE");
        calc.Hash.Should().Be("DEADBEEF");
        calc.RunCount.Should().Be(7);
        calc.LastRun.Should().Be(lastRun);
        calc.FormatVersion.Should().Be(23);
        calc.SourceFile.Should().Be("CALC.EXE-11111111.pf");
        result.Skipped.Should().Equal(new SkippedPrefetchFile("CHROME.EXE-22222222.pf", "compressed (MAM), not decoded"));
        result.CompressedNotDecoded.Should().Be(1);
    }
}

public sealed class MissingLibraryContractTests : WorkerContractTestBase
{
    [PythonFact]
    public async Task A_worker_whose_library_is_missing_fails_the_call_with_a_message_not_a_hang()
    {
        var image = Path.Combine(Dir, "not-an-image.bin");
        await File.WriteAllBytesAsync(image, new byte[1024]);

        // With pytsk3 / volatility3 absent this is "… is not installed"; with them present it is
        // their own complaint about a file that is not an image. Either way: a SidecarException.
        var identify = () => new PytskFilesystemParser(() => PythonEnvironment.Module("filesystem.fs_worker"))
            .IdentifyAsync(image).WaitAsync(Workers.Deadline);
        (await identify.Should().ThrowAsync<SidecarException>()).Which.Message.Should().NotBeNullOrWhiteSpace();

        var pstree = () => Drain(new VolatilitySidecar(() => PythonEnvironment.Module("memory.vol_worker")).ProcessTreeAsync(image))
            .WaitAsync(Workers.Deadline);
        await pstree.Should().ThrowAsync<SidecarException>();
    }

    private static async Task Drain<T>(IAsyncEnumerable<T> source)
    {
        await foreach (var item in source)
        {
            _ = item;
        }
    }
}
