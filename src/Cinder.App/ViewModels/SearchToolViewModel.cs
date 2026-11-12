using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Cinder.App.Services;
using Cinder.App.ViewModels.Tools;
using Cinder.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

/// <summary>
/// Full-text search over a folder. The examiner indexes a folder once (the index lives in a
/// folder they choose) and then queries it; an index built earlier can be reopened.
/// </summary>
public sealed partial class SearchToolViewModel : ViewModelBase
{
    /// <summary>Results shown per query. Said out loud in the status line when it is reached.</summary>
    private const int MaxHits = 500;

    /// <summary>Files larger than this are skipped by the indexer (and counted as skipped).</summary>
    private const long MaxFileBytes = 50L * 1024 * 1024;

    private CaseIndex? _index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIndex), nameof(ShowReadyState))]
    private string? _indexPath;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadyTitle), nameof(ReadyMessage))]
    private string? _statusLine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReadyState))]
    private bool _isIndexing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadyTitle), nameof(ReadyMessage))]
    private int _docCount;

    [ObservableProperty]
    private SearchHit? _selectedHit;

    /// <summary>The last query that ran, so the "no results" state can name it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadyTitle), nameof(ReadyMessage))]
    private string? _lastQuery;

    public ObservableCollection<SearchHit> Hits { get; } = new();

    public SearchToolViewModel()
    {
        Hits.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowReadyState));
    }

    public bool HasIndex => !string.IsNullOrEmpty(IndexPath);

    /// <summary>An index is open and there is nothing in the grid: say what to do, or that nothing matched.</summary>
    public bool ShowReadyState => HasIndex && Hits.Count == 0 && !IsIndexing;

    public string ReadyTitle => LastQuery is null
        ? DocCount == 0 ? "This index is empty" : "Ready to search"
        : "No results";

    public string ReadyMessage => LastQuery is null
        ? DocCount == 0
            ? "Use \"Index a folder…\" to add files to it."
            : $"{DocCount:N0} files indexed. Type what you are looking for above and press Enter."
        : $"Nothing in the index matches \"{LastQuery}\". Check the spelling, try fewer words, or index more folders.";

    [RelayCommand]
    private async Task PickIndexFolderAsync(CancellationToken ct)
    {
        var path = await PickFolderAsync("Open an existing search index folder");
        if (string.IsNullOrEmpty(path)) return;
        if (!CaseIndex.Exists(path))
        {
            StatusLine = $"{path} does not contain a search index. Use \"Index a folder…\" to build one.";
            return;
        }
        OpenIndex(path);
        _ = ct;
    }

    private bool OpenIndex(string path)
    {
        try
        {
            _index?.Dispose();
            _index = new CaseIndex(path);
            IndexPath = path;
            DocCount = _index.DocumentCount;
            LastQuery = null;
            Hits.Clear();
            StatusLine = $"Index open: {DocCount:N0} files.";
            return true;
        }
        catch (Exception ex)
        {
            _index = null;
            IndexPath = null;
            StatusLine = $"Could not open the index at {path}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Index every file under a folder. Text comes from <see cref="DocumentReader"/> where the
    /// format is known (DOCX, PDF, XLSX, …) and from a printable-strings pass otherwise, so
    /// names, URLs and messages embedded in binaries are searchable too. Re-indexing a folder
    /// replaces its files rather than duplicating them.
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task BuildIndexAsync(CancellationToken ct)
    {
        var sourcePath = await PickFolderAsync("Pick the folder to index");
        if (string.IsNullOrEmpty(sourcePath)) return;

        if (_index is null)
        {
            var indexFolder = await PickFolderAsync("Pick an empty folder to store the search index in");
            if (string.IsNullOrEmpty(indexFolder)) return;
            if (IsInside(indexFolder, sourcePath))
            {
                StatusLine = "Store the index outside the folder being indexed, or it would index itself.";
                return;
            }
            if (!OpenIndex(indexFolder)) return;
        }
        var index = _index!;

        IsIndexing = true;
        LastQuery = null;
        Hits.Clear();
        StatusLine = "Indexing…";
        var progress = new Progress<int>(n => StatusLine = $"Indexing… {n:N0} files");
        var result = new IngestResult();
        try
        {
            index.OpenForWrite();
            await Task.Run(() => IngestAsync(sourcePath, index, result, progress, ct), ct);
            StatusLine = $"Indexed {result.Indexed:N0} file{(result.Indexed == 1 ? "" : "s")} from {sourcePath}."
                + (result.TooLarge > 0 ? $" Skipped {result.TooLarge:N0} over 50 MB." : "")
                + (result.Unreadable > 0 ? $" {result.Unreadable:N0} could not be read." : "");
        }
        catch (OperationCanceledException)
        {
            StatusLine = $"Stopped. The {result.Indexed:N0} files indexed so far are searchable.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Indexing failed: {ex.Message}";
        }
        finally
        {
            try
            {
                index.Commit();
                DocCount = index.DocumentCount;
            }
            catch (Exception ex)
            {
                StatusLine = $"Could not save the index: {ex.Message}";
            }
            IsIndexing = false;
        }
        await ActiveCaseContext.LogAsync(Cinder.Core.Custody.CustodyAction.ParserRan,
            new { Tool = "search", Action = "index", Source = sourcePath, Index = IndexPath, Files = result.Indexed }, CancellationToken.None);
    }

    private sealed class IngestResult
    {
        public int Indexed;
        public int TooLarge;
        public int Unreadable;
    }

    private static bool IsInside(string candidate, string parent)
    {
        var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return c.StartsWith(p, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static async Task IngestAsync(string root, CaseIndex index, IngestResult result, IProgress<int> progress, CancellationToken ct)
    {
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", enumeration))
        {
            ct.ThrowIfCancellationRequested();
            FileInfo info;
            try { info = new FileInfo(path); }
            catch { result.Unreadable++; continue; }
            if (info.Length == 0) continue;
            if (info.Length > MaxFileBytes) { result.TooLarge++; continue; }

            try
            {
                var read = await DocumentReader.ReadAsync(path, ct);
                var text = read.Success && !string.IsNullOrWhiteSpace(read.Text)
                    ? read.Text
                    : ExtractPrintableStrings(path, ct);
                if (string.IsNullOrWhiteSpace(text)) continue;

                // The path is the document id, so indexing the same folder twice updates
                // each file in place instead of returning every hit twice.
                index.IndexDocument(new IndexableDoc(
                    Id: path,
                    Source: "file",
                    User: null,
                    Path: path,
                    Summary: Path.GetFileName(path),
                    Text: text,
                    Timestamp: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    Tags: [Path.GetExtension(path).TrimStart('.')]));
                result.Indexed++;
                if (result.Indexed % 25 == 0)
                {
                    progress.Report(result.Indexed);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                result.Unreadable++;
            }
        }
    }

    private static string ExtractPrintableStrings(string path, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var buf = new byte[1 << 15];
        var run = new System.Text.StringBuilder();
        int read;
        while ((read = fs.Read(buf)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++)
            {
                var b = buf[i];
                if (b is >= 0x20 and < 0x7F)
                {
                    run.Append((char)b);
                }
                else
                {
                    if (run.Length >= 6)
                    {
                        sb.Append(run);
                        sb.Append(' ');
                    }
                    run.Clear();
                }
            }
        }
        if (run.Length >= 6) sb.Append(run);
        return sb.ToString();
    }

    [RelayCommand]
    private void Run()
    {
        if (_index is null)
        {
            StatusLine = "Index a folder first, or open an index you built earlier.";
            return;
        }
        var query = Query.Trim();
        if (query.Length == 0)
        {
            return;
        }
        Hits.Clear();
        try
        {
            var hits = _index.Search(query, MaxHits);
            foreach (var h in hits)
            {
                Hits.Add(h);
            }
            LastQuery = query;
            StatusLine = Hits.Count >= MaxHits
                ? $"Showing the best {MaxHits:N0} matches for \"{query}\". Add words to narrow it down."
                : $"{Hits.Count:N0} match{(Hits.Count == 1 ? "" : "es")} for \"{query}\".";
        }
        catch (FormatException ex)
        {
            LastQuery = null;
            StatusLine = $"That query could not be read: {ex.Message}";
        }
        catch (Exception ex)
        {
            LastQuery = null;
            StatusLine = $"Search failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenHit(SearchHit? hit)
    {
        hit ??= SelectedHit;
        if (hit?.Path is not { Length: > 0 } path)
        {
            return;
        }
        if (!File.Exists(path))
        {
            StatusLine = $"{path} is no longer at that location.";
            return;
        }
        AppNavigator.OpenInHex?.Invoke(path, null);
    }

    [RelayCommand]
    private async Task ExportAsync(string format, CancellationToken ct)
    {
        var rows = Hits.Select(h => (object)new
        {
            h.Score,
            File = h.Summary ?? "",
            Path = h.Path ?? "",
            ModifiedUtc = h.Timestamp?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            Query = LastQuery ?? "",
        }).ToList();
        if (await ToolExport.SaveRowsAsync("search", "search-results", format, rows, ct) is { } status) StatusLine = status;
    }

    private static async Task<string?> PickFolderAsync(string title)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return null;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
