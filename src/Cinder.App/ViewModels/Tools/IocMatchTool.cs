using Avalonia.Platform.Storage;
using Cinder.App.Services;
using Cinder.Core.Analysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels.Tools;

/// <summary>
/// IOC matching. "Evidence" for this tool is the indicator list; the target is a folder set
/// separately. Inherits the grid, export, truncation banner and custody logging every parser
/// tool has; the view is its own because it needs two pickers rather than one.
/// </summary>
public sealed partial class IocMatchTool : SidecarToolViewModel
{
    public override string Id => "ioc";
    public override string Title => "IOC match";
    public override string Icon => "🎯";
    public override string Subtitle => "Run an indicator list against a folder: hashes, paths, file content (ASCII + UTF-16), timeline events.";
    public override string Phase => "6";
    public override string Kind => "ioc";

    /// <summary>Folder of evidence to scan — a triage collection, a mounted image, an export.</summary>
    [ObservableProperty] private string? _targetFolder;

    [ObservableProperty] private string? _indicatorSummary;

    public override string? EmptyStateHint => "Pick an IOC list (one indicator per line) and a folder to scan.";

    [RelayCommand]
    private async Task PickIocListAsync()
    {
        var path = await ToolDialog.PickFileAsync("Pick an IOC list", "Text / CSV", "*.*");
        if (!string.IsNullOrEmpty(path))
        {
            EvidencePath = path;
            try
            {
                var iocs = IocList.Parse(await File.ReadAllTextAsync(path));
                var byType = iocs.GroupBy(i => i.Type).Select(g => $"{g.Count()} {g.Key}");
                IndicatorSummary = iocs.Count == 0 ? "No indicators found in that file." : $"{iocs.Count} indicators: {string.Join(", ", byType)}";
            }
            catch (Exception ex)
            {
                IndicatorSummary = $"Could not read list: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Pick the folder to scan",
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            TargetFolder = path;
        }
    }

    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(TargetFolder) || !Directory.Exists(TargetFolder))
        {
            throw new InvalidOperationException("Pick a folder to scan first.");
        }

        var iocs = IocList.Parse(await File.ReadAllTextAsync(evidencePath, ct));
        if (iocs.Count == 0)
        {
            throw new InvalidOperationException("The IOC list contains no indicators.");
        }

        var progress = new Progress<string>(s => StatusLine = $"Scanning · {s}");
        var (rows, stats) = await IocScanner.ScanAsync(iocs, TargetFolder, progress, maxRows: 50_000, ct);

        AddRows(rows, budget: 50_000);
        IndicatorSummary = $"{iocs.Count} indicators · {stats}";
    }
}
