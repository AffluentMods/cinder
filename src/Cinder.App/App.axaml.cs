using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Cinder.App.Services;
using Cinder.App.ViewModels;
using Cinder.App.Views;

namespace Cinder.App;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var registry = new CommandRegistry();
            var vm = new MainWindowViewModel(registry);
            CommandRegistration.RegisterBuiltIns(registry, vm);
            CommandRegistration.RegisterNavigation(registry, vm);

            // Hydrate persisted "recent cases" + "recent evidence" so the Home dashboard
            // shows the user's last few sessions across app restarts.
            // The dev screenshot harness must never touch the examiner's real recents.
            var args = desktop.Args ?? [];
            vm.Workspace.Dashboard.AttachStore(args.Contains("--shot")
                ? new RecentsStore(Path.Combine(Path.GetTempPath(), "cinder-shot-recents.json"))
                : new RecentsStore());

            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;

            // Dev-only: `--shot out.png` renders the window to a PNG and exits (visual review).
            DevScreenshot.TryAttach(args, window, vm);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
