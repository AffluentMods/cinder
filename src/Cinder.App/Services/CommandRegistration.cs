using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Cinder.App.ViewModels;
using Cinder.App.Views;

namespace Cinder.App.Services;

/// <summary>Built-in command palette actions registered at startup.</summary>
public static class CommandRegistration
{
    public static void RegisterBuiltIns(CommandRegistry registry, MainWindowViewModel mainVm)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(mainVm);

        registry.Register(new CommandDescriptor(
            Id: "file.open",
            Title: "Open file… (Ctrl+O)",
            Subtitle: "Load a file into the hex viewer.",
            Category: "File",
            Invoke: async ct =>
            {
                var window = ResolveMainWindow();
                if (window is null)
                {
                    return;
                }
                var picked = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open file in Cinder",
                    AllowMultiple = false,
                }).ConfigureAwait(false);
                var path = picked.FirstOrDefault()?.TryGetLocalPath();
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }
                Dispatcher.UIThread.Post(() =>
                {
                    mainVm.OpenFileInNewBuffer(path);
                    var hexTab = mainVm.Tabs.FirstOrDefault(t => t.Kind == "hex");
                    if (hexTab is not null)
                    {
                        mainVm.SelectedTab = hexTab;
                    }
                });
                _ = ct;
            }));

        registry.Register(new CommandDescriptor(
            Id: "hex.find",
            Title: "Find in buffer… (Ctrl+F)",
            Subtitle: "Search the open file: hex / ASCII / UTF-16 / regex.",
            Category: "Hex",
            Invoke: _ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var owner = ResolveMainWindow();
                    if (owner is null || mainVm.Hex.Buffer is null)
                    {
                        return;
                    }
                    var dialog = new FindDialog { DataContext = new FindDialogViewModel(mainVm.Hex) };
                    dialog.ShowDialog(owner);
                });
                return Task.CompletedTask;
            }));

        registry.Register(new CommandDescriptor(
            Id: "hex.goto",
            Title: "Goto offset… (Ctrl+G)",
            Subtitle: "Jump the caret to a specific offset (decimal or 0x… hex).",
            Category: "Hex",
            Invoke: _ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var owner = ResolveMainWindow();
                    if (owner is null || mainVm.Hex.Buffer is null)
                    {
                        return;
                    }
                    var dialog = new GotoDialog { DataContext = mainVm.Hex };
                    dialog.ShowDialog(owner);
                });
                return Task.CompletedTask;
            }));

        registry.Register(new CommandDescriptor(
            Id: "app.settings",
            Title: "Settings… (Ctrl+,)",
            Subtitle: "Theme, density, AI provider, cloud client_ids, parsers directory.",
            Category: "App",
            Invoke: _ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var owner = ResolveMainWindow();
                    if (owner is null)
                    {
                        return;
                    }
                    var dialog = new SettingsDialog { DataContext = new SettingsDialogViewModel(new SettingsStore()) };
                    dialog.ShowDialog(owner);
                });
                return Task.CompletedTask;
            }));

        registry.Register(new CommandDescriptor(
            Id: "case.create",
            Title: "Create case…",
            Subtitle: "Set up a new case file (.cinder) with chain-of-custody log.",
            Category: "Case",
            Invoke: async ct =>
            {
                var owner = ResolveMainWindow();
                if (owner is null)
                {
                    return;
                }
                var picked = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Create Cinder case",
                    DefaultExtension = "cinder",
                    SuggestedFileName = "case.cinder",
                    FileTypeChoices = [new FilePickerFileType("Cinder case") { Patterns = ["*.cinder"] }],
                }).ConfigureAwait(false);
                var path = picked?.TryGetLocalPath();
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }
                try
                {
                    var store = new Cinder.Core.Cases.CaseStore(path);
                    var custody = new Cinder.Core.Custody.CustodyLog(store);
                    var svc = new Cinder.Core.Cases.CaseService(store, custody);
                    var c = await svc.CreateAsync(System.IO.Path.GetFileNameWithoutExtension(path), Environment.UserName, null, ct).ConfigureAwait(false);
                    Dispatcher.UIThread.Post(() =>
                    {
                        mainVm.ActiveCaseName = c.Name;
                        mainVm.Announce($"Case created: {c.Name}");
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() => mainVm.Announce($"Failed to create case: {ex.Message}"));
                }
            }));

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
                    if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.Shutdown();
                    }
                });
                return Task.CompletedTask;
            }));
    }

    private static Window? ResolveMainWindow()
        => (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
