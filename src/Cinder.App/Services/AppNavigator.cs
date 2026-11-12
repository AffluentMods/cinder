namespace Cinder.App.Services;

/// <summary>
/// Lets a tool ask the shell to do something ("open this hit in the Hex Viewer", "open this
/// case") without holding a reference to the main window's view-model. The shell assigns the
/// delegates at start-up; they are null in tests and headless runs, so callers null-check.
/// </summary>
public static class AppNavigator
{
    /// <summary>Open <c>path</c> in a hex tab, switch to the Hex Viewer, and optionally jump to an offset.</summary>
    public static Action<string, long?>? OpenInHex { get; set; }

    /// <summary>Run the shell's "Create case…" flow (save dialog, create, activate).</summary>
    public static Func<Task>? CreateCase { get; set; }

    /// <summary>Run the shell's "Open case…" flow (file picker, open, activate).</summary>
    public static Func<Task>? OpenCase { get; set; }

    /// <summary>Open and activate the case file at a known path.</summary>
    public static Func<string, Task>? OpenCaseAtPath { get; set; }

    /// <summary>Close the case that is currently active.</summary>
    public static Action? CloseActiveCase { get; set; }
}
