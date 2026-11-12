using Avalonia.Platform.Storage;
using Cinder.App.Services;
using Cinder.Core.Analysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels.Tools;

/// <summary>
/// Checks a folder of evidence against a list of indicators.
///
/// <para>Two inputs, and they are not the same kind of thing. The folder is the evidence: it is
/// what the custody log records as examined and what a bookmark is attributed to. The list is
/// what the folder was examined with, recorded beside it. (<see cref="SidecarToolViewModel.EvidencePath"/>
/// holds the list, because the list is what the shared run command is given; the two custody
/// overrides below put that right.)</para>
///
/// <para>Results belong to one list and one folder. Changing either clears the grid, so matches
/// from the previous pair are never left on screen under the new names.</para>
/// </summary>
public sealed partial class IocMatchTool : SidecarToolViewModel
{
    public override string Id => "ioc";
    public override string Title => "IOC match";
    public override string Icon => "🎯";
    public override string Subtitle => "Check a folder of evidence against a list of indicators: file hashes, names and paths, text inside files, and timeline events.";
    public override string Phase => "6";
    public override string Kind => "ioc";

    public override string? EmptyStateHint => "Pick an indicator list (a text file with one indicator per line) and the folder to scan.";

    /// <summary>Most matches one scan shows.</summary>
    internal const int RowBudget = 50_000;

    /// <summary>Folder of evidence to scan — a triage collection, a mounted image, an export.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunScan))]
    private string? _targetFolder;

    [ObservableProperty] private string? _indicatorSummary;

    /// <summary>True once both a list and a folder are chosen: the scan has everything it needs.</summary>
    public bool CanRunScan => !string.IsNullOrEmpty(EvidencePath) && !string.IsNullOrEmpty(TargetFolder);

    /// <summary>The folder that was scanned is the evidence.</summary>
    protected override string? CustodyEvidence => TargetFolder;

    /// <summary>The indicator list is what the evidence was examined with.</summary>
    protected override string? CustodyInput => EvidencePath;

    protected override string FailureTitle(string path) => "Could not run the scan";

    protected override string FailureStatus(string name) => "The scan did not run.";

