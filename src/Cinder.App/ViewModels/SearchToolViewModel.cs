using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Cinder.App.Services;
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

    [ObservableProperty]
    private bool _isIndexing;

    [ObservableProperty]
    private int _docCount;

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

    /// <summary>
    /// Build (or extend) the Lucene index from a folder of evidence. Walks every file under
    /// the picked folder, extracts text via DocumentReader where possible (DOCX/PDF/etc.), and
    /// falls back to a printable-strings extraction for binary files. Each file becomes one
    /// indexed document.
    /// </summary>
    [RelayCommand]
    private async Task BuildIndexAsync(CancellationToken ct)
    {
        if (_index is null)
        {
            StatusLine = "Pick an index folder first.";
            return;
        }
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick the evidence folder to ingest",
        });
        var sourcePath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(sourcePath)) return;

        IsIndexing = true;
        StatusLine = "Ingesting…";
        try
        {
            _index.OpenForWrite();
            var indexed = await Task.Run(async () => await Ingest(sourcePath, _index, ct), ct);
            _index.Commit();
            DocCount += indexed;
            StatusLine = $"Indexed {indexed:N0} file{(indexed == 1 ? "" : "s")} from {sourcePath}.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Index build failed: {ex.Message}";
        }
        finally
        {
            IsIndexing = false;
        }
    }

    /// <summary>
    /// Walk a directory recursively and index every file under 50 MB. Tries DocumentReader
    /// first (which handles DOCX / PDF / XLSX / PPTX / ODT / EPUB / RTF / TXT / code), then
    /// falls back to a printable-strings extraction for binaries — captures every URL,
    /// filename, error message etc. embedded in executables, memory dumps, images, etc.
    /// </summary>
    private static async Task<int> Ingest(string root, CaseIndex index, CancellationToken ct)
    {
        int n = 0;
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", enumeration))
        {
            ct.ThrowIfCancellationRequested();
            if (n >= 100_000) break;
            FileInfo info;
            try { info = new FileInfo(path); }
            catch { continue; }
            if (info.Length == 0 || info.Length > 50L * 1024 * 1024) continue;

            try
            {
                // Try DocumentReader first for known textual formats.
                var result = await DocumentReader.ReadAsync(path, ct);
                var text = result.Success && !string.IsNullOrWhiteSpace(result.Text)
                    ? result.Text
                    : ExtractPrintableStrings(path, ct);
                if (string.IsNullOrWhiteSpace(text)) continue;

                index.IndexDocument(new IndexableDoc(
                    Id: Guid.NewGuid().ToString(),
                    Source: "file",
                    User: null,
                    Path: path,
                    Summary: Path.GetFileName(path),
                    Text: text,
                    Timestamp: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    Tags: [Path.GetExtension(path).TrimStart('.')]));
                n++;
            }
            catch
            {
                // skip unreadable files
            }
        }
        return n;
    }

    private static string ExtractPrintableStrings(string path, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        Span<byte> buf = stackalloc byte[1 << 15];
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
