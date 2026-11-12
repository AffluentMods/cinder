namespace Cinder.App.Services;

/// <summary>
/// The one way for code without a reference to the shell (services, dialog view-models, the
/// crash handler) to tell the examiner that something went wrong. The shell assigns
/// <see cref="ErrorSink"/> at start-up; it is null in tests and headless runs, where a notice
/// is simply dropped. The sink must be callable from any thread.
/// </summary>
public static class ShellNotice
{
    public static Action<string>? ErrorSink { get; set; }

    /// <summary>Shows <paramref name="message"/> to the examiner. Never throws.</summary>
    public static void Error(string message)
    {
        try
        {
            ErrorSink?.Invoke(message);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not show a notice: {Notice}", message);
        }
    }
}
