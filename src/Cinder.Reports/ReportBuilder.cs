using System.Globalization;
using System.Text;
using Markdig;

namespace Cinder.Reports;

/// <summary>
/// Stateful builder for a single report. Accumulates sections + exhibits; can render to
/// Markdown (canonical), HTML (Markdig-rendered, searchable), and JSON ("playbook" — re-runnable
/// per docs/plan.md §8.13).
///
/// PDF/A and DOCX exports go through the wkhtmltopdf binary and a docx-from-html templater
/// respectively (see <see cref="ReportExporter"/>); they're not pure-managed because PDF/A-2u
/// embedding is non-trivial and Cinder leans on a battle-tested external converter.
/// </summary>
public sealed class ReportBuilder
{
    private readonly List<ReportSection> _sections = new();
    private readonly List<Exhibit> _allExhibits = new();
    private int _exhibitCounter;

    public Guid Id { get; } = Guid.NewGuid();
    public string CaseName { get; }
    public string Examiner { get; }
    public string Title { get; }
    public string TemplateId { get; }
    public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;

    public ReportBuilder(string caseName, string examiner, string title, string templateId = "plain")
    {
        CaseName = caseName ?? throw new ArgumentNullException(nameof(caseName));
        Examiner = examiner ?? throw new ArgumentNullException(nameof(examiner));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        TemplateId = templateId;
    }

    public void AddSection(string title, string markdownBody, IEnumerable<Exhibit>? exhibits = null)
    {
        var ex = exhibits?.ToList() ?? [];
        foreach (var e in ex) _allExhibits.Add(e);
        _sections.Add(new ReportSection(title, markdownBody, ex));
    }

    public Exhibit RegisterExhibit(string title, ExhibitKind kind,
        string? description = null, string? filePath = null, long? fileSize = null,
        string? sha256 = null, string? examiner = null,
        IReadOnlyList<Cinder.Artifacts.IArtifact>? artifacts = null,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        _exhibitCounter++;
        var id = $"EX-{_exhibitCounter:D4}";
        var ex = new Exhibit(id, title, kind, description, filePath, fileSize, sha256,
            examiner ?? Examiner, DateTimeOffset.UtcNow, artifacts, properties);
        _allExhibits.Add(ex);
        return ex;
    }

    public Report Build() => new(Id, CaseName, Examiner, Title, CreatedUtc, TemplateId, _sections);

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {Title}");
        sb.AppendLine();
        sb.AppendLine($"- **Case:** {CaseName}");
        sb.AppendLine($"- **Examiner:** {Examiner}");
        sb.AppendLine($"- **Generated:** {CreatedUtc:O}");
        sb.AppendLine($"- **Template:** {TemplateId}");
        sb.AppendLine();
        foreach (var s in _sections)
        {
            sb.AppendLine($"## {s.Title}");
            sb.AppendLine();
            sb.AppendLine(s.MarkdownBody);
            sb.AppendLine();
            foreach (var e in s.Exhibits)
            {
                sb.AppendLine($"### {e.Id} — {e.Title}");
                sb.AppendLine();
                if (!string.IsNullOrEmpty(e.Description)) sb.AppendLine(e.Description);
                if (!string.IsNullOrEmpty(e.FilePath)) sb.AppendLine($"- **File:** `{e.FilePath}`");
                if (e.FileSize.HasValue) sb.AppendLine($"- **Size:** {e.FileSize:N0} bytes");
                if (!string.IsNullOrEmpty(e.Sha256)) sb.AppendLine($"- **SHA-256:** `{e.Sha256}`");
                sb.AppendLine($"- **Captured:** {e.CapturedUtc:O} by {e.Examiner ?? Examiner}");
                sb.AppendLine();
            }
        }

        if (_allExhibits.Count > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## Exhibit Index");
            sb.AppendLine();
            sb.AppendLine("| ID | Title | Type | Hash | Captured |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var e in _allExhibits)
            {
                sb.AppendLine($"| {e.Id} | {e.Title} | {e.Kind} | `{(e.Sha256 ?? "—")}` | {e.CapturedUtc.ToString("u", CultureInfo.InvariantCulture)} |");
            }
        }
        return sb.ToString();
    }

    public string ToHtml()
    {
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var inner = Markdown.ToHtml(ToMarkdown(), pipeline);
        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head>
            <meta charset="utf-8" />
            <title>{{Title}}</title>
            <style>
              body { font: 15px/1.6 Inter, -apple-system, system-ui, sans-serif; max-width: 880px; margin: 40px auto; padding: 0 24px; color: #16181D; }
              h1, h2, h3 { color: #FF7A1A; }
              code, pre { font-family: 'JetBrains Mono', Cascadia, monospace; }
              table { border-collapse: collapse; width: 100%; }
              th, td { border: 1px solid #ddd; padding: 6px 10px; text-align: left; }
              th { background: #FAFAFA; }
              footer { margin-top: 40px; padding-top: 16px; border-top: 1px solid #ddd; color: #888; font-size: 12px; }
            </style>
            </head><body>
            {{inner}}
            <footer>Generated by Cinder · case "{{CaseName}}" · examiner {{Examiner}} · {{CreatedUtc:u}}</footer>
            </body></html>
            """;
    }

    public string ToPlaybookJson() => System.Text.Json.JsonSerializer.Serialize(Build(),
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}
