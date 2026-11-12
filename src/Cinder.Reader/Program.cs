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
/// CinderReader — the shell of a read-only viewer for shared encrypted bundles. Free, separately
/// distributed, no parsing engines bundled.
///
/// <para><b>Not implemented yet:</b> this build opens a window and nothing else. It does not
/// open a bundle, and it does not verify the chain-of-custody log — do not treat having opened
/// a case in CinderReader as a custody check. Until the open/verify flow lands, use
/// <c>cinder custody verify &lt;case-path&gt;</c>.</para>
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
            Text = "CinderReader · Viewer not implemented in this build. It does not open bundles or verify custody.",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FontSize = 16,
            Foreground = Brushes.Gainsboro,
        };
        // The open/verify/render flow is planned for 8.1 and does not exist yet. For Phase 8
        // the executable only demonstrates the binary surface and dependency tree.
        _ = typeof(EncryptedBundle);
        _ = typeof(CustodyLog);
        _ = typeof(Markdown);
    }
}
