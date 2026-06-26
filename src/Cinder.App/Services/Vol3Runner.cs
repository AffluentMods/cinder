// =====================================================================================
// Vol3Runner — minimal wrapper around the Volatility 3 CLI.
//
// We shell out to `python -m volatility3 -f <image> -r json <plugin>` and parse the
// JSON renderer output. Each plugin returns a flat array of objects; we route those
// directly into the MemoryTool's Rows collection.
//
// Why shell out vs. embed Python:
//   - vol3 is heavy (pulls regipy + capstone + yara-python + pefile + sqlalchemy +
//     a dozen others). Embedding it via Python.NET pulls all of that into Cinder.exe.
//   - Shell-out lets the user install their own Volatility (homebrew / apt / pip /
//     standalone PyInstaller binary) and Cinder stays small.
//   - The JSON renderer was added in vol3 2.0 — every supported plugin emits stable JSON.
//
// Volatility 3 is detected by trying `python -m volatility3 --help`; missing python
// or missing module surfaces a clear actionable error.
// =====================================================================================

using System.Diagnostics;
using System.Text.Json;

namespace Cinder.App.Services;

public static class Vol3Runner
{
    /// <summary>The plugins we expose by default. Each corresponds to one canonical
    /// forensics question — most cases need them all.</summary>
    public static readonly IReadOnlyList<Vol3Plugin> DefaultPlugins =
    [
        new("windows.pstree.PsTree",   "Process tree",          "PID, PPID, ImageFileName, CreateTime, ExitTime"),
        new("windows.psscan.PsScan",   "Hidden / exited procs", "Scans for EPROCESS structures (catches rootkits)"),
        new("windows.netscan.NetScan", "Network sockets",       "Local/foreign endpoints + owning PID"),
        new("windows.dlllist.DllList", "Loaded DLLs",           "Per-process module list"),
        new("windows.malfind.Malfind", "Injected code",         "Pages with RWX + no backing file"),
        new("windows.hashdump.Hashdump","SAM hash dump",         "Local user NTLM hashes"),
        new("windows.lsadump.Lsadump", "LSA secrets",           "Cached credentials, machine secret"),
    ];

    public sealed record Vol3Plugin(string Name, string Title, string Description);

    public sealed record Vol3Result(
        string Plugin,
        IReadOnlyList<IDictionary<string, object?>> Rows,
        string? Error);

    /// <summary>Run a single plugin against an image. Returns one row dict per record.</summary>
    public static async Task<Vol3Result> RunAsync(
        string memoryImagePath,
        string plugin,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolvePython(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("volatility3");
        psi.ArgumentList.Add("-r"); psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(memoryImagePath);
        psi.ArgumentList.Add(plugin);

        using var p = Process.Start(psi);
        if (p is null)
        {
            return new Vol3Result(plugin, [], "Failed to spawn python.");
        }

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (p.ExitCode != 0)
        {
            return new Vol3Result(plugin, [], $"Volatility exited {p.ExitCode}: {stderr.Trim()}");
        }
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return new Vol3Result(plugin, [], "Volatility produced no output. Try --offline if you don't have network.");
        }

        try
        {
            var rows = new List<IDictionary<string, object?>>();
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    rows.Add(JsonToDict(el));
                }
            }
            return new Vol3Result(plugin, rows, null);
        }
        catch (Exception ex)
        {
            return new Vol3Result(plugin, [], $"Could not parse Volatility JSON: {ex.Message}");
        }
    }

    /// <summary>Returns true if Volatility 3 is callable. Cached for the process lifetime.</summary>
    private static bool? _availability;
    public static async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (_availability is { } v) return v;
        var psi = new ProcessStartInfo
        {
            FileName = ResolvePython(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("volatility3");
        psi.ArgumentList.Add("--help");
        try
        {
            using var p = Process.Start(psi);
            if (p is null) { _availability = false; return false; }
            await p.WaitForExitAsync(ct);
            _availability = p.ExitCode == 0;
            return _availability.Value;
        }
        catch
        {
            _availability = false;
            return false;
        }
    }

    private static string ResolvePython()
    {
        // PythonBootstrap puts the per-user venv here; fall back to PATH otherwise.
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cinder", "venv", "Scripts", "python.exe");
        if (File.Exists(local)) return local;
        return OperatingSystem.IsWindows() ? "python.exe" : "python3";
    }

    private static IDictionary<string, object?> JsonToDict(JsonElement el)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (el.ValueKind != JsonValueKind.Object) return d;
        foreach (var prop in el.EnumerateObject())
        {
            d[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object?)l : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText(),
            };
        }
        return d;
    }
}
