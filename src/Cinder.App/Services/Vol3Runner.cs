// =====================================================================================
// Vol3Runner — minimal wrapper around the Volatility 3 command line.
//
// Volatility is started through its console entry point (volatility3.cli.main) with the JSON
// renderer, and the output is parsed here. Tree plugins such as pstree nest each record's
// children under "__children"; ParseRows flattens that into a list with a depth per row, so
// child processes are not lost.
//
// Why shell out rather than embed Python:
//   - Volatility is heavy (regipy, capstone, yara-python, pefile and more). Embedding it
//     would pull all of that into Cinder.
//   - Shelling out lets the examiner install their own Volatility and keeps Cinder small.
// =====================================================================================

using System.Diagnostics;
using System.Text.Json;
using Cinder.Core.Diagnostics;

namespace Cinder.App.Services;

public static class Vol3Runner
{
    /// <summary>Key added to every row by <see cref="ParseRows"/>: 0 for a root record, 1 for its children, and so on.</summary>
    public const string DepthKey = "__depth";

    private const string ChildrenKey = "__children";

    /// <summary>
    /// Runs Volatility's own entry point. <c>python -m volatility3</c> is not used because the
    /// package is not guaranteed to be runnable as a module; the console entry point is.
    /// </summary>
    private const string Launcher = "import sys; from volatility3.cli import main; sys.exit(main())";

    public sealed record Vol3Result(
        string Plugin,
        IReadOnlyList<IDictionary<string, object?>> Rows,
        string? Error);

    /// <summary>
    /// Runs one plugin against an image. Returns one flattened row per record, or
    /// <see cref="Vol3Result.Error"/> when Volatility failed. Cancelling kills Volatility and
    /// everything it started; a memory scan left running would hold the image open for minutes.
    /// </summary>
    public static async Task<Vol3Result> RunAsync(
        string memoryImagePath,
        string plugin,
        CancellationToken ct)
    {
        var psi = NewPython();
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(Launcher);
        psi.ArgumentList.Add("-r"); psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(memoryImagePath);
        psi.ArgumentList.Add(plugin);

        using var p = Process.Start(psi);
        if (p is null)
        {
            return new Vol3Result(plugin, [], "Python could not be started.");
        }

        var (exitCode, stdout, stderr) = await WaitAndDrainAsync(p, ct);
        if (exitCode != 0)
        {
            return new Vol3Result(plugin, [], $"Volatility stopped with exit code {exitCode}. {LastLines(stderr)}".TrimEnd());
        }
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return new Vol3Result(plugin, [], $"Volatility produced no output. {LastLines(stderr)}".TrimEnd());
        }

        try
        {
            return new Vol3Result(plugin, ParseRows(stdout), null);
        }
        catch (JsonException ex)
        {
            return new Vol3Result(plugin, [], $"Volatility's output could not be read as JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Turns Volatility's JSON renderer output into a flat list. Each record's nested
    /// <c>__children</c> follow it in order, and every row carries <see cref="DepthKey"/> so the
    /// tree can still be shown. Without this only the root records survive, which for pstree
    /// means every process started by another process is missing.
    /// </summary>
    internal static List<IDictionary<string, object?>> ParseRows(string json)
    {
        var rows = new List<IDictionary<string, object?>>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                Flatten(el, 0, rows);
            }
        }
        return rows;
    }

    private static void Flatten(JsonElement el, int depth, List<IDictionary<string, object?>> rows)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [DepthKey] = (long)depth };
        JsonElement? children = null;
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.NameEquals(ChildrenKey))
            {
                children = prop.Value;
                continue;
            }
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
        rows.Add(d);
        if (children is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var child in list.EnumerateArray())
            {
                Flatten(child, depth + 1, rows);
            }
        }
    }

    private static bool? _availability;

    /// <summary>
    /// True when Volatility 3 can be imported by the Python Cinder would use. A definite answer
    /// is cached for the life of the process; a probe that was cancelled or could not be started
    /// is not an answer and is not cached, so stopping a run can never turn into "Volatility is
    /// not installed" for the rest of the session.
    /// </summary>
    public static async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (_availability is { } known)
        {
            return known;
        }

        ProcessStartInfo psi;
        try
        {
            psi = NewPython();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false; // no Python at all; may be installed later, so not cached
        }
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("import volatility3.cli");

        using var p = Process.Start(psi);
        if (p is null)
        {
            return false;
        }
        // Both pipes are drained. A child whose output nobody reads blocks as soon as the pipe
        // buffer fills, and the wait below would then never return.
        var (exitCode, _, _) = await WaitAndDrainAsync(p, ct);
        _availability = exitCode == 0;
        return exitCode == 0;
    }

    /// <summary>Reads both output pipes to the end while waiting for exit. On cancellation the whole process tree is killed.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> WaitAndDrainAsync(Process p, CancellationToken ct)
    {
        var stdoutTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Already gone, or not ours to kill.
            }
            throw;
        }
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>The tail of a traceback or error stream: the lines that name the actual problem.</summary>
    internal static string LastLines(string? text, int count = 3)
    {
        var lines = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines.Skip(Math.Max(0, lines.Length - count)));
    }

    private static ProcessStartInfo NewPython() => new()
    {
        FileName = ResolvePython(),
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private static string ResolvePython()
    {
        // A per-user virtual environment, if one exists, lives here; fall back to PATH otherwise.
        var venv = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cinder", "venv");
        var local = OperatingSystem.IsWindows()
            ? Path.Combine(venv, "Scripts", "python.exe")
            : Path.Combine(venv, "bin", "python");
        if (File.Exists(local)) return local;

        // Resolved to an absolute path rather than handed to Process.Start as a bare name:
        // Windows would otherwise search Cinder's own directory first, so a python.exe sitting
        // beside the portable executable would run in preference to the real interpreter.
        return ExecutableResolver.ResolveRequired(
            OperatingSystem.IsWindows() ? "python.exe" : "python3",
            "Install Python 3.12+ and make sure it is on PATH.");
    }
}
