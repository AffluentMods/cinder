using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Cinder.App.Services;

/// <summary>Built-in command palette actions registered at startup.</summary>
public static class CommandRegistration
{
    public static void RegisterBuiltIns(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(new CommandDescriptor(
            Id: "view.theme.toggle",
            Title: "Toggle Theme (Dark / Light)",
            Subtitle: "Switch between Cinder's dark and light variants.",
            Category: "View",
            Invoke: _ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var app = Avalonia.Application.Current;
                    if (app is null)
                    {
                        return;
                    }
                    app.RequestedThemeVariant = app.ActualThemeVariant == ThemeVariant.Dark
                        ? ThemeVariant.Light
                        : ThemeVariant.Dark;
                });
                return Task.CompletedTask;
            }));

        registry.Register(new CommandDescriptor(
            Id: "app.about",
            Title: "About Cinder",
            Subtitle: "Version, license, and acknowledgments.",
            Category: "Help",
            Invoke: _ => Task.CompletedTask));

        registry.Register(new CommandDescriptor(
            Id: "app.exit",
            Title: "Exit Cinder",
            Subtitle: null,
            Category: "App",
            Invoke: _ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (Avalonia.Application.Current?.ApplicationLifetime is
                        Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.Shutdown();
                    }
                });
                return Task.CompletedTask;
            }));
    }
}
