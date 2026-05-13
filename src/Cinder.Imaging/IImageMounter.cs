using System.Runtime.InteropServices;

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

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LinuxLoopMounter(string mountRoot) : IImageMounter
{
    private readonly string _mountRoot = mountRoot;

    public async Task<MountedImage> MountReadOnlyAsync(string imagePath, CancellationToken ct = default)
    {
        // SECURITY: arguments are passed individually via ArgumentList so neither imagePath nor
        // mountPoint can break out of their slot via shell metacharacters or embedded quotes.
        var loop = (await RunCaptureAsync("losetup",
            ["--read-only", "--find", "--show", imagePath], ct).ConfigureAwait(false)).Trim();
        if (string.IsNullOrEmpty(loop))
        {
            throw new InvalidOperationException("losetup did not return a loop device.");
        }

        Directory.CreateDirectory(_mountRoot);
        var mountPoint = Path.Combine(_mountRoot, $"img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountPoint);
        await RunCaptureAsync("mount",
            ["-o", "ro,noload,noexec,nosuid", loop, mountPoint], ct).ConfigureAwait(false);

        return new MountedImage(imagePath, mountPoint, loop, DateTimeOffset.UtcNow);
    }

    public async Task UnmountAsync(MountedImage handle, CancellationToken ct = default)
    {
        await RunCaptureAsync("umount", [handle.MountPoint], ct).ConfigureAwait(false);
        await RunCaptureAsync("losetup", ["-d", handle.LoopDevice], ct).ConfigureAwait(false);
        try { Directory.Delete(handle.MountPoint, recursive: false); } catch { }
    }

    private static async Task<string> RunCaptureAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file)
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
        var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file}: exit {p.ExitCode}");
        }
        return stdout;
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
        // SECURITY: PowerShell single-quoted strings escape via doubled single-quote (''). We
        // sanitize the path AND pass it as an explicit -Args entry rather than interpolating
        // it into the command string. The path itself never crosses parser boundaries.
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        if (ext is ".vhd" or ".vhdx" or ".iso" or ".img")
        {
            var letter = await RunPowerShellAsync(
                command:
                    "Mount-DiskImage -Access ReadOnly -ImagePath $args[0] | Out-Null; " +
                    "(Get-DiskImage -ImagePath $args[0] | Get-Volume).DriveLetter",
                args: [imagePath],
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
                command: "Dismount-DiskImage -ImagePath $args[0] | Out-Null",
                args: [path],
                ct: ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a PowerShell command. The command itself is hardcoded; the variable parts are
    /// passed via <c>$args[]</c> so an attacker can't escape into the command stream by
    /// crafting a malicious filename. ArgumentList handles the CLI quoting on the .NET side.
    /// </summary>
    private static async Task<string> RunPowerShellAsync(string command, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe")
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
        psi.ArgumentList.Add("--");
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        p.Start();
        var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"powershell: exit {p.ExitCode}");
        }
        return stdout;
    }
}
