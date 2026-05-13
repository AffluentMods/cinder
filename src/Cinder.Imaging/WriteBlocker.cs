using System.Diagnostics;
using System.Runtime.Versioning;
using Cinder.Native;

namespace Cinder.Imaging;

public static class WriteBlockerService
{
    public static IWriteBlocker ForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsWriteBlocker();
        }
        if (OperatingSystem.IsLinux())
        {
            return new LinuxBlockdevWriteBlocker();
        }
        return new NullWriteBlocker();
    }

    /// <summary>Fallback when the host platform has no first-party write-blocker yet.</summary>
    private sealed class NullWriteBlocker : IWriteBlocker
    {
        public bool IsActive => false;
        public bool TryEngage() => false;
        public bool TryDisengage() => true;
    }
}

/// <summary>
/// Linux software write-blocker. Toggles <c>blockdev --setro &lt;dev&gt;</c> on every block device
/// returned by <c>lsblk -dno NAME</c>. Doesn't block userland writes through the VFS — that's the
/// <c>dm-readonly</c> device-mapper wrap, which Phase 2.1 will add. For Phase 2 this is enough to
/// freeze sd*/nvme* writes from the kernel down.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxBlockdevWriteBlocker : IWriteBlocker
{
    private readonly HashSet<string> _frozen = new();

    public bool IsActive => _frozen.Count > 0;

    public bool TryEngage()
    {
        try
        {
            var devs = ListBlockDevices();
            foreach (var d in devs)
            {
                // SECURITY: `d` comes from lsblk output. We allow-list valid device-name
                // characters before splicing it into a device path — defense in depth even
                // though blockdev runs as a separate Process invocation with ArgumentList.
                if (!IsSafeDeviceName(d))
                {
                    continue;
                }
                Run("blockdev", ["--setro", "/dev/" + d]);
                _frozen.Add(d);
            }
            return _frozen.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public bool TryDisengage()
    {
        var ok = true;
        foreach (var d in _frozen.ToArray())
        {
            try
            {
                if (!IsSafeDeviceName(d))
                {
                    continue;
                }
                Run("blockdev", ["--setrw", "/dev/" + d]);
                _frozen.Remove(d);
            }
            catch
            {
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>Letters, digits, dash, underscore. Linux device names are restricted to these.</summary>
    private static bool IsSafeDeviceName(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 64)
        {
            return false;
        }
        foreach (var c in s)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }
        return true;
    }

    private static IEnumerable<string> ListBlockDevices()
    {
        var psi = new ProcessStartInfo("lsblk")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-dno");
        psi.ArgumentList.Add("NAME");
        using var p = new Process { StartInfo = psi };
        p.Start();
        var raw = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim());
    }

    private static void Run(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi);
        p?.WaitForExit();
    }
}

/// <summary>
/// Windows software write-blocker. The proper implementation is a kernel filter driver
/// (<c>drivers/cinder-wb-windows/</c>); until that ships signed, this class talks to a stub
/// service via DeviceIoControl IOCTLs. **TODO**: needs <c>drivers/cinder-wb-windows</c> built and
/// signed via SignPath.io. See <c>LIMITATIONS.md → write-blocker-windows</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWriteBlocker : IWriteBlocker
{
    public bool IsActive { get; private set; }

    public bool TryEngage()
    {
        // TODO: open \\.\CinderWB device handle, send IOCTL_CINDER_WB_ENGAGE.
        // Until the signed driver ships, this is a no-op that returns false so callers can
        // fall back to "hardware write-blocker required" UX guidance.
        IsActive = false;
        return false;
    }

    public bool TryDisengage()
    {
        IsActive = false;
        return true;
    }
}
