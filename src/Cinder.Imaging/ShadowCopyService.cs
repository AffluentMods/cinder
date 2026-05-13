using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Cinder.Native;

namespace Cinder.Imaging;

/// <summary>
/// Cross-platform shadow-copy / snapshot enumeration. Delegates to <see cref="VssEnumerator"/> on
/// Windows and <see cref="LinuxSnapshotEnumerator"/> on Linux (btrfs / LVM / ZFS).
/// </summary>
public static class ShadowCopyService
{
    public static IShadowCopyEnumerator ForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return new VssEnumerator();
        }
        if (OperatingSystem.IsLinux())
        {
            return new LinuxSnapshotEnumerator();
        }
        return new NullShadowCopyEnumerator();
    }

    /// <summary>Fallback for platforms with no snapshot story yet (e.g. macOS).</summary>
    private sealed class NullShadowCopyEnumerator : IShadowCopyEnumerator
    {
        public IReadOnlyList<ShadowCopy> Enumerate() => [];
    }
}

[SupportedOSPlatform("windows")]
public sealed partial class VssEnumerator : IShadowCopyEnumerator
{
    public IReadOnlyList<ShadowCopy> Enumerate()
    {
        // `vssadmin list shadows` requires Administrator. We tolerate the failure and return [].
        // Arguments fixed at compile time — no user input feeds vssadmin.
        try
        {
            var psi = new ProcessStartInfo("vssadmin.exe")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("list");
            psi.ArgumentList.Add("shadows");
            using var p = new Process { StartInfo = psi };
            p.Start();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return Parse(output);
        }
        catch
        {
            return [];
        }
    }

    [GeneratedRegex(@"Shadow Copy ID: (\{[^\}]+\}).*?Original Volume: \(([^)]+)\).*?Creation Time: ([^\r\n]+)", RegexOptions.Singleline)]
    private static partial Regex ShadowRegex();

    internal static IReadOnlyList<ShadowCopy> Parse(string vssadminOutput)
    {
        var list = new List<ShadowCopy>();
        foreach (Match m in ShadowRegex().Matches(vssadminOutput))
        {
            var id = m.Groups[1].Value;
            var origin = m.Groups[2].Value;
            DateTimeOffset created = DateTimeOffset.UtcNow;
            DateTimeOffset.TryParse(m.Groups[3].Value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out created);
            list.Add(new ShadowCopy(id, origin, created.ToUniversalTime(), null));
        }
        return list;
    }
}

[SupportedOSPlatform("linux")]
public sealed class LinuxSnapshotEnumerator : IShadowCopyEnumerator
{
    public IReadOnlyList<ShadowCopy> Enumerate()
    {
        var hits = new List<ShadowCopy>();
        TryAdd(hits, EnumerateBtrfs);
        TryAdd(hits, EnumerateLvm);
        TryAdd(hits, EnumerateZfs);
        return hits;
    }

    private static void TryAdd(List<ShadowCopy> hits, Func<IEnumerable<ShadowCopy>> source)
    {
        try { hits.AddRange(source()); } catch { /* tool not installed; skip */ }
    }

    private static IEnumerable<ShadowCopy> EnumerateBtrfs()
    {
        var raw = RunCapture("btrfs", ["subvolume", "list", "-s", "/"]);
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return new ShadowCopy(
                Id: line.Trim(),
                Origin: "btrfs:/",
                CreatedUtc: DateTimeOffset.UtcNow,
                Notes: null);
        }
    }

    private static IEnumerable<ShadowCopy> EnumerateLvm()
    {
        // SECURITY: each argument is a separate ArgumentList entry. The `2>/dev/null` form
        // is a shell construct; under UseShellExecute=false it would be passed as a literal
        // arg. We just drop the redirect — stderr from this service is read separately
        // and the tool's "not installed" failure is caught by TryAdd.
        var raw = RunCapture("lvs", ["--noheadings", "-o", "lv_name,origin,lv_attr,time", "--units", "b"]);
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[2].StartsWith('s'))
            {
                continue; // not a snapshot
            }
            yield return new ShadowCopy(parts[0], $"lvm:{parts[1]}", DateTimeOffset.UtcNow, null);
        }
    }

    private static IEnumerable<ShadowCopy> EnumerateZfs()
    {
        var raw = RunCapture("zfs", ["list", "-t", "snapshot", "-H", "-o", "name,creation"]);
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            yield return new ShadowCopy(parts[0], "zfs", DateTimeOffset.UtcNow, parts.Length > 1 ? parts[1] : null);
        }
    }

    private static string RunCapture(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file)
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
        using var p = new Process { StartInfo = psi };
        p.Start();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5_000);
        return output;
    }
}
