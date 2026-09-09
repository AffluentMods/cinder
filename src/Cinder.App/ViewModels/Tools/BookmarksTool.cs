using System.Collections.ObjectModel;
using Cinder.App.Services;
using Cinder.Core.Cases;
using Cinder.Core.Custody;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels.Tools;

/// <summary>Every finding bookmarked in the active case, with delete. Reports consumes the same list as exhibits.</summary>
public sealed partial class BookmarksTool : ToolViewModel
{
    public override string Id => "bookmarks";
    public override string Title => "Bookmarks";
    public override string Icon => "🔖";
    public override string Subtitle => "Findings flagged in any tool. Review, delete, export; Reports turns them into exhibits.";
    public override string Phase => "8";
    public override string Kind => "bookmarks";

    public override string HelpMarkdown => """
## What this is
The list of everything you flagged with "Bookmark selected" — in a parser grid, on the
timeline, in the IOC tool. Each bookmark keeps the row exactly as it was, your note,
which tool and evidence file it came from, and who flagged it when.

## How to use it in Cinder
1. Open a case; the list loads from the case file. "Refresh" re-reads it.
2. Select a row and "Delete" to withdraw a finding. The deletion is recorded in the
   chain of custody — nothing silently disappears from a case.
3. "Export CSV / JSON" writes the list.
4. In Reports, "Load bookmarks" turns this list into a numbered Exhibits section.
""";

    public ObservableCollection<Bookmark> Bookmarks { get; } = new();

    [ObservableProperty] private Bookmark? _selected;
    [ObservableProperty] private string? _statusLine;

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        Bookmarks.Clear();
        var session = ActiveCaseContext.Current;
        if (session?.Path is null)
        {
            StatusLine = "Open a case to see its bookmarks.";
            return;
        }
        try
        {
            var store = new BookmarkStore(new CaseStore(session.Path));
            foreach (var b in await store.ListAsync(session.Id, ct))
            {
                Bookmarks.Add(b);
            }
            StatusLine = $"{Bookmarks.Count:N0} bookmark{(Bookmarks.Count == 1 ? "" : "s")} in {session.Name}";
        }
        catch (Exception ex)
        {
            StatusLine = $"Could not load bookmarks: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync(CancellationToken ct)
    {
        var b = Selected;
        var session = ActiveCaseContext.Current;
        if (b is null || session?.Path is null)
        {
            StatusLine = "Select a bookmark first.";
            return;
        }
        try
        {
            var store = new BookmarkStore(new CaseStore(session.Path));
            if (await store.DeleteAsync(b.Id, ct))
            {
                Bookmarks.Remove(b);
                await ActiveCaseContext.LogAsync(CustodyAction.Annotation, new
                {
                    Kind = "bookmark-deleted",
                    BookmarkId = b.Id,
                    b.Tool,
                    b.Title,
                    OriginalNote = b.Note,
                }, ct);
                StatusLine = $"Deleted bookmark #{b.Id} (recorded in custody).";
            }
        }
        catch (Exception ex)
        {
            StatusLine = $"Delete failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportAsync(string format, CancellationToken ct)
    {
        if (Bookmarks.Count == 0) return;
        var ext = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? "json" : "csv";
        var path = await ToolDialog.SaveFileAsync($"Export bookmarks as {ext.ToUpperInvariant()}", $"bookmarks.{ext}", ext);
        if (string.IsNullOrEmpty(path)) return;

        var rows = Bookmarks.Select(b => (object)new
        {
            b.Id,
            Created = b.CreatedUtc.ToString("u"),
            b.Examiner,
            b.Tool,
            Evidence = b.EvidencePath ?? "",
            b.Title,
            Note = b.Note ?? "",
            Row = b.RowJson,
        }).ToList();

        try
        {
            var n = await Task.Run(() =>
            {
                if (ext == "json")
                {
                    using var fs = File.Create(path);
                    return Cinder.Core.Export.TabularExporter.WriteJson(rows, fs);
                }
                using var w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
                return Cinder.Core.Export.TabularExporter.WriteCsv(rows, w);
            }, ct);
            StatusLine = $"Exported {n:N0} bookmarks → {path}";
            await ActiveCaseContext.LogAsync(CustodyAction.DataExported, new { Tool = Id, Format = ext, Path = path, Rows = n }, ct);
        }
        catch (Exception ex)
        {
            StatusLine = $"Export failed: {ex.Message}";
        }
    }
}
