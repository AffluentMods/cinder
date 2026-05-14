using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Cinder.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

/// <summary>Lucene.NET case-wide search UI.</summary>
public sealed partial class SearchToolViewModel : ViewModelBase
{
    private CaseIndex? _index;

    [ObservableProperty]
    private string _indexPath = "";

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private string? _statusLine;

    public ObservableCollection<SearchHit> Hits { get; } = new();

    [RelayCommand]
    private async Task PickIndexFolderAsync(CancellationToken ct)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Pick the case index folder",
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        IndexPath = path;
        try
        {
            _index?.Dispose();
            _index = new CaseIndex(path);
            StatusLine = $"Opened index at {path}.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Run()
    {
        if (_index is null)
        {
            StatusLine = "Pick or build an index first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Query))
        {
            return;
        }
        Hits.Clear();
        try
        {
            foreach (var h in _index.Search(Query, max: 200))
            {
                Hits.Add(h);
            }
            StatusLine = $"{Hits.Count} hit{(Hits.Count == 1 ? "" : "s")} for \"{Query}\".";
        }
        catch (Exception ex)
        {
            StatusLine = $"Search failed: {ex.Message}";
        }
    }
}
