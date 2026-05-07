using System.Globalization;
using System.IO;
using System.Text;
using Serilog;
using Serilog.Events;

namespace Cinder.App.Services;

/// <summary>
/// Local-only crash handler. On unhandled exception, dumps a timestamped <c>.crash.txt</c>
/// bundle next to the per-user log directory. **Never** uploads anything (Cinder is zero-telemetry).
/// </summary>
public static class CrashHandler
{
    private static readonly object Sync = new();
    private static bool _installed;
    private static string? _crashDir;

    public static string? LastCrashDirectory => _crashDir;

    public static void Install()
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
        }

        var logDir = ResolveLogDirectory();
        Directory.CreateDirectory(logDir);
        _crashDir = logDir;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("App", "Cinder")
            .WriteTo.Async(a => a.File(
                Path.Combine(logDir, "cinder-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
                restrictedToMinimumLevel: LogEventLevel.Information))
            .WriteTo.Console()
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Capture(ex, "appdomain-unhandled");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Capture(e.Exception, "task-unobserved");
            e.SetObserved();
        };
    }

    public static void Capture(Exception ex, string source)
    {
        try
        {
            Log.Error(ex, "Captured unhandled exception from {Source}", source);

            var dir = _crashDir ?? ResolveLogDirectory();
            Directory.CreateDirectory(dir);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            var bundle = Path.Combine(dir, $"crash-{stamp}.txt");

            var sb = new StringBuilder()
                .Append("Cinder crash bundle ").AppendLine(stamp)
                .Append("Source: ").AppendLine(source)
                .Append("Process: ").AppendLine(Environment.ProcessPath ?? "(unknown)")
                .Append("OS: ").AppendLine(Environment.OSVersion.VersionString)
                .Append("Runtime: ").AppendLine(Environment.Version.ToString())
                .Append("Working dir: ").AppendLine(Environment.CurrentDirectory)
                .AppendLine()
                .AppendLine("Exception:")
                .AppendLine(ex.ToString());

            File.WriteAllText(bundle, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Crash handler must never throw.
        }
    }

    private static string ResolveLogDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        }
        return Path.Combine(baseDir, "Cinder", "logs");
    }
}
