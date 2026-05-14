using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Cinder.Reports;

/// <summary>
/// In-process PDF rendering for a <see cref="Report"/>. Built on top of QuestPDF so reports
/// generate without an external converter (no wkhtmltopdf, no Chromium headless). The layout
/// is intentionally court-clean: title page → section bodies → exhibit index → footer with case
/// metadata on every page.
///
/// We render the Markdown body of each section into plain paragraphs/lists rather than
/// hand-implementing a full Markdown renderer in QuestPDF. The result is reliable across every
/// template; rich Markdown features (tables, embedded HTML, fenced code) degrade gracefully to
/// plain text. A future iteration can lift the HTML render path through QuestPDF's HTML
/// extension when that ships.
/// </summary>
internal sealed class QuestPdfDocument : IDocument
{
    private readonly Report _report;
    private readonly string _markdown;

    private QuestPdfDocument(Report report, string markdown)
    {
        _report = report;
        _markdown = markdown;
    }

    public static QuestPdfDocument Create(Report report, string markdown) => new(report, markdown);

    public DocumentMetadata GetMetadata() => new()
    {
        Title = _report.Title,
        Author = _report.Examiner,
        Subject = $"Cinder report — case {_report.CaseName}",
        Creator = "Cinder · github.com/AffluentMods/cinder",
        Producer = "Cinder · QuestPDF",
        Keywords = "forensics,cinder,report",
        CreationDate = _report.CreatedUtc,
        ModifiedDate = _report.CreatedUtc,
        Language = "en-US",
    };

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Margin(40);
            page.Size(PageSizes.Letter);
            page.DefaultTextStyle(x => x.FontFamily("Helvetica").FontSize(11).LineHeight(1.35f));
            page.PageColor(Colors.White);

