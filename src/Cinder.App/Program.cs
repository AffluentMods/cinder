using Avalonia;
using Avalonia.ReactiveUI;
using Cinder.App.Services;
using Serilog;

namespace Cinder.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        CrashHandler.Install();
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Reached for a start-up failure and for an exception the UI-thread policy in
            // CrashHandler judged unsafe to continue past. Either way: record it, say so, exit.
            var bundle = CrashHandler.Capture(ex, "fatal");
            Log.Fatal(ex, "Cinder stopped on an unhandled exception.");
            CrashHandler.ReportFatal(ex, bundle, interactive: !args.Contains("--shot"));
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
