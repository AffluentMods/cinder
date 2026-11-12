using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Cinder.Reports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

/// <summary>
/// UI for the report builder. Wraps <see cref="Cinder.Reports.ReportBuilder"/>.
///
/// <para>The bookmark list that becomes the Exhibits section belongs to one case. It reloads
/// when the active case changes, when the Reports tool is opened and immediately before every
/// export, and a list loaded from one case is never written into a report for another.</para>
/// </summary>
public sealed partial class ReportsToolViewModel : ViewModelBase
{
    private readonly Func<string> _activeCaseAccessor;

    public IReadOnlyList<ReportTemplate> Templates => ReportTemplates.All;

    [ObservableProperty]
    private ReportTemplate _selectedTemplate;

    [ObservableProperty]
    private string _title = "Untitled report";

    [ObservableProperty]
    private string _examiner = Environment.UserName;

    public ObservableCollection<DraftSection> Sections { get; } = new();

    [ObservableProperty]
    private DraftSection? _selectedSection;

    [ObservableProperty]
    private string _previewMarkdown = "";

    [ObservableProperty]
    private string? _lastExportPath;

    [ObservableProperty]
    private string? _statusLine;

    /// <summary>Bookmarks loaded from the active case, in creation order.</summary>
    public ObservableCollection<Cinder.Core.Cases.Bookmark> Bookmarks { get; } = new();

    /// <summary>The case <see cref="Bookmarks"/> was loaded from. Null when nothing is loaded.</summary>
    private Guid? _bookmarksCaseId;
    private int _bookmarkLoadSerial;

    /// <summary>True when the loaded bookmarks belong to the case that is open now.</summary>
    private bool BookmarksAreForOpenCase =>
        _bookmarksCaseId is { } id && Services.ActiveCaseContext.Current?.Id == id;

    /// <summary>When set, <see cref="Build"/> appends an "Exhibits" section with one exhibit per bookmark.</summary>
    [ObservableProperty]
    private bool _includeBookmarks = true;

    [ObservableProperty]
    private string _bookmarkSummary = "No bookmarks loaded.";

    partial void OnIncludeBookmarksChanged(bool value) => Refresh();

    /// <summary>Reads the active case's bookmarks so they can become exhibits.</summary>
    [RelayCommand]
    private Task LoadBookmarksAsync(CancellationToken ct) => SyncBookmarksAsync(ct);

    /// <summary>
    /// Replaces <see cref="Bookmarks"/> with the open case's bookmarks (or nothing, when no case
    /// is open) and refreshes the preview. A slower earlier load cannot overwrite a later one.
    /// </summary>
    internal async Task SyncBookmarksAsync(CancellationToken ct)
    {
        var serial = ++_bookmarkLoadSerial;
        var session = Services.ActiveCaseContext.Current;
        Bookmarks.Clear();
        _bookmarksCaseId = null;
        if (session?.Path is null)
        {
            BookmarkSummary = "Open a case to add its bookmarks as exhibits.";
            Refresh();
            return;
        }
        try
        {
            var store = new Cinder.Core.Cases.BookmarkStore(new Cinder.Core.Cases.CaseStore(session.Path));
            var list = await store.ListAsync(session.Id, ct);
            if (serial != _bookmarkLoadSerial)
            {
                return;
            }
            foreach (var b in list)
            {
                Bookmarks.Add(b);
            }
            _bookmarksCaseId = session.Id;
            BookmarkSummary = Bookmarks.Count == 0
                ? $"No bookmarks in {session.Name} yet. Select a row in any tool and choose Bookmark."
                : $"{Bookmarks.Count} bookmark{(Bookmarks.Count == 1 ? "" : "s")} from {session.Name}";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (serial != _bookmarkLoadSerial)
            {
                return;
            }
            BookmarkSummary = $"Could not load the bookmarks of {session.Name}: {ex.Message}";
        }
        Refresh();
    }

