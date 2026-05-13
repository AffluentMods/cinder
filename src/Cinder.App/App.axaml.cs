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

            // Hydrate persisted "recent cases" + "recent evidence" so the Home dashboard
            // shows the user's last few sessions across app restarts.
            vm.Workspace.Dashboard.AttachStore(new RecentsStore());

            desktop.MainWindow = new MainWindow { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
