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
            Title: "Open file…",
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
                    var hexTool = mainVm.Workspace.FindByKind("hex");
                    if (hexTool is not null)
                    {
                        mainVm.Workspace.SelectedTool = hexTool;
                    }
                });
                _ = ct;
            }));

        registry.Register(new CommandDescriptor(
            Id: "hex.find",
            Title: "Find in buffer (dialog)…",
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
            Title: "Go to offset…",
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
            Title: "Settings…",
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
            Id: "case.open",
            Title: "Open case…",
            Subtitle: "Open an existing .cinder case file from disk.",
            Category: "Case",
            Invoke: async ct =>
            {
                var owner = ResolveMainWindow();
                if (owner is null)
                {
                    return;
                }
                var picked = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open Cinder case",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Cinder case") { Patterns = ["*.cinder"] }],
                }).ConfigureAwait(false);
                var path = picked.FirstOrDefault()?.TryGetLocalPath();
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }
                await mainVm.OpenCaseFromPathAsync(path).ConfigureAwait(false);
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
                        mainVm.OpenCase(c.Id, c.Name, c.Examiner, path);
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
            Title: "Toggle theme (dark / light)",
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
            Subtitle: "Version and license.",
            Category: "Help",
            Invoke: _ =>
            {
                Dispatcher.UIThread.Post(() =>
                    mainVm.Announce($"Cinder {UpdateChecker.CurrentVersion()} · Apache-2.0 · github.com/AffluentMods/cinder"));
                return Task.CompletedTask;
            }));

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

    /// <summary>Shortcut shown in the palette for each built-in that has one.</summary>
    private static readonly Dictionary<string, string> Shortcuts = new(StringComparer.Ordinal)
    {
        ["file.open"] = "Ctrl O",
        ["hex.goto"] = "Ctrl G",
        ["app.settings"] = "Ctrl ,",
        ["case.open"] = "Ctrl Shift O",
        ["case.create"] = "Ctrl N",
    };

    /// <summary>
    /// Everything reachable from the rail and the shell is reachable from Ctrl+K too: one
    /// "go to" command per tool, the pane toggles, and the hex viewer's keyboard actions.
    /// </summary>
    public static void RegisterNavigation(CommandRegistry registry, MainWindowViewModel mainVm)
    {
        foreach (var (id, keys) in Shortcuts)
        {
            if (registry.Commands.FirstOrDefault(c => c.Id == id) is { } c)
            {
                registry.Replace(c with { Shortcut = keys });
            }
        }

        foreach (var section in mainVm.Workspace.Sections)
        {
            foreach (var tool in section.Tools)
            {
                var t = tool;
                registry.Register(new CommandDescriptor(
                    Id: "tool." + t.Id,
                    Title: t.Title,
                    Subtitle: t.Subtitle,
                    Category: section.Title == "Home" ? "Go to" : section.Title,
                    Invoke: _ =>
                    {
                        Dispatcher.UIThread.Post(() => mainVm.Workspace.SelectedTool = t);
                        return Task.CompletedTask;
                    }));
            }
        }

        void Action(string id, string title, string category, string? keys, Action run, string? subtitle = null) =>
            registry.Register(new CommandDescriptor(id, title, subtitle, category, _ =>
            {
                Dispatcher.UIThread.Post(run);
                return Task.CompletedTask;
            }, keys));

        Action("view.rail", "Toggle sidebar", "View", "Ctrl B", () => mainVm.ToggleRailCommand.Execute(null));
        Action("view.inspector", "Toggle inspector", "View", "Ctrl Alt B", () => mainVm.ToggleInspectorCommand.Execute(null),
            "The byte inspector beside the hex viewer.");
        Action("hex.findinline", "Find in hex viewer", "Hex", "Ctrl F", () => mainVm.FocusFindCommand.Execute(null));
        Action("hex.findnext", "Find next", "Hex", "F3", () => mainVm.Hex.FindNextCommand.Execute(null));
        Action("hex.findprev", "Find previous", "Hex", "Shift F3", () => mainVm.Hex.FindPreviousCommand.Execute(null));
        Action("hex.bookmark", "Bookmark caret", "Hex", "Ctrl D", () => mainVm.Hex.AddBookmarkCommand.Execute(null));
        Action("hex.copyhex", "Copy selection as hex", "Hex", "Ctrl C", () => mainVm.Hex.CopySelectionAsHexCommand.Execute(null));
        Action("hex.back", "Back to previous offset", "Hex", "Alt Left", () => mainVm.Hex.NavigateBackCommand.Execute(null));
        Action("hex.forward", "Forward to next offset", "Hex", "Alt Right", () => mainVm.Hex.NavigateForwardCommand.Execute(null));
        Action("hex.strings", "Open current file in Strings", "Hex", null, () => mainVm.OpenInStringsCommand.Execute(null));
        Action("hex.hash", "Hash current file (SHA-256)", "Hex", null, () => mainVm.Hex.StartHashCommand.Execute(null),
            "Reads every byte; files up to 2 GiB are hashed automatically on open.");
        Action("hex.utf16", "Show text pane as UTF-16LE", "Hex", null, () => mainVm.Hex.TextIsUtf16 = true);
        Action("hex.ascii", "Show text pane as ASCII", "Hex", null, () => mainVm.Hex.TextIsUtf16 = false);
        Action("hex.close", "Close file tab", "Hex", "Ctrl W", () => mainVm.CloseBufferCommand.Execute(mainVm.Hex));
        Action("case.close", "Close case", "Case", null, () => mainVm.CloseCaseCommand.Execute(mainVm.ActiveCase));
        Action("help.tool", "Help for this tool", "Help", "F1", () => mainVm.OpenHelpCommand.Execute(null));
    }

    private static Window? ResolveMainWindow()
        => (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
