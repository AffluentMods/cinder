using System.Collections.ObjectModel;
using Cinder.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

public sealed partial class CommandPaletteViewModel : ViewModelBase
{
    private readonly CommandRegistry _registry;

    public ObservableCollection<CommandDescriptor> Results { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private CommandDescriptor? _selected;

    public bool HasNoResults => Results.Count == 0;

    public CommandPaletteViewModel(CommandRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        RebuildResults();
    }

    partial void OnQueryChanged(string value) => RebuildResults();

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            Query = string.Empty;
            RebuildResults();
        }
    }

    [RelayCommand]
    private async Task InvokeSelectedAsync(CancellationToken ct)
    {
        var sel = Selected;
        if (sel is null)
        {
            return;
        }
        IsOpen = false;
        _registry.NoteInvoked(sel);
        await sel.Invoke(ct);
    }

    [RelayCommand]
    private async Task InvokeAsync(CommandDescriptor? command, CancellationToken ct)
    {
        if (command is null)
        {
            return;
        }
        Selected = command;
        await InvokeSelectedAsync(ct);
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>Arrow keys, wrapping at both ends.</summary>
    [RelayCommand]
    private void Move(string? delta)
    {
        if (Results.Count == 0)
        {
            return;
        }
        var step = delta == "-1" ? -1 : 1;
        var i = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[((i + step) % Results.Count + Results.Count) % Results.Count];
    }

    private void RebuildResults()
    {
        Results.Clear();
        foreach (var c in _registry.Search(Query))
        {
            Results.Add(c);
        }
        Selected = Results.Count > 0 ? Results[0] : null;
        OnPropertyChanged(nameof(HasNoResults));
    }
}