    /// <summary>Turns a bookmark into an exhibit: the note as the description, the row as properties.</summary>
    private static Exhibit ToExhibit(Cinder.Core.Cases.Bookmark b, int ordinal, string examiner)
    {
        IReadOnlyDictionary<string, string>? props = null;
        var description = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(b.Note))
        {
            description.AppendLine(b.Note.Trim());
            description.AppendLine();
        }
        try
        {
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(b.RowJson);
            if (dict is not null)
            {
                props = dict.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!);
                foreach (var kv in dict)
                {
                    if (!string.IsNullOrEmpty(kv.Value))
                    {
                        // Values come from evidence: escaped so a crafted file name or page
                        // title cannot add headings, tables or links to the report.
                        description.AppendLine($"- **{ReportMarkdown.EscapeInline(kv.Key)}:** {ReportMarkdown.EscapeInline(kv.Value)}");
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            description.AppendLine(ReportMarkdown.EscapeInline(b.RowJson));
        }
        description.AppendLine();
        description.AppendLine($"_Bookmarked by {ReportMarkdown.EscapeInline(b.Examiner ?? "")} in {ReportMarkdown.EscapeInline(b.Tool ?? "")} at {b.CreatedUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC" +
                               (string.IsNullOrEmpty(b.EvidencePath) ? "_" : $" from {ReportMarkdown.CodeSpan(b.EvidencePath)}_"));

        return new Exhibit(
            Id: $"EX-{ordinal:D4}",
            Title: b.Title,
            Kind: ExhibitKind.ArtifactSet,
            Description: description.ToString(),
            FilePath: b.EvidencePath,
            FileSize: null,
            Sha256: null,
            Examiner: b.Examiner ?? examiner,
            CapturedUtc: b.CreatedUtc,
            Properties: props);
    }

    public ReportsToolViewModel(Func<string> activeCaseAccessor)
    {
        _activeCaseAccessor = activeCaseAccessor ?? throw new ArgumentNullException(nameof(activeCaseAccessor));
        _selectedTemplate = ReportTemplates.All[0];
        ApplyTemplate(_selectedTemplate);
        Refresh();

        // Exhibits follow the open case: reload when it changes, and when the Reports tool is
        // opened (bookmarks may have been added in other tools since).
        Services.ActiveCaseContext.Changed += OnActiveCaseChanged;
        Tools.ToolViewModel.ToolSelected += tool =>
        {
            if (tool.Kind == "reports")
            {
                _ = SyncBookmarksAsync(CancellationToken.None);
            }
        };
        if (Services.ActiveCaseContext.Current is not null)
        {
            _ = SyncBookmarksAsync(CancellationToken.None);
        }
    }

    private void OnActiveCaseChanged(CaseSession? session) => _ = SyncBookmarksAsync(CancellationToken.None);

    partial void OnSelectedTemplateChanged(ReportTemplate value)
    {
        ApplyTemplate(value);
        Refresh();
    }

    /// <summary>
    /// Switches to a template's sections without throwing away what the examiner wrote. A
    /// section whose title exists in the new template keeps its text there; any other section
    /// that has text is carried over after the template's own.
    /// </summary>
    private void ApplyTemplate(ReportTemplate template)
    {
        var written = Sections.Where(s => !string.IsNullOrWhiteSpace(s.Body)).ToList();
        Sections.Clear();
        foreach (var name in template.DefaultSections)
        {
            var kept = written.FirstOrDefault(s => string.Equals(s.Title, name, StringComparison.OrdinalIgnoreCase));
            if (kept is not null)
            {
                written.Remove(kept);
            }
            Sections.Add(new DraftSection { Title = name, Body = kept?.Body ?? "" });
        }
        foreach (var extra in written)
        {
            Sections.Add(extra);
        }
        if (Sections.Count == 0)
        {
            Sections.Add(new DraftSection { Title = "Notes", Body = "" });
        }
        SelectedSection = Sections[0];
    }

    [RelayCommand]
    private void AddSection()
    {
        var s = new DraftSection { Title = $"Section {Sections.Count + 1}", Body = "" };
        Sections.Add(s);
        SelectedSection = s;
        Refresh();
    }

    [RelayCommand]
    private void RemoveSection(DraftSection? section)
    {
        if (section is null) return;
        Sections.Remove(section);
        SelectedSection = Sections.Count > 0 ? Sections[0] : null;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var rb = Build();
        PreviewMarkdown = rb.ToMarkdown();
    }

    private ReportBuilder Build()
    {
        var rb = new ReportBuilder(_activeCaseAccessor(), Examiner, Title, SelectedTemplate.Id);
        foreach (var s in Sections)
        {
            rb.AddSection(s.Title, s.Body);
        }
        // Only bookmarks loaded from the case this report is for. A list left over from
        // another case is ignored here and replaced as soon as the reload finishes.
        if (IncludeBookmarks && Bookmarks.Count > 0 && BookmarksAreForOpenCase)
        {
            // Exhibits are constructed directly and handed to AddSection, which registers them
            // in the index once; RegisterExhibit + AddSection would list each twice.
            var exhibits = Bookmarks.Select((b, i) => ToExhibit(b, i + 1, Examiner)).ToList();
            rb.AddSection("Exhibits",
                $"{exhibits.Count} finding{(exhibits.Count == 1 ? "" : "s")} bookmarked during examination, in the order they were recorded.",
                exhibits);
        }
        return rb;
    }

    [RelayCommand]
    private async Task ExportAsync(string format)
    {
        // Log to the case this work started under, even if the examiner switches case before it ends.
        var custodyCase = Cinder.App.Services.ActiveCaseContext.Current;
        if (string.IsNullOrEmpty(format))
        {
            return;
        }
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;

        var (extension, fmt) = format switch
        {
            "html" => (".html", ReportFormat.Html),
            "pdf" => (".pdf", ReportFormat.PdfA),
            "json" => (".json", ReportFormat.JsonPlaybook),
            "docx" => (".docx", ReportFormat.Docx),
            _ => (".md", ReportFormat.Markdown),
        };

        var picked = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export report",
            DefaultExtension = extension.TrimStart('.'),
            SuggestedFileName = $"report{extension}",
        });
        var path = picked?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            // Read the bookmarks again from the open case, so the export reflects the case file
            // as it is now and can never carry another case's findings.
            await SyncBookmarksAsync(CancellationToken.None);
            var rb = Build();
            var exporter = new ReportExporter();
            var actual = await exporter.ExportAsync(rb, fmt, path);
            LastExportPath = actual;
            StatusLine = $"Exported to {actual}";

            await Services.ActiveCaseContext.LogAsync(custodyCase, Cinder.Core.Custody.CustodyAction.ReportExported, new
            {
                Title,
                Template = SelectedTemplate.Id,
                Format = fmt.ToString(),
                Path = actual,
                Sections = Sections.Select(s => s.Title).ToArray(),
                Examiner,
            });
        }
        catch (Exception ex)
        {
            StatusLine = $"Could not export the report: {ex.Message}";
        }
    }
}

public sealed partial class DraftSection : ViewModelBase
{
    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _body = "";
}
