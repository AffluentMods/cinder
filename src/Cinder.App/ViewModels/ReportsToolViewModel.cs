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
        }).ConfigureAwait(false);
        var path = picked?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            var rb = Build();
            var exporter = new ReportExporter();
            var actual = await exporter.ExportAsync(rb, fmt, path).ConfigureAwait(false);
            LastExportPath = actual;
            StatusLine = $"Exported to {actual}";
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
