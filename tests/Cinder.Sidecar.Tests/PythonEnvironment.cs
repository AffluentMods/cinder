using System.Diagnostics;
using Cinder.Core.Diagnostics;
using Xunit;

namespace Cinder.Sidecar.Tests;

/// <summary>
/// Finds the Python the worker tests run against, once per test run. The workers need
/// Python 3.12+ with <c>pydantic</c>; anything less and the tests that start a worker are
/// skipped with <see cref="SkipReason"/> rather than failed, because a missing interpreter says
/// nothing about the code under test.
/// </summary>
internal static class PythonEnvironment
{
    /// <summary>Overrides interpreter discovery with an explicit path.</summary>
    public const string OverrideVariable = "CINDER_TEST_PYTHON";

    /// <summary>The workers, copied next to the test assembly by the project file.</summary>
    public static string ParsersDir { get; } = Path.Combine(AppContext.BaseDirectory, "parsers");

    /// <summary>Workers that exist only for these tests.</summary>
    public static string TestWorkersDir { get; } = Path.Combine(AppContext.BaseDirectory, "Workers");

    public static string? Executable { get; }

    /// <summary>Null when worker tests can run; otherwise why they cannot.</summary>
    public static string? SkipReason { get; }

    static PythonEnvironment()
    {
        if (!File.Exists(Path.Combine(ParsersDir, "shared", "protocol.py")))
        {
            SkipReason = $"The Python workers were not copied to {ParsersDir}.";
            return;
        }

        var configured = Environment.GetEnvironmentVariable(OverrideVariable);
        var executable = !string.IsNullOrWhiteSpace(configured)
            ? ExecutableResolver.Resolve(configured)
            : ExecutableResolver.Resolve(OperatingSystem.IsWindows() ? "python.exe" : "python3")
              ?? ExecutableResolver.Resolve(OperatingSystem.IsWindows() ? "python3.exe" : "python");
        if (executable is null)
        {
            SkipReason = string.IsNullOrWhiteSpace(configured)
                ? $"No python executable was found on PATH. Install Python 3.12+ (or set {OverrideVariable}) to run the sidecar worker tests."
                : $"{OverrideVariable}='{configured}' does not point at an existing executable.";
            return;
        }

        var problem = Probe(executable);
        if (problem is not null)
        {
            SkipReason = $"Python at '{executable}' cannot run the sidecar workers: {problem}";
            return;
        }
        Executable = executable;
    }

    /// <summary>Checks version and dependencies by actually running the interpreter.</summary>
    private static string? Probe(string executable)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(
                "import sys\n" +
                "if sys.version_info < (3, 12): sys.exit('Python 3.12 or later is required, found ' + sys.version.split()[0])\n" +
                "try:\n    import pydantic\nexcept Exception as ex:\n    sys.exit('pydantic is not importable (pip install -r parsers/requirements.txt): ' + str(ex))\n");
            using var process = Process.Start(psi);
            if (process is null)
            {
                return "the process could not be started.";
            }
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(20_000))
            {
                process.Kill(entireProcessTree: true);
                return "it did not answer a version check within 20 seconds.";
            }
            if (process.ExitCode == 0)
            {
                return null;
            }
            var message = stderr.Wait(2_000) ? stderr.Result.Trim() : "";
            return message.Length > 0 ? message : $"the version check exited with code {process.ExitCode}.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return ex.Message;
        }
    }

    /// <summary>Start info for <c>python -m &lt;module&gt;</c> run from the parsers directory.</summary>
    public static ProcessStartInfo Module(string module) => Start("-m", module);

    /// <summary>Start info for a worker script given by path.</summary>
    public static ProcessStartInfo Script(string path) => Start(path);

    private static ProcessStartInfo Start(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Executable ?? throw new InvalidOperationException(SkipReason),
            WorkingDirectory = ParsersDir,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }
        // Scripts given by path do not get the working directory on sys.path.
        psi.Environment["PYTHONPATH"] = ParsersDir;
        // Never write __pycache__ into the test output.
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        return psi;
    }

    public static string NoisyWorker => Path.Combine(ParsersDir, "tests", "workers", "noisy_worker.py");
}

/// <summary>
/// A fact that starts a Python worker. Reported as skipped, with the reason, on a machine
/// without a usable Python.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (PythonEnvironment.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
