namespace Cinder.App.Services;

/// <summary>
/// Lets a tool hand a file to the shell ("open this hit in the Hex Viewer") without holding a
/// reference to the main window's view-model. The shell assigns the delegates at start-up;
/// they are null in tests and headless runs, so callers use <c>?.Invoke</c>.
/// </summary>
public static class AppNavigator
{
    /// <summary>Open <c>path</c> in a hex tab, switch to the Hex Viewer, and optionally jump to an offset.</summary>
    public static Action<string, long?>? OpenInHex { get; set; }
}
