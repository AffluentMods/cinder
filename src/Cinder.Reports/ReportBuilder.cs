using System.Globalization;
using System.Text;

namespace Cinder.Reports;

/// <summary>
/// Stateful builder for a single report. Accumulates sections + exhibits; can render to
/// Markdown (canonical), HTML (Markdig-rendered, searchable), and JSON ("playbook" — re-runnable
/// per docs/plan.md §8.13).
///
/// PDF and DOCX exports are produced in-process by <see cref="ReportExporter"/> (QuestPDF and
/// the Open XML SDK). Only the plain-PDF format falls back to an external converter, and only
/// if QuestPDF fails.
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
        foreach (var e in ex)
        {
            // An exhibit handed back from RegisterExhibit is already indexed; adding it again
            // listed it twice in the Exhibit Index. Identity, not value equality: two distinct
            // exhibits may legitimately carry identical fields.
            if (!_allExhibits.Any(known => ReferenceEquals(known, e)))
            {
                _allExhibits.Add(e);
            }
        }
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

    /// <summary>
    /// The canonical Markdown form of the report. Section bodies and exhibit descriptions are
    /// Markdown by contract and pass through as written; every other field (titles, names,
    /// paths, hashes) is plain text and is escaped via <see cref="ReportMarkdown"/> so a value
    /// that came out of evidence cannot inject markup, forge an Exhibit Index cell or break out
    /// of its line.
    /// </summary>
    public string ToMarkdown()
    {
        static string E(string? s) => ReportMarkdown.EscapeInline(s);

        var sb = new StringBuilder();
        sb.AppendLine($"# {E(Title)}");
        sb.AppendLine();
        sb.AppendLine($"- **Case:** {E(CaseName)}");
        sb.AppendLine($"- **Examiner:** {E(Examiner)}");
        sb.AppendLine($"- **Generated:** {CreatedUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"- **Template:** {E(TemplateId)}");
        sb.AppendLine();
        foreach (var s in _sections)
        {
            sb.AppendLine($"## {E(s.Title)}");
            sb.AppendLine();
            sb.AppendLine(s.MarkdownBody);
            sb.AppendLine();
            foreach (var e in s.Exhibits)
            {
                sb.AppendLine($"### {E(e.Id)} — {E(e.Title)}");
                sb.AppendLine();
                if (!string.IsNullOrEmpty(e.Description)) sb.AppendLine(e.Description);
                if (!string.IsNullOrEmpty(e.FilePath)) sb.AppendLine($"- **File:** {ReportMarkdown.CodeSpan(e.FilePath)}");
                if (e.FileSize.HasValue) sb.AppendLine($"- **Size:** {e.FileSize:N0} bytes");
                if (!string.IsNullOrEmpty(e.Sha256)) sb.AppendLine($"- **SHA-256:** {ReportMarkdown.CodeSpan(e.Sha256)}");
                sb.AppendLine($"- **Captured:** {e.CapturedUtc:O} by {E(e.Examiner ?? Examiner)}");
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
                sb.AppendLine($"| {E(e.Id)} | {E(e.Title)} | {e.Kind} | {HashCell(e.Sha256)} | {e.CapturedUtc.ToString("u", CultureInfo.InvariantCulture)} |");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// A hash renders in a code span only when it is hex, as every hash Cinder computes is. Any
    /// other value is treated as untrusted text: a pipe inside a code span would still split
    /// the table row.
    /// </summary>
    private static string HashCell(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return "—";
        }
        return hash.All(char.IsAsciiHexDigit) ? $"`{hash}`" : ReportMarkdown.EscapeInline(hash);
    }

    /// <summary>
    /// A self-contained HTML page. Raw HTML in the Markdown is rendered as text, links and
    /// images are restricted (see <see cref="ReportMarkdown.ToSafeHtml"/>), the header fields
    /// are HTML-encoded, and a Content-Security-Policy forbids script and every external load —
    /// so opening the report, or printing it through a headless browser, cannot run code or
    /// fetch anything, whatever the evidence contained.
    /// </summary>
    public string ToHtml()
    {
        static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        var inner = ReportMarkdown.ToSafeHtml(ToMarkdown());
        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head>
            <meta charset="utf-8" />
            <meta http-equiv="Content-Security-Policy" content="{{ReportMarkdown.ContentSecurityPolicy}}" />
            <title>{{H(Title)}}</title>
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
            <footer>Generated by Cinder · case "{{H(CaseName)}}" · examiner {{H(Examiner)}} · {{CreatedUtc:u}}</footer>
            </body></html>
            """;
    }

    public string ToPlaybookJson() => System.Text.Json.JsonSerializer.Serialize(Build(),
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}
