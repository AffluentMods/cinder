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
    public override string Subtitle => "Check a folder of evidence against a list of indicators: file hashes, names and paths, text inside files, and timeline events.";
    public override string Phase => "6";
    public override string Kind => "ioc";

    /// <summary>Folder of evidence to scan — a triage collection, a mounted image, an export.</summary>
    [ObservableProperty] private string? _targetFolder;

    [ObservableProperty] private string? _indicatorSummary;

    public override string? EmptyStateHint => "Pick an indicator list (a text file with one indicator per line) and the folder to scan.";

    private const int RowBudget = 50_000;

    /// <summary>The custody log records the folder that was scanned, not only the list it was scanned with.</summary>
    protected override string? CustodyTarget => TargetFolder;

    // Results belong to one list and one folder. Changing either makes the grid stale, so it
    // is cleared rather than left on screen under the new names.
    partial void OnTargetFolderChanged(string? value) =>
        ClearResults(Rows.Count > 0 ? "The folder changed. Run the scan again to see matches for it." : null);

    [RelayCommand]
    private async Task PickIocListAsync()
    {
        var path = await ToolDialog.PickFileAsync("Pick an IOC list", "Text / CSV", "*.*");
        if (!string.IsNullOrEmpty(path))
        {
            var hadResults = Rows.Count > 0;
            EvidencePath = path;
            ClearResults(hadResults ? "The indicator list changed. Run the scan again to see matches for it." : null);
            try
            {
                var iocs = IocList.Parse(await File.ReadAllTextAsync(path));
                var byType = iocs.GroupBy(i => i.Type).Select(g => $"{g.Count()} {g.Key}");
                IndicatorSummary = iocs.Count == 0
                    ? "No indicators were recognised in that file. Put one hash, address, domain, path or text string on each line."
                    : $"{iocs.Count} indicators: {string.Join(", ", byType)}";
            }
            catch (Exception ex)
            {
                IndicatorSummary = $"Could not read the indicator list: {ex.Message}";
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

    protected override async Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct)
    {
        var folder = TargetFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            throw new InvalidOperationException("Pick the folder to scan first.");
        }

        var iocs = IocList.Parse(await File.ReadAllTextAsync(evidencePath, ct));
        if (iocs.Count == 0)
        {
            throw new InvalidOperationException(
                "No indicators were recognised in the list. Put one hash, address, domain, path or text string on each line.");
        }

        // A run that has been stopped or replaced must not write to the status line any more.
        var progress = new Progress<string>(s =>
        {
            if (!ct.IsCancellationRequested)
            {
                StatusLine = $"Scanning · {s}";
            }
        });
        // The scanner does its own work on the thread pool; nothing here runs on the UI thread.
        var (rows, stats) = await IocScanner.ScanAsync(iocs, folder, progress, maxRows: RowBudget, ct);
        ct.ThrowIfCancellationRequested();

        IndicatorSummary = $"{iocs.Count} indicators · {stats}";
        return ParseResult.WithBudget([.. rows.Cast<object>()], RowBudget,
            rows.Count == 0 ? $"No indicator matched anything under {folder}." : null);
    }
}
