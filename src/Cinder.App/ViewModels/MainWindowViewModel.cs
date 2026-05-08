using System.Collections.ObjectModel;
using Avalonia.Threading;
using Cinder.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public CommandRegistry Commands { get; }
    public CommandPaletteViewModel Palette { get; }
    public HexViewModel Hex { get; }

    public ObservableCollection<string> CaseTreeItems { get; } = ["No case open"];

    public ObservableCollection<TabItemViewModel> Tabs { get; }

    public ObservableCollection<string> ActivityLog { get; } = ["Cinder started."];

    [ObservableProperty]
    private string _activityHeadline = "Cinder started.";

    [ObservableProperty]
    private TabItemViewModel _selectedTab;

    [ObservableProperty]
    private string _statusBar = "🛡 WriteBlock OFF · No case · Ctrl+K for the command palette";

    [ObservableProperty]
    private string _headerSubtitle = "Affluent Labs · pre-alpha";

    [ObservableProperty]
    private string? _activeCaseName;

    public MainWindowViewModel(CommandRegistry commands)
    {
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        Hex = new HexViewModel();
        Palette = new CommandPaletteViewModel(commands);
        Tabs =
        [
            new("Hex", "hex"),
            new("Gallery", "gallery"),
            new("Timeline", "timeline"),
            new("Reports", "reports"),
        ];
        _selectedTab = Tabs[0];

        // Status-bar headline auto-fades to "Ready" after 3s if nothing else is happening.
        DispatcherTimer.RunOnce(() =>
        {
            if (ActivityHeadline == "Cinder started.")
            {
                ActivityHeadline = "Ready";
            }
        }, TimeSpan.FromSeconds(3));
    }

    public void Announce(string headline)
    {
        ActivityHeadline = headline;
        ActivityLog.Insert(0, headline);
        DispatcherTimer.RunOnce(() =>
        {
            if (ActivityHeadline == headline)
            {
                ActivityHeadline = "Ready";
            }
        }, TimeSpan.FromSeconds(4));
    }

    partial void OnActiveCaseNameChanged(string? value)
    {
        HeaderSubtitle = string.IsNullOrEmpty(value) ? "Affluent Labs · pre-alpha" : value;
    }

    [RelayCommand]
    private void OpenPalette() => Palette.IsOpen = true;

    [RelayCommand]
    private async Task OpenFileAsync(CancellationToken ct)
    {
        var openCmd = Commands.Commands.FirstOrDefault(c => c.Id == "file.open");
        if (openCmd is not null)
        {
            await openCmd.Invoke(ct).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private void OpenFind()
    {
        // Surfaces the Find dialog through the command. The dialog itself is opened from
        // FindCommand.cs (registered in CommandRegistration.RegisterBuiltIns).
        var findCmd = Commands.Commands.FirstOrDefault(c => c.Id == "hex.find");
        findCmd?.Invoke(default);
    }

    [RelayCommand]
    private void OpenGoto()
    {
        var gotoCmd = Commands.Commands.FirstOrDefault(c => c.Id == "hex.goto");
        gotoCmd?.Invoke(default);
    }
}

public sealed partial class TabItemViewModel(string title, string kind) : ViewModelBase
{
    public string Title { get; } = title;
    public string Kind { get; } = kind;
}
