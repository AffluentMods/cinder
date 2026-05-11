using System.Collections.ObjectModel;
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
    public override string Subtitle => "Recent cases, quick actions, and a guided start for new examiners.";
    public override string Phase => "0";
    public override string Kind => "dashboard";

    public override string HelpMarkdown => """
## What this is
The Home screen — your starting point in Cinder. From here you can create a new
case, jump back into a recent one, open a single piece of evidence without a case,
or walk through the "First time?" guide.

## When you'd use it
Every time you launch Cinder. The Home tab is always the first one selected so
you're never dropped into an empty parser screen wondering what to do.

## How it works
Recent cases appear on the left. Quick actions on the right cover the most common
starts: New Case, Open Case, Open Evidence (no case required, useful for a quick
look at a single file). The "First time?" panel walks you through Cinder's
mental model: case → evidence → parsers → timeline → report.
""";

    public ObservableCollection<RecentCaseRow> RecentCases { get; } = new();
    public ObservableCollection<RecentEvidenceRow> RecentEvidence { get; } = new();
    public ObservableCollection<GuideStep> Guide { get; } = new()
    {
        new("1", "Create a case",
            "A case is a folder Cinder keeps your evidence, your parsing results, and your " +
            "audit trail in. Every investigation starts with one. Click \"New case\" on the right."),
        new("2", "Add evidence",
            "Drop in a disk image (.dd, .E01), a memory dump, a registry hive, a single file " +
            "— anything. Cinder hashes it on import and records the action in the custody log."),
        new("3", "Open the right tool for the evidence",
            "The left rail is grouped by what you want to do: Examine (look at one thing), " +
            "Analyze (look at the case as a whole), Acquire (collect new evidence), " +
            "Case (manage the investigation)."),
        new("4", "Build a timeline",
            "When you've parsed enough artifacts, open Super-timeline. Every timestamped event " +
            "merges into one chronological view — usually where the story comes together."),
        new("5", "Export a report",
            "When you're done, Reports turns your findings into a court-ready PDF / DOCX, with " +
            "the chain of custody log appended automatically."),
    };

    /// <summary>True once at least one recent case/evidence row exists, used to swap empty states.</summary>
    public bool HasHistory => RecentCases.Count > 0 || RecentEvidence.Count > 0;

    public DashboardTool()
    {
        RecentCases.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));
        RecentEvidence.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));
    }

    /// <summary>
    /// Push a "just opened" case onto the top of the recent list. Caller is responsible for
    /// persistence; this is purely UI memory.
    /// </summary>
    public void NoteCaseOpened(Guid id, string name, string? examiner)
    {
        var existing = RecentCases.FirstOrDefault(r => r.Id == id);
        if (existing is not null)
        {
            RecentCases.Remove(existing);
        }
        RecentCases.Insert(0, new RecentCaseRow(id, name, examiner ?? "", DateTimeOffset.UtcNow));
        while (RecentCases.Count > 8)
        {
            RecentCases.RemoveAt(RecentCases.Count - 1);
        }
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
    }

    [RelayCommand]
    private void Clear()
    {
        RecentCases.Clear();
        RecentEvidence.Clear();
    }
}

/// <summary>One row in the Dashboard's "recent cases" pane.</summary>
public sealed record RecentCaseRow(Guid Id, string Name, string Examiner, DateTimeOffset OpenedUtc)
{
    public string OpenedDisplay => OpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

/// <summary>One row in the Dashboard's "recent evidence" pane.</summary>
public sealed record RecentEvidenceRow(string Path, string FileName, DateTimeOffset OpenedUtc)
{
    public string OpenedDisplay => OpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

/// <summary>One step in the "first time?" guide on the Dashboard.</summary>
public sealed record GuideStep(string Number, string Title, string Body);
