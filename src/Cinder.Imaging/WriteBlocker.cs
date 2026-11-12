using System.Diagnostics;
using System.Runtime.Versioning;
using Cinder.Native;
using Cinder.Core.Diagnostics;

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
/// Linux software write-blocker. Runs <c>blockdev --setro &lt;dev&gt;</c> on every disk
/// <c>lsblk -dno NAME,TYPE</c> lists, then reads the flag back with <c>blockdev --getro</c>.
/// Doesn't block userland writes through the VFS — that's the <c>dm-readonly</c> device-mapper
/// wrap, which Phase 2.1 will add. For Phase 2 this is enough to freeze sd*/nvme* writes from
/// the kernel down.
///
/// <para>A device counts as protected only when <c>blockdev</c> exited 0 <em>and</em> the flag
/// reads back as set. <see cref="TryEngage"/> returns true, and <see cref="IsActive"/> is true,
/// only when that holds for every device; the ones it does not hold for are listed in
/// <see cref="FailedDevices"/>. A blocker that says it is engaged while a disk is still
/// writable is worse than none.</para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxBlockdevWriteBlocker : IWriteBlocker
{
    /// <summary>Runs a command and returns its exit code and standard output.</summary>
    internal delegate (int ExitCode, string Output) CommandRunner(string file, IReadOnlyList<string> args);

    private readonly CommandRunner _run;
    private readonly HashSet<string> _frozen = new();
    private readonly List<string> _failed = new();

    public LinuxBlockdevWriteBlocker()
        : this(RunProcess)
    {
    }

    /// <summary>Test seam: the commands go to <paramref name="run"/> instead of real processes.</summary>
    internal LinuxBlockdevWriteBlocker(CommandRunner run)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public bool IsActive => _frozen.Count > 0 && _failed.Count == 0;

    /// <summary>Devices the last <see cref="TryEngage"/> could not set, or could not confirm, read-only.</summary>
    public IReadOnlyList<string> FailedDevices => _failed;

    /// <summary>Devices confirmed read-only.</summary>
    public IReadOnlyCollection<string> ProtectedDevices => _frozen;

    public bool TryEngage()
    {
        _failed.Clear();
        List<string> devs;
        try
        {
            devs = ListBlockDevices();
        }
        catch
        {
            return false;
        }

        foreach (var d in devs)
        {
            // SECURITY: `d` comes from lsblk output. We allow-list valid device-name
            // characters before splicing it into a device path — defense in depth even
            // though blockdev runs as a separate Process invocation with ArgumentList.
            // A name that fails the check is a device left unprotected, so it is a failure.
            if (IsSafeDeviceName(d) && SetAndConfirmReadOnly(d))
            {
                _frozen.Add(d);
            }
            else
            {
                _frozen.Remove(d);
                _failed.Add(d);
            }
        }
        return IsActive;
    }

    private bool SetAndConfirmReadOnly(string device)
    {
        try
        {
            var path = "/dev/" + device;
            if (_run("blockdev", ["--setro", path]).ExitCode != 0)
            {
                return false;
            }
            var check = _run("blockdev", ["--getro", path]);
            return check.ExitCode == 0 && check.Output.Trim() == "1";
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
                if (_run("blockdev", ["--setrw", "/dev/" + d]).ExitCode == 0)
                {
                    _frozen.Remove(d);
                }
                else
                {
                    ok = false;
                }
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

    /// <summary>
    /// Whole devices from <c>lsblk</c>, without optical drives: <c>rom</c> devices are not
    /// acquisition targets, and <c>blockdev</c> cannot open one that has no disc in it.
    /// </summary>
    private List<string> ListBlockDevices()
    {
        var (exit, raw) = _run("lsblk", ["-dno", "NAME,TYPE"]);
        if (exit != 0)
        {
            throw new InvalidOperationException($"lsblk: exit {exit}");
        }
        var names = new List<string>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || (parts.Length > 1 && parts[1] == "rom"))
            {
                continue;
            }
            names.Add(parts[0]);
        }
        return names;
    }

    private static (int ExitCode, string Output) RunProcess(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(ExecutableResolver.ResolveRequired(file))
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{file}: could not be started");
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
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
