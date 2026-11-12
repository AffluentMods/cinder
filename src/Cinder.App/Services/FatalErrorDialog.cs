using System.Diagnostics;
using Cinder.Core.Diagnostics;

namespace Cinder.App.Services;

/// <summary>
/// Shows the "Cinder has stopped" message on Linux and macOS, where there is no system call
/// for a message box.
///
/// <para>When this runs, the application's own UI cannot be trusted: the exception that is
/// ending the process came out of it. So the message is shown by a separate, ordinary desktop
/// program — <c>osascript</c> on macOS; <c>zenity</c>, <c>kdialog</c> or <c>xmessage</c> on
/// Linux, whichever is installed — started as its own process. It needs nothing from Cinder
/// beyond its command line and stays on screen after Cinder has exited.</para>
///
/// <para>The text is always passed as an argument, never spliced into a script or a shell
/// command, because it contains an exception message and a file path.</para>
/// </summary>
internal static class FatalErrorDialog
{
    /// <summary>One way of showing the message: a program and its arguments.</summary>
    internal sealed record Command(string Program, IReadOnlyList<string> Arguments);

    internal enum Desktop
    {
        MacOS,
        Linux,
    }

    /// <summary>
    /// How long a helper gets to fail. One that cannot reach a display exits non-zero almost
    /// at once; one that is still running after this is on screen.
    /// </summary>
    private static readonly TimeSpan FailFast = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Shows the message with the first helper that works. Returns false when none did (no
    /// helper installed, no display, or every attempt failed) — the caller has already written
    /// the same text to standard error, which is then all there is.
    /// </summary>
    public static bool TryShow(string title, string text)
    {
        if (OperatingSystem.IsMacOS())
        {
            return TryShow(CommandsFor(Desktop.MacOS, title, text), Run);
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            // No display server, no dialog: a helper would only fail more slowly.
            var hasDisplay = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                          || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            return hasDisplay && TryShow(CommandsFor(Desktop.Linux, title, text), Run);
        }
        return false;
    }

    /// <summary>Tries each command in order until <paramref name="run"/> reports success. A runner that throws counts as a failure.</summary>
    internal static bool TryShow(IReadOnlyList<Command> commands, Func<Command, bool> run)
    {
        foreach (var command in commands)
        {
            try
            {
                if (run(command))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // This is the last thing the process does; one helper failing in an unexpected
                // way must not stop the next from being tried.
            }
        }
        return false;
    }

    /// <summary>The helpers to try on <paramref name="desktop"/>, most capable first.</summary>
    internal static IReadOnlyList<Command> CommandsFor(Desktop desktop, string title, string text) => desktop switch
    {
        Desktop.MacOS =>
        [
            // The script is fixed; title and text arrive as `argv`, so nothing in them is
            // ever parsed as AppleScript.
            new Command("/usr/bin/osascript",
            [
                "-e", "on run argv",
                "-e", "display dialog (item 2 of argv) with title (item 1 of argv) buttons {\"OK\"} default button \"OK\" with icon stop",
                "-e", "end run",
                title,
                text,
            ]),
        ],
        Desktop.Linux =>
        [
            // --no-markup: the text is shown as written, not interpreted as Pango markup.
            new Command("zenity", ["--error", "--no-markup", "--width=560", "--title=" + title, "--text=" + text]),
            new Command("kdialog", ["--title", title, "--error", text]),
            new Command("xmessage", ["-center", "-title", title, text]),
            new Command("gxmessage", ["-center", "-title", title, text]),
        ],
        _ => [],
    };

    /// <summary>Starts the helper as its own process. True when it is showing (still running) or was shown and dismissed (exit 0).</summary>
    private static bool Run(Command command)
    {
        // An absolute path, resolved without looking in Cinder's own directory.
        var program = Path.IsPathRooted(command.Program)
            ? (File.Exists(command.Program) ? command.Program : null)
            : ExecutableResolver.Resolve(command.Program);
        if (program is null)
        {
            return false;
        }

        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return false;
        }
        return !process.WaitForExit(FailFast) || process.ExitCode == 0;
    }
}