            page.Header().Element(ComposeHeader);
            page.Content().Element(ComposeContent);
            page.Footer().Element(ComposeFooter);
        });
    }

    private void ComposeHeader(IContainer container)
    {
        container.PaddingBottom(8).BorderBottom(0.75f).BorderColor(Colors.Grey.Lighten2).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text(_report.Title).FontSize(13).SemiBold();
                col.Item().Text($"Case · {_report.CaseName}")
                    .FontSize(9).FontColor(Colors.Grey.Darken1);
            });
            row.ConstantItem(170).AlignRight().Column(col =>
            {
                col.Item().Text("CINDER")
                    .FontSize(9).LetterSpacing(0.2f).FontColor("#FF7A1A").SemiBold();
                col.Item().Text(_report.CreatedUtc.ToString("u", CultureInfo.InvariantCulture))
                    .FontSize(9).FontColor(Colors.Grey.Darken1);
            });
        });
    }

    private void ComposeContent(IContainer container)
    {
        container.PaddingVertical(14).Column(col =>
        {
            col.Spacing(10);

            // Cover / metadata block.
            col.Item().Element(c => c.PaddingBottom(8).Column(meta =>
            {
                meta.Spacing(2);
                meta.Item().Text(_report.Title).FontSize(22).SemiBold();
                meta.Item().Text($"Template · {ResolveTemplateName(_report.TemplateId)}")
                    .FontSize(10).FontColor(Colors.Grey.Darken2);
                meta.Item().PaddingTop(8).Row(r =>
                {
                    r.RelativeItem().Column(lhs =>
                    {
                        lhs.Spacing(1);
                        lhs.Item().Text("CASE").FontSize(8).LetterSpacing(0.15f).FontColor(Colors.Grey.Darken1);
                        lhs.Item().Text(_report.CaseName).FontSize(11);
                    });
                    r.RelativeItem().Column(rhs =>
                    {
                        rhs.Spacing(1);
                        rhs.Item().Text("EXAMINER").FontSize(8).LetterSpacing(0.15f).FontColor(Colors.Grey.Darken1);
                        rhs.Item().Text(_report.Examiner).FontSize(11);
                    });
                    r.RelativeItem().Column(rt =>
                    {
                        rt.Spacing(1);
                        rt.Item().Text("CREATED (UTC)").FontSize(8).LetterSpacing(0.15f).FontColor(Colors.Grey.Darken1);
                        rt.Item().Text(_report.CreatedUtc.ToString("u", CultureInfo.InvariantCulture)).FontSize(11);
                    });
                });
            }));

            // Sections.
            foreach (var section in _report.Sections)
            {
                col.Item().Element(c => RenderSection(c, section));
            }

            // Exhibit index — drawn from every section's exhibit list.
            var allExhibits = _report.Sections.SelectMany(s => s.Exhibits).ToList();
            if (allExhibits.Count > 0)
            {
                col.Item().PaddingTop(12).Element(c => RenderExhibitIndex(c, allExhibits));
            }
        });
    }

    private static void RenderSection(IContainer container, ReportSection section)
    {
        container.PaddingTop(6).Column(col =>
        {
            col.Spacing(4);
            col.Item().Text(section.Title).FontSize(15).SemiBold().FontColor("#FF7A1A");
            // Markdown body: render as a sequence of paragraphs / bullet lines. We don't
            // attempt to render fenced code, links, or tables — the canonical Markdown text
            // is exported separately for that.
            foreach (var block in PlainParagraphs(section.MarkdownBody))
            {
                col.Item().Text(block).FontSize(11);
            }
            // Per-section exhibits (if any) get a compact box.
            foreach (var e in section.Exhibits)
            {
                col.Item().PaddingTop(4)
                    .Background(Colors.Grey.Lighten4)
                    .Border(0.5f).BorderColor(Colors.Grey.Lighten2)
                    .Padding(8)
                    .Column(c =>
                    {
                        c.Spacing(1);
                        c.Item().Text($"{e.Id} · {e.Title}").SemiBold().FontSize(10.5f);
                        if (!string.IsNullOrEmpty(e.Description))
                        {
                            c.Item().Text(e.Description).FontSize(10);
                        }
                        if (!string.IsNullOrEmpty(e.FilePath))
                        {
                            c.Item().Text(t =>
                            {
                                t.Span("File · ").FontSize(9).FontColor(Colors.Grey.Darken1);
                                t.Span(e.FilePath!).FontFamily("Courier").FontSize(9);
                            });
                        }
                        if (!string.IsNullOrEmpty(e.Sha256))
                        {
                            c.Item().Text(t =>
                            {
                                t.Span("SHA-256 · ").FontSize(9).FontColor(Colors.Grey.Darken1);
                                t.Span(e.Sha256!).FontFamily("Courier").FontSize(9);
                            });
                        }
                        c.Item().Text($"Captured {e.CapturedUtc:u} by {e.Examiner}")
                            .FontSize(9).FontColor(Colors.Grey.Darken1);
                    });
            }
        });
    }

    private static void RenderExhibitIndex(IContainer container, IReadOnlyList<Exhibit> exhibits)
    {
        container.Column(col =>
        {
            col.Spacing(4);
            col.Item().Text("Exhibit Index").FontSize(15).SemiBold().FontColor("#FF7A1A");
            col.Item().Border(0.5f).BorderColor(Colors.Grey.Lighten2).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(60);
                    c.RelativeColumn(3);
                    c.RelativeColumn(2);
                    c.RelativeColumn(2);
                    c.RelativeColumn(3);
                });
                table.Header(h =>
                {
                    static IContainer HeaderCell(IContainer cell) =>
                        cell.Background(Colors.Grey.Lighten3).PaddingVertical(4).PaddingHorizontal(6);
                    h.Cell().Element(HeaderCell).Text("ID").FontSize(9).SemiBold();
                    h.Cell().Element(HeaderCell).Text("Title").FontSize(9).SemiBold();
                    h.Cell().Element(HeaderCell).Text("Type").FontSize(9).SemiBold();
                    h.Cell().Element(HeaderCell).Text("Captured").FontSize(9).SemiBold();
                    h.Cell().Element(HeaderCell).Text("SHA-256").FontSize(9).SemiBold();
                });
                foreach (var e in exhibits)
                {
                    static IContainer BodyCell(IContainer cell) =>
                        cell.BorderTop(0.5f).BorderColor(Colors.Grey.Lighten3).PaddingVertical(3).PaddingHorizontal(6);
                    table.Cell().Element(BodyCell).Text(e.Id).FontSize(9);
                    table.Cell().Element(BodyCell).Text(e.Title).FontSize(9);
                    table.Cell().Element(BodyCell).Text(e.Kind.ToString()).FontSize(9);
                    table.Cell().Element(BodyCell).Text(e.CapturedUtc.ToString("u", CultureInfo.InvariantCulture)).FontSize(9);
                    table.Cell().Element(BodyCell).Text(e.Sha256 ?? "—").FontFamily("Courier").FontSize(8);
                }
            });
        });
    }

    private void ComposeFooter(IContainer container)
    {
        container.PaddingTop(8).BorderTop(0.5f).BorderColor(Colors.Grey.Lighten2).Row(row =>
        {
            row.RelativeItem().Text($"Cinder · {_report.CaseName} · {_report.Examiner}")
                .FontSize(8).FontColor(Colors.Grey.Darken1);
            row.ConstantItem(140).AlignRight().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken1));
                t.Span("Page ");
                t.CurrentPageNumber();
                t.Span(" of ");
                t.TotalPages();
            });
        });
    }

    private static IEnumerable<string> PlainParagraphs(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) yield break;
        var paragraph = new System.Text.StringBuilder();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (paragraph.Length > 0)
                {
                    yield return paragraph.ToString().Trim();
                    paragraph.Clear();
                }
                continue;
            }
            // Bullet / numbered list → emit as separate lines prefixed by •.
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                if (paragraph.Length > 0)
                {
                    yield return paragraph.ToString().Trim();
                    paragraph.Clear();
                }
                yield return "  •  " + line[2..].Trim();
                continue;
            }
            // Headings inside section bodies — emit as a bold-feel paragraph (just text).
            if (line.StartsWith("# ", StringComparison.Ordinal) ||
                line.StartsWith("## ", StringComparison.Ordinal) ||
                line.StartsWith("### ", StringComparison.Ordinal))
            {
                if (paragraph.Length > 0)
                {
                    yield return paragraph.ToString().Trim();
                    paragraph.Clear();
                }
                yield return line.TrimStart('#', ' ');
                continue;
            }
            paragraph.AppendLine(line);
        }
        if (paragraph.Length > 0)
        {
            yield return paragraph.ToString().Trim();
        }
    }

    private static string ResolveTemplateName(string id) =>
        ReportTemplates.All.FirstOrDefault(t => t.Id == id)?.Name ?? id;
}
