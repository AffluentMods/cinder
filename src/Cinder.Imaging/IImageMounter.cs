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
    public static IImageMounter ForCurrentPlatform(string parsersDir, string? mountRoot = null) =>
        OperatingSystem.IsWindows()
            ? new WindowsImageMounter(parsersDir)
            : new LinuxLoopMounter(mountRoot ?? "/mnt/cinder");
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LinuxLoopMounter(string mountRoot) : IImageMounter
{
    private readonly string _mountRoot = mountRoot;

    public async Task<MountedImage> MountReadOnlyAsync(string imagePath, CancellationToken ct = default)
    {
        // 1. losetup --read-only --find --show <imagePath>
        var loop = await RunCaptureAsync("losetup", $"--read-only --find --show \"{imagePath}\"", ct).ConfigureAwait(false);
        loop = loop.Trim();
        if (string.IsNullOrEmpty(loop))
        {
            throw new InvalidOperationException("losetup did not return a loop device.");
        }

        // 2. mount -o ro,noload,noexec,nosuid <loop> <mount_point>
        Directory.CreateDirectory(_mountRoot);
        var mountPoint = Path.Combine(_mountRoot, $"img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountPoint);
        await RunAsync("mount", $"-o ro,noload,noexec,nosuid \"{loop}\" \"{mountPoint}\"", ct).ConfigureAwait(false);

        return new MountedImage(imagePath, mountPoint, loop, DateTimeOffset.UtcNow);
    }

    public async Task UnmountAsync(MountedImage handle, CancellationToken ct = default)
    {
        await RunAsync("umount", $"\"{handle.MountPoint}\"", ct).ConfigureAwait(false);
        await RunAsync("losetup", $"-d \"{handle.LoopDevice}\"", ct).ConfigureAwait(false);
        try { Directory.Delete(handle.MountPoint, recursive: false); } catch { }
    }

    private static async Task<string> RunCaptureAsync(string file, string args, CancellationToken ct)
    {
        using var p = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            },
        };
        p.Start();
        var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} {args}: exit {p.ExitCode}");
        }
        return stdout;
    }

    private static Task RunAsync(string file, string args, CancellationToken ct) => RunCaptureAsync(file, args, ct);
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
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        if (ext is ".vhd" or ".vhdx" or ".iso" or ".img")
        {
            // PowerShell native path — works on Win10+ without third-party.
            using var p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    $"-NoProfile -NonInteractive -Command \"Mount-DiskImage -Access ReadOnly -ImagePath '{imagePath}' | Out-Null; (Get-DiskImage -ImagePath '{imagePath}' | Get-Volume).DriveLetter\"")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                },
            };
            p.Start();
            var letter = (await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
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

    public Task UnmountAsync(MountedImage handle, CancellationToken ct = default)
    {
        if (handle.LoopDevice.StartsWith("DiskImage:", StringComparison.Ordinal))
        {
            var path = handle.LoopDevice["DiskImage:".Length..];
            using var p = System.Diagnostics.Process.Start("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"Dismount-DiskImage -ImagePath '{path}'\"");
            return p?.WaitForExitAsync(ct) ?? Task.CompletedTask;
        }
        return Task.CompletedTask;
    }
}
