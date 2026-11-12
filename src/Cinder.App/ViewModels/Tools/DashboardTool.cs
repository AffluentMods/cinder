using System.Collections.ObjectModel;
using Cinder.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels.Tools;

/// <summary>
/// The home screen. First thing the user sees when Cinder launches. Surfaces recent cases,
/// recent evidence, suggested next steps for beginners, and quick-action buttons that just
/// invoke the same commands as the title bar / menu / palette.
/// </summary>
public sealed partial class DashboardTool : ToolViewModel
{
    public override string Id => "dashboard";
    public override string Title => "Home";
    public override string Icon => "⌂";
    public override string Subtitle => "Reopen a recent case or file, start a new case, and read a short guide to how the tools fit together.";
    public override string Phase => "0";
    public override string Kind => "dashboard";

    public override string HelpMarkdown => """
## What this is
The Home screen, shown when Cinder starts. From here you can create a case, reopen a
recent case or a recently opened file, open a single file without a case, or read the
short "First time?" guide.

## How it works
Recent cases and recently opened files are listed so you can return to them with one
click. The quick actions cover the usual ways to start: new case, open case, open a
file. The guide describes the order most examinations follow: case, evidence, tools,
timeline, report.

## Good to know
You do not need a case to look at a file. A case is needed for bookmarks and for the
custody log: without one, nothing you do is recorded.
""";

    public ObservableCollection<RecentCaseRow> RecentCases { get; } = new();
    public ObservableCollection<RecentEvidenceRow> RecentEvidence { get; } = new();
    public ObservableCollection<GuideStep> Guide { get; } = new()
    {
        new("1", "Create a case",
            "A case is one file that holds your bookmarks and a log of what you did. " +
            "Click \"New case\" and choose where to save it. Without an open case, " +
            "nothing is logged and rows cannot be bookmarked."),
        new("2", "Open your evidence",
            "Cinder does not import evidence into the case. Each tool asks for the file or " +
            "folder it reads: a disk image, a registry hive, an event log, a folder of " +
            "exported files. \"Open file\" shows any file in the Hex Viewer."),
        new("3", "Pick the tool for the evidence",
            "The left rail is grouped by what you want to do: Examine (look at one piece of " +
            "evidence), Analyze (look across evidence), Acquire (make or check an image), " +
            "Case (bookmarks, reports and the custody log)."),
        new("4", "Bookmark what matters and build a timeline",
            "Select a row in any tool and choose Bookmark to keep it as a finding. In " +
            "Super-timeline, \"Ingest folder\" reads the timestamped events from a folder of " +
            "evidence into one list in time order."),
        new("5", "Export a report",
            "Reports lets you write sections from a template, adds your bookmarks as numbered " +
            "exhibits, and exports Markdown, HTML, PDF, Word or JSON. Each export is recorded " +
            "in the case's custody log; the log itself is viewed and checked in Chain of custody."),
    };

    /// <summary>True once at least one recent case/evidence row exists, used to swap empty states.</summary>
    public bool HasHistory => RecentCases.Count > 0 || RecentEvidence.Count > 0;

    private RecentsStore? _store;

    public DashboardTool()
    {
        RecentCases.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));
        RecentEvidence.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));
    }

    /// <summary>
    /// Attach a backing <see cref="RecentsStore"/> and hydrate from disk. Subsequent
    /// NoteCaseOpened / NoteEvidenceOpened / Clear calls persist back automatically.
    /// </summary>
    public void AttachStore(RecentsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        var snap = store.Load();
        RecentCases.Clear();
        foreach (var c in snap.Cases)
        {
            RecentCases.Add(new RecentCaseRow(c.Id, c.Name, c.Examiner, c.Path, c.OpenedUtc));
        }
        RecentEvidence.Clear();
        foreach (var e in snap.Evidence)
        {
            var name = System.IO.Path.GetFileName(e.Path);
            if (string.IsNullOrEmpty(name))
            {
                name = e.Path;
            }
            RecentEvidence.Add(new RecentEvidenceRow(e.Path, name, e.OpenedUtc));
        }
        OnPropertyChanged(nameof(HasHistory));
    }

    private void Persist()
    {
        _store?.Save(new RecentsSnapshot
        {
            Cases = RecentCases
                .Select(r => new RecentCaseEntry(r.Id, r.Name, r.Examiner, r.Path ?? "", r.OpenedUtc))
                .ToList(),
            Evidence = RecentEvidence
                .Select(r => new RecentEvidenceEntry(r.Path, r.OpenedUtc))
                .ToList(),
        });
    }

    /// <summary>
    /// Push a "just opened" case onto the top of the recent list. The <paramref name="path"/>
    /// is the .cinder file path; without it we can't reopen the case from the dashboard.
    /// </summary>
    public void NoteCaseOpened(Guid id, string name, string? examiner, string? path)
    {
        var existing = RecentCases.FirstOrDefault(r => r.Id == id);
        if (existing is not null)
        {
            RecentCases.Remove(existing);
        }
        RecentCases.Insert(0, new RecentCaseRow(id, name, examiner ?? "", path, DateTimeOffset.UtcNow));
        while (RecentCases.Count > 8)
        {
            RecentCases.RemoveAt(RecentCases.Count - 1);
        }
        Persist();
    }

    public void NoteEvidenceOpened(string path)
    {
        var existing = RecentEvidence.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentEvidence.Remove(existing);
        }
        var name = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
        {
            name = path;
        }
        RecentEvidence.Insert(0, new RecentEvidenceRow(path, name, DateTimeOffset.UtcNow));
        while (RecentEvidence.Count > 8)
        {
            RecentEvidence.RemoveAt(RecentEvidence.Count - 1);
        }
        Persist();
    }

    [RelayCommand]
    private void Clear()
    {
        RecentCases.Clear();
        RecentEvidence.Clear();
        Persist();
    }
}

/// <summary>
/// One row in the Dashboard's "recent cases" pane. <see cref="Path"/> is the .cinder file
/// path — required to reopen the case from the dashboard.
/// </summary>
public sealed record RecentCaseRow(Guid Id, string Name, string Examiner, string? Path, DateTimeOffset OpenedUtc)
{
    public string OpenedDisplay => OpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>True only when we have a path to reopen from — drives IsEnabled on the click button.</summary>
    public bool CanReopen => !string.IsNullOrEmpty(Path);
}

/// <summary>One row in the Dashboard's "recent evidence" pane.</summary>
public sealed record RecentEvidenceRow(string Path, string FileName, DateTimeOffset OpenedUtc)
{
    public string OpenedDisplay => OpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

/// <summary>One step in the "first time?" guide on the Dashboard.</summary>
public sealed record GuideStep(string Number, string Title, string Body);
