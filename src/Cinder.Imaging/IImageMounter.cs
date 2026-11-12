using System.Runtime.InteropServices;
using Cinder.Core.Diagnostics;

namespace Cinder.Imaging;

public interface IImageMounter
{
    Task<MountedImage> MountReadOnlyAsync(string imagePath, CancellationToken ct = default);
    Task UnmountAsync(MountedImage handle, CancellationToken ct = default);
}

public sealed record MountedImage(
    string ImagePath,
    string MountPoint,
    string LoopDevice,           // /dev/loop0 on Linux, "Disk\\PhysicalDrive7" on Windows
    DateTimeOffset MountedAt);

public static class ImageMounterFactory
{
    public static IImageMounter ForCurrentPlatform(string parsersDir, string? mountRoot = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsImageMounter(parsersDir);
        }
        if (OperatingSystem.IsLinux())
        {
            return new LinuxLoopMounter(mountRoot ?? "/mnt/cinder");
        }
        throw new PlatformNotSupportedException(
            $"Image mounting is not yet implemented for {RuntimeInformation.OSDescription}.");
    }
}

/// <summary>
/// Mounts an image read-only through a loop device.
///
/// <para>The loop device is attached with <c>--read-only</c>, so nothing the kernel does on
/// top of it can reach the image file. The mount options are chosen per file system on top of
/// that, to keep the driver from even trying to replay a journal: <c>noload</c> for ext3/ext4,
/// <c>norecovery</c> for XFS and f2fs, the log-replay switches for btrfs, plain <c>ro</c> where
/// there is no such thing. When the file system cannot be identified, or a kernel rejects an
/// option, the next candidate is tried — every one of them read-only. If none works the loop
/// device is detached and the mount point removed before the failure is reported.</para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LinuxLoopMounter : IImageMounter
{
    /// <summary>Exit code and output of one command.</summary>
    internal readonly record struct CommandResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>Runs a command to completion.</summary>
    internal delegate Task<CommandResult> CommandRunner(string file, IReadOnlyList<string> args, CancellationToken ct);

    /// <summary>Appended to every candidate: nothing on evidence is executed or trusted.</summary>
    private const string Hardening = ",noexec,nosuid,nodev";

    private readonly string _mountRoot;
    private readonly CommandRunner _run;

    public LinuxLoopMounter(string mountRoot)
        : this(mountRoot, RunProcessAsync)
    {
    }

    /// <summary>Test seam: commands go to <paramref name="run"/> instead of real processes.</summary>
    internal LinuxLoopMounter(string mountRoot, CommandRunner run)
    {
        _mountRoot = mountRoot;
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public async Task<MountedImage> MountReadOnlyAsync(string imagePath, CancellationToken ct = default)
    {
        // SECURITY: arguments are passed individually via ArgumentList so neither imagePath nor
        // mountPoint can break out of their slot via shell metacharacters or embedded quotes.
        var attach = await _run("losetup", ["--read-only", "--find", "--show", imagePath], ct).ConfigureAwait(false);
        if (attach.ExitCode != 0)
        {
            throw new InvalidOperationException(Describe("losetup", attach));
        }
        var loop = attach.StdOut.Trim();
        if (string.IsNullOrEmpty(loop))
        {
            throw new InvalidOperationException("losetup did not return a loop device.");
        }

        // From here on the loop device exists and is ours to release on every way out.
        string? mountPoint = null;
        try
        {
            Directory.CreateDirectory(_mountRoot);
            mountPoint = Path.Combine(_mountRoot, $"img-{Guid.NewGuid():N}");
            Directory.CreateDirectory(mountPoint);

            var fileSystem = await DetectFileSystemAsync(loop, ct).ConfigureAwait(false);
            CommandResult last = default;
            foreach (var options in ReadOnlyOptionsFor(fileSystem))
            {
                last = await _run("mount", ["-o", options, loop, mountPoint], ct).ConfigureAwait(false);
                if (last.ExitCode == 0)
                {
                    return new MountedImage(imagePath, mountPoint, loop, DateTimeOffset.UtcNow);
                }
            }
            throw new InvalidOperationException(
                $"Could not mount {loop} read-only (file system: {fileSystem ?? "not identified"}). {Describe("mount", last)}");
        }
        catch
        {
            // Cleanup must happen even when the caller's token is what stopped us.
            try { await _run("losetup", ["-d", loop], CancellationToken.None).ConfigureAwait(false); } catch { }
            if (mountPoint is not null)
            {
                try { Directory.Delete(mountPoint, recursive: false); } catch { }
            }
            throw;
        }
    }

    public async Task UnmountAsync(MountedImage handle, CancellationToken ct = default)
    {
        var unmount = await _run("umount", [handle.MountPoint], ct).ConfigureAwait(false);
        if (unmount.ExitCode != 0)
        {
            throw new InvalidOperationException(Describe("umount", unmount));
        }
        var detach = await _run("losetup", ["-d", handle.LoopDevice], ct).ConfigureAwait(false);
        try { Directory.Delete(handle.MountPoint, recursive: false); } catch { }
        if (detach.ExitCode != 0)
        {
            throw new InvalidOperationException(Describe("losetup -d", detach));
        }
    }

    /// <summary>The file system on the device as <c>blkid</c> names it, lower-case; null when it cannot tell.</summary>
    private async Task<string?> DetectFileSystemAsync(string device, CancellationToken ct)
    {
        try
        {
            var probe = await _run("blkid", ["-o", "value", "-s", "TYPE", device], ct).ConfigureAwait(false);
            var type = probe.StdOut.Trim().ToLowerInvariant();
            return probe.ExitCode == 0 && type.Length > 0 ? type : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;   // blkid missing: fall back to trying the candidates in turn
        }
    }

    /// <summary>
    /// Mount option strings to try, in order, for a file system. Every one is read-only; the
    /// earlier ones also tell the driver not to replay its journal. A later, plainer candidate
    /// is still safe to fall back to, because the loop device underneath is itself read-only.
    /// </summary>
    internal static IReadOnlyList<string> ReadOnlyOptionsFor(string? fileSystem)
    {
        string[] candidates = fileSystem switch
        {
            "ext3" or "ext4" => ["ro,noload", "ro"],
            "xfs" or "f2fs" => ["ro,norecovery", "ro"],
            "btrfs" => ["ro,rescue=nologreplay", "ro,nologreplay", "ro,norecovery", "ro"],
            "ext2" or "ntfs" or "ntfs3" or "vfat" or "msdos" or "exfat" or "iso9660" or "udf"
                or "hfs" or "hfsplus" or "squashfs" => ["ro"],
            // Not identified, or something this list does not know: try the journal switches
            // the common file systems accept, then plain read-only.
            _ => ["ro,noload", "ro,norecovery", "ro"],
        };
        return [.. candidates.Select(c => c + Hardening)];
    }

    private static string Describe(string command, CommandResult result)
    {
        var detail = result.StdErr.Trim();
        return detail.Length == 0 ? $"{command}: exit {result.ExitCode}" : $"{command}: exit {result.ExitCode}: {detail}";
    }

    private static async Task<CommandResult> RunProcessAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ExecutableResolver.ResolveRequired(file))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        p.Start();
        // Both pipes are drained together; reading only one can block the child on the other.
        var stderr = p.StandardError.ReadToEndAsync(ct);
        var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var errors = await stderr.ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return new CommandResult(p.ExitCode, stdout, errors);
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsImageMounter(string parsersDir) : IImageMounter
{
    private readonly string _parsersDir = parsersDir;

    /// <summary>
    /// Windows mounting in Phase 2 leans on Arsenal Image Mounter (free, supports E01/raw/VHD)
    /// or PowerShell <c>Mount-DiskImage</c> for VHD/VHDX/ISO. Cinder's bundled mounter wraps
    /// PowerShell when Arsenal isn't installed, falling back to a sidecar for E01.
    /// </summary>
    public async Task<MountedImage> MountReadOnlyAsync(string imagePath, CancellationToken ct = default)
    {
        // SECURITY: the image path is never part of the PowerShell command text. It travels in
        // an environment variable of the child process and the fixed command reads it back as
        // a string value, so nothing in a file name can be parsed as script.
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        if (ext is ".vhd" or ".vhdx" or ".iso" or ".img")
        {
            var letter = await RunPowerShellAsync(
                command:
                    $"Mount-DiskImage -Access ReadOnly -ImagePath $env:{ImagePathVariable} | Out-Null; " +
                    $"(Get-DiskImage -ImagePath $env:{ImagePathVariable} | Get-Volume).DriveLetter",
                imagePath: imagePath,
                ct: ct).ConfigureAwait(false);
            letter = letter.Trim();
            if (string.IsNullOrEmpty(letter))
            {
                throw new InvalidOperationException("Mount-DiskImage returned no drive letter.");
            }
            return new MountedImage(imagePath, $"{letter}:\\", $"DiskImage:{imagePath}", DateTimeOffset.UtcNow);
        }

        // E01 / AFF4 — TODO: wrap Arsenal Image Mounter CLI when installed; otherwise fall back
        // to a Python sidecar that exposes the image as a virtual disk via a userland HTTP/iSCSI
        // shim. Tracked in LIMITATIONS.md → "windows-e01-mount".
        _ = _parsersDir;
        throw new NotSupportedException(
            "Windows E01/AFF4 mounting requires Arsenal Image Mounter (free) or the libewf Windows build. " +
            "See LIMITATIONS.md → windows-e01-mount.");
    }

    public async Task UnmountAsync(MountedImage handle, CancellationToken ct = default)
    {
        if (handle.LoopDevice.StartsWith("DiskImage:", StringComparison.Ordinal))
        {
            var path = handle.LoopDevice["DiskImage:".Length..];
            await RunPowerShellAsync(
                command: $"Dismount-DiskImage -ImagePath $env:{ImagePathVariable} | Out-Null",
                imagePath: path,
                ct: ct).ConfigureAwait(false);
        }
    }

    /// <summary>Environment variable that carries the image path to the PowerShell child.</summary>
    internal const string ImagePathVariable = "CINDER_IMAGE_PATH";

    /// <summary>
    /// Start info for a fixed PowerShell command that needs one image path. The command is a
    /// compile-time string; the path is put in the child's environment as
    /// <see cref="ImagePathVariable"/>, where PowerShell reads it as data.
    ///
    /// <para>It is deliberately not passed on the command line. Everything that follows
    /// <c>-Command</c> is joined into the command text and parsed as script, so an argument
    /// placed there never reaches <c>$args</c> — and a crafted file name would be executed.</para>
    /// </summary>
    internal static System.Diagnostics.ProcessStartInfo BuildPowerShell(string command, string imagePath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ExecutableResolver.ResolveRequired("powershell.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        psi.Environment[ImagePathVariable] = imagePath;
        return psi;
    }

    internal static async Task<string> RunPowerShellAsync(string command, string imagePath, CancellationToken ct)
    {
        using var p = new System.Diagnostics.Process { StartInfo = BuildPowerShell(command, imagePath) };
        p.Start();
        // Both pipes are drained together; reading only one can block the child on the other.
        var stderr = p.StandardError.ReadToEndAsync(ct);
        var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var errors = await stderr.ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            var detail = errors.Trim();
            throw new InvalidOperationException(detail.Length == 0
                ? $"powershell: exit {p.ExitCode}"
                : $"powershell: exit {p.ExitCode}: {detail}");
        }
        return stdout;
    }
}
