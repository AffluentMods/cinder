using System.Collections.ObjectModel;
using Cinder.App.Services;
using Cinder.Core.Cases;
using Cinder.Core.Custody;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels.Tools;

/// <summary>
/// Every finding bookmarked in the active case, with delete and export. Reports consumes the
/// same list as exhibits.
///
/// <para>The list belongs to one case. It reloads whenever the active case changes and each
/// time the tool is opened, and it remembers which case it was loaded from: delete and export
/// refuse to act if that is no longer the open case, so a row can never be removed from, or
/// exported as part of, a case it does not belong to.</para>
/// </summary>
public sealed partial class BookmarksTool : ToolViewModel
{
    public override string Id => "bookmarks";
    public override string Title => "Bookmarks";
    public override string Icon => "🔖";
    public override string Subtitle => "Review, delete and export the findings you bookmarked in the open case.";
    public override string Phase => "8";
    public override string Kind => "bookmarks";

    public override string HelpMarkdown => """
## What this is
The list of everything you flagged with "Bookmark" — in a parser grid, on the timeline,
in the indicator tool. Each bookmark keeps the row exactly as it was, your note, which
tool and evidence file it came from, and who flagged it when.

## How to use it in Cinder
1. Open a case. The list loads from the case file by itself, and reloads when you
   switch to another case or come back to this tool. "Refresh" re-reads it on demand.
2. Select a row and "Delete selected" to withdraw a finding. The deletion is recorded
   in the chain of custody — nothing silently disappears from a case.
3. "Export CSV" / "Export JSON" write the list.
4. In Reports, the same bookmarks become a numbered Exhibits section.

## Limits
Bookmarks are stored in the case file, so there are none until a case is open. The
list shows the open case only.
""";

    public ObservableCollection<Bookmark> Bookmarks { get; } = new();

    [ObservableProperty] private Bookmark? _selected;
    [ObservableProperty] private string? _statusLine;

    /// <summary>The case the list on screen was loaded from. Null when nothing is loaded.</summary>
    private Guid? _loadedCaseId;
    private string? _loadedCasePath;
    private int _loadSerial;

    public BookmarksTool()
    {
        // Raised on the UI thread whenever the open case changes, including to "no case".
        ActiveCaseContext.Changed += OnActiveCaseChanged;
        _ = ReloadAsync(CancellationToken.None);
    }

    private void OnActiveCaseChanged(CaseSession? session) => _ = ReloadAsync(CancellationToken.None);

    /// <summary>Opening the tool brings the list up to date: bookmarks added in other tools appear without a click.</summary>
    protected override void OnSelected() => _ = ReloadAsync(CancellationToken.None);

    [RelayCommand]
    private Task RefreshAsync(CancellationToken ct) => ReloadAsync(ct);

    /// <summary>Clears the list and loads the active case's bookmarks. A slower earlier load cannot overwrite a later one.</summary>
    internal async Task ReloadAsync(CancellationToken ct)
    {
        var serial = ++_loadSerial;
        var session = ActiveCaseContext.Current;
        Bookmarks.Clear();
        Selected = null;
        _loadedCaseId = null;
        _loadedCasePath = null;
        if (session?.Path is null)
        {
            StatusLine = "Open a case to see its bookmarks.";
            return;
        }
        try
        {
            var store = new BookmarkStore(new CaseStore(session.Path));
            var list = await store.ListAsync(session.Id, ct);
            if (serial != _loadSerial)
            {
                return;
            }
            foreach (var b in list)
            {
                Bookmarks.Add(b);
            }
            _loadedCaseId = session.Id;
            _loadedCasePath = session.Path;
            StatusLine = Bookmarks.Count == 0
                ? $"No bookmarks in {session.Name} yet. Select a row in any tool and choose Bookmark."
                : $"{Bookmarks.Count:N0} bookmark{(Bookmarks.Count == 1 ? "" : "s")} in {session.Name}";
        }
        catch (OperationCanceledException)
        {
            // Superseded or stopped: the newer load owns the list.
        }
        catch (Exception ex)
        {
            if (serial == _loadSerial)
            {
                StatusLine = $"Could not load the bookmarks of {session.Name}: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// The open case, when the list on screen was loaded from it. Otherwise null: the list is
    /// reloaded and the caller must stop, because whatever the examiner selected belongs to
    /// another case.
    /// </summary>
    private async Task<CaseSession?> OpenCaseIfListMatchesAsync()
    {
        var session = ActiveCaseContext.Current;
        if (session?.Path is not null
            && _loadedCaseId == session.Id
            && string.Equals(_loadedCasePath, session.Path, StringComparison.OrdinalIgnoreCase))
        {
            return session;
        }
        await ReloadAsync(CancellationToken.None);
        if (session?.Path is not null)
        {
            StatusLine = "The open case changed, so the list was reloaded. Nothing was deleted or exported; select again.";
        }
        return null;
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync(CancellationToken ct)
    {
        // Log to the case this work started under, even if the examiner switches case before it ends.
        var custodyCase = Cinder.App.Services.ActiveCaseContext.Current;
        var b = Selected;
        if (await OpenCaseIfListMatchesAsync() is not { Path: not null } session)
        {
            return;
        }
        if (b is null)
        {
            StatusLine = "Select a bookmark first.";
            return;
        }
        if (b.CaseId != session.Id)
        {
            // Belt and braces: the row itself says which case it belongs to.
            StatusLine = "That bookmark belongs to a different case and was not deleted.";
            return;
        }
        try
        {
            var store = new BookmarkStore(new CaseStore(session.Path));
            if (await store.DeleteAsync(b.CaseId, b.Id, ct))
            {
                Bookmarks.Remove(b);
                await ActiveCaseContext.LogAsync(custodyCase, CustodyAction.Annotation, new
                {
                    Kind = "bookmark-deleted",
                    BookmarkId = b.Id,
                    b.Tool,
                    b.Title,
                    OriginalNote = b.Note,
                }, ct);
                StatusLine = $"Deleted bookmark #{b.Id}. The deletion is recorded in the custody log.";
            }
            else
            {
                Bookmarks.Remove(b);
                StatusLine = $"Bookmark #{b.Id} was already gone from the case file.";
            }
        }
        catch (Exception ex)
        {
            StatusLine = $"Could not delete the bookmark: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportAsync(string format, CancellationToken ct)
    {
        if (await OpenCaseIfListMatchesAsync() is null)
        {
            return;
        }
        var rows = Bookmarks.Select(b => (object)new
        {
            b.Id,
            CreatedUtc = b.CreatedUtc.ToString("u", System.Globalization.CultureInfo.InvariantCulture),
            b.Examiner,
            b.Tool,
            Evidence = b.EvidencePath ?? "",
            b.Title,
            Note = b.Note ?? "",
            Row = b.RowJson,
        }).ToList();

        if (await ToolExport.SaveRowsAsync(Id, "bookmarks", format, rows, ct) is { } status)
        {
            StatusLine = status;
        }
    }
}
