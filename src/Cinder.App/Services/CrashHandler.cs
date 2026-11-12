using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using Serilog;
using Serilog.Events;

namespace Cinder.App.Services;

/// <summary>
/// Local-only crash handler. On unhandled exception, dumps a timestamped <c>crash-*.txt</c>
/// bundle next to the per-user log directory. **Never** uploads anything (Cinder is zero-telemetry).
///
/// <para>An exception that escapes onto the UI thread is handled by policy, not by reflex
/// (see <see cref="IsRecoverable"/>): a failed operation — a file that could not be read, a
/// pattern that timed out — is logged, shown to the examiner and survived, because nothing the
/// app holds was damaged by it. Anything else is a defect of unknown reach; that is logged and
/// shown too, and then the process is allowed to end rather than carry on over state nobody can
/// vouch for. Neither kind ends the session silently.</para>
/// </summary>
public static class CrashHandler
{
    private static readonly object Sync = new();
    private static bool _installed;
    private static bool _dispatcherAttached;
    private static string? _crashDir;
    private static Exception? _lastCaptured;
    private static string? _lastBundle;
    private static int _sequence;

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

    /// <summary>
    /// Routes exceptions that reach the UI thread's dispatcher through this handler. Call once,
    /// on the UI thread, after the framework is initialised.
    /// </summary>
    public static void AttachToDispatcher()
    {
        lock (Sync)
        {
            if (_dispatcherAttached)
            {
                return;
            }
            _dispatcherAttached = true;
        }
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var bundle = Capture(e.Exception, "ui-thread");
        if (!IsRecoverable(e.Exception))
        {
            // Left unhandled on purpose: it propagates out of the main loop to Program.Main,
            // which reports it to the examiner before the process exits.
            return;
        }

        e.Handled = true;
        var where = bundle is null ? "the log" : Path.GetFileName(bundle);
        ShellNotice.Error($"That did not work: {Describe(e.Exception)} Cinder is still running; details are in {where}.");
    }

    /// <summary>
    /// True when <paramref name="ex"/> is the failure of an operation rather than evidence of a
    /// defect: I/O and permissions, cancellation and timeouts, a child process or database or
    /// network call failing, malformed input, something already closed. After one of these the
    /// operation is over and the rest of the app is as it was, so it is safe to keep running.
    ///
    /// <para>Everything else — null references, invalid casts, broken invariants, wrong-thread
    /// access, exhausted memory — says the program is somewhere its author did not expect. The
    /// session is not continued on top of that.</para>
    /// </summary>
    internal static bool IsRecoverable(Exception? ex) => ex switch
    {
        null => false,
        AggregateException aggregate =>
            aggregate.InnerExceptions.Count > 0 && aggregate.Flatten().InnerExceptions.All(IsRecoverable),
        OperationCanceledException => true,
        IOException => true,
        InvalidDataException => true,          // malformed input; not an IOException in .NET
        UnauthorizedAccessException => true,
        System.Security.SecurityException => true,
        TimeoutException => true,
        ObjectDisposedException => true,
        PlatformNotSupportedException => true,
        FormatException => true,
        System.ComponentModel.Win32Exception => true,
        System.Data.Common.DbException => true,
        System.Net.Http.HttpRequestException => true,
        System.Security.Cryptography.CryptographicException => true,
        System.Text.Json.JsonException => true,
        System.Xml.XmlException => true,
        _ => false,
    };

    /// <summary>One sentence for the status bar: the innermost message, ending in a full stop.</summary>
    internal static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner is AggregateException { InnerExceptions.Count: > 0 } aggregate)
        {
            inner = aggregate.InnerExceptions[0];
        }
        var message = string.IsNullOrWhiteSpace(inner.Message) ? inner.GetType().Name : inner.Message.Trim();
        return message.EndsWith('.') || message.EndsWith('!') || message.EndsWith('?') ? message : message + ".";
    }

    /// <summary>
    /// Writes the crash bundle and returns its path (null if even that failed). Capturing the
    /// same exception object twice — once on the dispatcher, once as it leaves <c>Main</c> —
    /// writes one bundle, not two.
    /// </summary>
    public static string? Capture(Exception ex, string source)
    {
        try
        {
            lock (Sync)
            {
                if (ReferenceEquals(ex, _lastCaptured))
                {
                    return _lastBundle;
                }
            }

            Log.Error(ex, "Captured unhandled exception from {Source}", source);

            var dir = _crashDir ?? ResolveLogDirectory();
            Directory.CreateDirectory(dir);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            // The sequence keeps two failures in the same second from overwriting each other.
            var sequence = Interlocked.Increment(ref _sequence);
            var bundle = Path.Combine(dir, $"crash-{stamp}-{sequence.ToString(CultureInfo.InvariantCulture)}.txt");

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
            lock (Sync)
            {
                _lastCaptured = ex;
                _lastBundle = bundle;
            }
            return bundle;
        }
        catch
        {
            // Crash handler must never throw.
            return null;
        }
    }

    /// <summary>
    /// Last words for an exception that is ending the process: a message box on Windows, standard
    /// error everywhere. <paramref name="interactive"/> is false for unattended runs (the
    /// screenshot harness), which must never block on a dialog.
    /// </summary>
    public static void ReportFatal(Exception ex, string? bundle, bool interactive)
    {
        try
        {
            var text =
                "Cinder has stopped because of an unexpected error and cannot safely continue." + Environment.NewLine +
                Environment.NewLine +
                Describe(ex) + Environment.NewLine +
                Environment.NewLine +
                (bundle is null
                    ? "The details could not be written to disk."
                    : "The details were saved to:" + Environment.NewLine + bundle) + Environment.NewLine +
                Environment.NewLine +
                "Case files and custody logs are written as you work and are not affected. Nothing has been sent anywhere.";

            Console.Error.WriteLine(text);
            if (interactive && OperatingSystem.IsWindows() && Environment.UserInteractive)
            {
                const uint IconError = 0x10;
                const uint TaskModal = 0x2000;
                _ = MessageBoxW(IntPtr.Zero, text, "Cinder", IconError | TaskModal);
            }
        }
        catch
        {
            // Reporting is best effort; the bundle and the log already hold the facts.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

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