    protected override string StoppedStatus => "Stopped. The scan did not finish, so no matches are shown.";

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(EvidencePath))
        {
            OnPropertyChanged(nameof(CanRunScan));
        }
    }

    partial void OnTargetFolderChanged(string? value) =>
        ClearResults(Rows.Count > 0 || IsLoading ? "The folder changed. Run the scan again to see matches for it." : null);

    [RelayCommand]
    private async Task PickIocListAsync()
    {
        var path = await ToolDialog.PickFileAsync("Pick an indicator list", "Text or CSV file", "*.*");
        if (!string.IsNullOrEmpty(path))
        {
            await SetIndicatorListAsync(path);
        }
    }

    /// <summary>
    /// Makes <paramref name="path"/> the indicator list: clears any results (they were for the
    /// previous list), stops a scan that is running, and says how many indicators of each kind
    /// the new list holds.
    /// </summary>
    internal async Task SetIndicatorListAsync(string path)
    {
        var hadResults = Rows.Count > 0 || IsLoading;
        EvidencePath = path;
        ClearResults(hadResults ? "The indicator list changed. Run the scan again to see matches for it." : null);
        try
        {
            var iocs = IocList.Parse(await File.ReadAllTextAsync(path));
            var byType = iocs.GroupBy(i => i.Type).Select(g => $"{g.Count()} {g.Key}");
            IndicatorSummary = iocs.Count == 0
                ? "No indicators were recognised in that file. Put one hash, address, domain, path or text string on each line."
                : $"{iocs.Count:N0} indicators: {string.Join(", ", byType)}";
        }
        catch (Exception ex)
        {
            IndicatorSummary = $"Could not read the indicator list: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
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
        if (string.IsNullOrEmpty(folder))
        {
            throw new InvalidOperationException("Pick the folder to scan, then run the scan again.");
        }
        if (!Directory.Exists(folder))
        {
            throw new InvalidOperationException($"The folder to scan no longer exists or cannot be opened: {folder}");
        }

        var iocs = IocList.Parse(await File.ReadAllTextAsync(evidencePath, ct));
        if (iocs.Count == 0)
        {
            throw new InvalidOperationException(
                "No indicators were recognised in the list. Put one hash, address, domain, path or text string on each line.");
        }

        // A run that has been stopped or replaced must not write to the status line any more.
        // Progress callbacks are posted, so the last one can arrive after the scan has returned.
        // Once it has, they are ignored: otherwise "Scanning…" could replace the final result.
        var finished = false;
        var progress = new Progress<string>(s =>
        {
            if (!finished && !ct.IsCancellationRequested)
            {
                StatusLine = $"Scanning · {s}";
            }
        });
        // The scanner does its own work on the thread pool; nothing here runs on the UI thread.
        var (rows, stats) = await IocScanner.ScanAsync(iocs, folder, progress, maxRows: RowBudget, ct);
        finished = true;
        ct.ThrowIfCancellationRequested();

        IndicatorSummary = DescribeScan(iocs.Count, stats.Files, stats.FilesHashed, stats.FilesScanned,
            stats.TimelineEvents, stats.Errors, stats.DamagedChunks, stats.DamagedFilePaths);
        var capped = rows.Count >= RowBudget;
        // "Nothing matched" is only as strong as what could be read. When something could
        // not be, the empty result says so instead of reading as a clean bill of health.
        var incomplete = stats.Errors > 0 || stats.DamagedChunks > 0;
        return new ParseResult(
            [.. rows.Cast<object>()],
            Truncated: capped,
            Note: rows.Count == 0
                ? incomplete
                    ? $"No indicator matched in what could be read under {folder}. Part of it could not be read, so this does not show the indicators are absent."
                    : $"No indicator matched anything under {folder}."
                : capped ? null : $"Scanned {folder}.",
            CustodyDetail: new
            {
                Indicators = iocs.Count,
                stats.Files,
                stats.FilesHashed,
                FilesSearched = stats.FilesScanned,
                stats.TimelineEvents,
                FilesUnreadable = stats.Errors,
                stats.DamagedChunks,
                stats.DamagedFiles,
            },
            TruncationReason: capped
                ? $"Showing the first {RowBudget:N0} matches. The scan stopped at that limit, so there are more matches than these. " +
                  "Use a shorter indicator list or scan one subfolder at a time. Export and bookmarks cover only the matches shown."
                : null);
    }

    /// <summary>
    /// The line under the toolbar after a scan: how much was looked at, and anything that means
    /// a match could have been missed. Unreadable files and damaged source chunks are both
    /// reasons a clean result is weaker than it looks, so they are stated, not folded into a total.
    /// </summary>
    internal static string DescribeScan(int indicators, int files, int hashed, int searched, int timelineEvents,
                                        int unreadable, int damagedChunks, IReadOnlyList<string>? damagedFiles = null)
    {
        var text = $"{indicators:N0} indicator{(indicators == 1 ? "" : "s")} checked against {files:N0} file{(files == 1 ? "" : "s")}: " +
                   $"{hashed:N0} hashed, {searched:N0} searched for text, {timelineEvents:N0} timeline event{(timelineEvents == 1 ? "" : "s")} read.";
        if (unreadable > 0)
        {
            text += unreadable == 1
                ? " 1 file could not be read, so a match in it could have been missed."
                : $" {unreadable:N0} files could not be read, so a match in them could have been missed.";
        }
        if (damagedChunks > 0)
        {
            text += damagedChunks == 1
                ? " 1 damaged chunk in the source read as zeros, so a match there could have been missed."
                : $" {damagedChunks:N0} damaged chunks in the source read as zeros, so a match there could have been missed.";
            if (damagedFiles is { Count: > 0 })
            {
                // Name the images, so the examiner knows which ones to verify.
                // Last segment under either separator: Path.GetFileName only splits on the
                // current platform's, so a Windows path listed on Linux kept its folders.
                var names = damagedFiles.Take(3).Select(f => f[(f.LastIndexOfAny(['/', '\\']) + 1)..]);
                text += $" Affected: {string.Join(", ", names)}{(damagedFiles.Count > 3 ? $" and {damagedFiles.Count - 3:N0} more" : "")}.";
            }
        }
        return text;
    }
}
