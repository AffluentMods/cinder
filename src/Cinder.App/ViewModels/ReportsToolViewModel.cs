using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Cinder.Reports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

/// <summary>UI for the report builder. Wraps <see cref="Cinder.Reports.ReportBuilder"/>.</summary>
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

    /// <summary>When set, <see cref="Build"/> appends an "Exhibits" section with one exhibit per bookmark.</summary>
    [ObservableProperty]
    private bool _includeBookmarks = true;

    [ObservableProperty]
    private string _bookmarkSummary = "No bookmarks loaded.";

    partial void OnIncludeBookmarksChanged(bool value) => Refresh();

    /// <summary>Reads the active case's bookmarks so they can become exhibits.</summary>
    [RelayCommand]
    private async Task LoadBookmarksAsync(CancellationToken ct)
    {
        var session = Services.ActiveCaseContext.Current;
        Bookmarks.Clear();
        if (session?.Path is null)
        {
            BookmarkSummary = "Open a case to load its bookmarks.";
            Refresh();
            return;
        }
        try
        {
            var store = new Cinder.Core.Cases.BookmarkStore(new Cinder.Core.Cases.CaseStore(session.Path));
            foreach (var b in await store.ListAsync(session.Id, ct))
            {
                Bookmarks.Add(b);
            }
            BookmarkSummary = Bookmarks.Count == 0
                ? "No bookmarks in this case yet — use \"Bookmark selected\" in any tool."
                : $"{Bookmarks.Count} bookmark{(Bookmarks.Count == 1 ? "" : "s")} from {session.Name}";
        }
        catch (Exception ex)
        {
            BookmarkSummary = $"Could not load bookmarks: {ex.Message}";
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
                        description.AppendLine($"- **{kv.Key}:** {kv.Value}");
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            description.AppendLine(b.RowJson);
        }
        description.AppendLine();
        description.AppendLine($"_Bookmarked by {b.Examiner} in {b.Tool} at {b.CreatedUtc:u}" +
                               (string.IsNullOrEmpty(b.EvidencePath) ? "_" : $" from `{b.EvidencePath}`_"));

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
    }

    partial void OnSelectedTemplateChanged(ReportTemplate value)
    {
        ApplyTemplate(value);
        Refresh();
    }

    private void ApplyTemplate(ReportTemplate template)
    {
        Sections.Clear();
        foreach (var name in template.DefaultSections)
        {
            Sections.Add(new DraftSection { Title = name, Body = "" });
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
        if (IncludeBookmarks && Bookmarks.Count > 0)
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
            var rb = Build();
            var exporter = new ReportExporter();
            var actual = await exporter.ExportAsync(rb, fmt, path);
            LastExportPath = actual;
            StatusLine = $"Exported to {actual}";

            await Services.ActiveCaseContext.LogAsync(Cinder.Core.Custody.CustodyAction.ReportExported, new
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
            StatusLine = $"Export failed: {ex.Message}";
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
