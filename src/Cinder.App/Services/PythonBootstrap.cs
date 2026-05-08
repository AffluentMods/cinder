using System.Diagnostics;
using System.Text;

namespace Cinder.App.Services;

/// <summary>
/// First-run Python venv bootstrapper. Creates a per-user venv under
/// <c>%LOCALAPPDATA%\Cinder\venv</c> (or <c>~/.local/share/Cinder/venv</c>) and pip-installs
/// the lockfile from <c>parsers/requirements.txt</c>. Once that venv exists, every sidecar
/// the C# layer spawns can point at <c>{venv}/Scripts/python.exe</c> (Windows) or
/// <c>{venv}/bin/python3</c> (Linux). The release installer ships an already-bootstrapped venv
/// so end users skip this step entirely.
/// </summary>
public sealed class PythonBootstrap
{
    public static string VenvDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cinder", "venv");

    public static string VenvPython =>
        OperatingSystem.IsWindows()
            ? Path.Combine(VenvDirectory, "Scripts", "python.exe")
            : Path.Combine(VenvDirectory, "bin", "python3");

    public static bool IsBootstrapped => File.Exists(VenvPython);

    /// <summary>Resolves the system Python executable for venv creation. Returns null if none.</summary>
    public static string? FindSystemPython()
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "py.exe", "python.exe", "python3.exe" }
            : new[] { "python3", "python" };
        foreach (var name in candidates)
        {
            var path = WhichOnPath(name);
            if (path is not null) return path;
        }
        return null;
    }

    private static string? WhichOnPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            try
            {
                var full = Path.Combine(dir, fileName);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Creates the venv (if missing) and installs <c>parsers/requirements.txt</c>. Streams
    /// progress lines via <paramref name="report"/>. Returns true on success.
    /// </summary>
    public async Task<bool> EnsureVenvAsync(string parsersDirectory, IProgress<string> report, CancellationToken ct = default)
    {
        var sysPython = FindSystemPython();
        if (sysPython is null)
        {
            report.Report("No system Python found. Install Python 3.10+ from python.org and re-run.");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(VenvDirectory)!);

        if (!IsBootstrapped)
        {
            report.Report($"Creating venv at {VenvDirectory} (using {sysPython})…");
            var ok = await RunAsync(sysPython, ["-m", "venv", VenvDirectory], report, ct).ConfigureAwait(false);
            if (!ok || !IsBootstrapped)
            {
                report.Report("venv creation failed.");
                return false;
            }
        }
        else
        {
            report.Report($"Re-using venv at {VenvDirectory}.");
        }

        // Always upgrade pip first.
        await RunAsync(VenvPython, ["-m", "pip", "install", "--upgrade", "pip"], report, ct).ConfigureAwait(false);

        var requirements = Path.Combine(parsersDirectory, "requirements.txt");
        if (!File.Exists(requirements))
        {
            report.Report($"requirements.txt not found at {requirements}. Skipping bulk install.");
            return false;
        }

        report.Report($"Installing forensic stack from {requirements} (this is a one-time ~150 MB download)…");
        var installed = await RunAsync(VenvPython,
            ["-m", "pip", "install", "-r", requirements],
            report, ct).ConfigureAwait(false);

        if (installed)
        {
            report.Report("✓ Bootstrap complete.");
        }
        return installed;
    }

    private static async Task<bool> RunAsync(string file, IReadOnlyList<string> args, IProgress<string> report, CancellationToken ct)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
            EnableRaisingEvents = true,
        };
        foreach (var a in args) p.StartInfo.ArgumentList.Add(a);
        p.OutputDataReceived += (_, e) => { if (e.Data is { } s) report.Report(s); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } s) report.Report(s); };
        if (!p.Start())
        {
            report.Report($"Failed to start {file}.");
            return false;
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode == 0;
    }
}
