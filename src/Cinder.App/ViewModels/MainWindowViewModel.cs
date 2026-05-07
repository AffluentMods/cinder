using System.Collections.ObjectModel;
using Cinder.App.Services;
using Cinder.Core.Hashing;
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

    public ObservableCollection<string> ActivityLog { get; } =
    [
        "Cinder started.",
        "Press Ctrl+K for the command palette · Ctrl+H to hash a file.",
    ];

    [ObservableProperty]
    private TabItemViewModel _selectedTab;

    [ObservableProperty]
    private string _statusBar = "🛡 WriteBlock OFF · No case · ⌘K for the command palette";

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
    }

    [RelayCommand]
    private void OpenPalette() => Palette.IsOpen = true;
}

public sealed partial class TabItemViewModel(string title, string kind) : ViewModelBase
{
    public string Title { get; } = title;
    public string Kind { get; } = kind;
}
