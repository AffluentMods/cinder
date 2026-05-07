using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Cinder.Cases;
using Cinder.Core.Custody;
using Markdig;

namespace Cinder.Reader;

/// <summary>
/// CinderReader — read-only viewer for shared encrypted bundles. Free, separately distributed,
/// no parsing engines bundled. Verifies the chain-of-custody log on open and surfaces a big
/// red banner if it's broken.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ReaderApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

public partial class ReaderApp : Avalonia.Application
{
    public override void Initialize() { }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new ReaderWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public partial class ReaderWindow : Window
{
    public ReaderWindow()
    {
        Title = "Cinder Reader";
        Width = 980;
        Height = 720;
        Background = Brushes.Black;
        Foreground = Brushes.White;
        Content = new TextBlock
        {
            Text = "CinderReader · Open a .cinderbundle to view a sealed case (read-only).",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FontSize = 16,
            Foreground = Brushes.Gainsboro,
        };
        // Full open/verify/render flow lives in CinderReader.ReaderViewModel — wired in 8.1.
        // For Phase 8 the executable demonstrates the binary surface and dependency tree;
        // the report-rendering UI reuses Cinder.Reports rendering so there's no logic dup.
        _ = typeof(EncryptedBundle);
        _ = typeof(CustodyLog);
        _ = typeof(Markdown);
    }
}
