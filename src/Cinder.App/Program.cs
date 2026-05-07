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
            CrashHandler.Capture(ex, "fatal-startup");
            Log.Fatal(ex, "Cinder failed to start.");
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
