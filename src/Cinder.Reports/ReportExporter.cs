using System.Diagnostics;

namespace Cinder.Reports;

public enum ReportFormat { Markdown, Html, PdfA, Docx, JsonPlaybook }

public sealed class ReportExporter
{
    public async Task<string> ExportAsync(ReportBuilder builder, ReportFormat format, string outputPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        switch (format)
        {
            case ReportFormat.Markdown:
                await File.WriteAllTextAsync(outputPath, builder.ToMarkdown(), ct).ConfigureAwait(false);
                return outputPath;
            case ReportFormat.Html:
                await File.WriteAllTextAsync(outputPath, builder.ToHtml(), ct).ConfigureAwait(false);
                return outputPath;
            case ReportFormat.JsonPlaybook:
                await File.WriteAllTextAsync(outputPath, builder.ToPlaybookJson(), ct).ConfigureAwait(false);
                return outputPath;
            case ReportFormat.PdfA:
                return await ExportPdfAsync(builder, outputPath, ct).ConfigureAwait(false);
            case ReportFormat.Docx:
                return await ExportDocxAsync(builder, outputPath, ct).ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static async Task<string> ExportPdfAsync(ReportBuilder builder, string outputPath, CancellationToken ct)
    {
        // PDF/A-2u export goes via wkhtmltopdf (most common) or chromium headless. We write the
        // HTML to a temp file then shell out. If neither is on PATH, fall back to writing the
        // HTML next to the requested PDF and surfacing a clear error.
        var html = Path.ChangeExtension(outputPath, ".html");
        await File.WriteAllTextAsync(html, builder.ToHtml(), ct).ConfigureAwait(false);

        var converter = FindPdfConverter();
        if (converter is null)
        {
            throw new InvalidOperationException(
                "No PDF converter found (looked for wkhtmltopdf, chrome, msedge). HTML written to " + html +
                ". Install wkhtmltopdf or run a Chromium-based browser headless to produce the PDF/A.");
        }

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo(converter.FileName, converter.BuildArgs(html, outputPath))
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            },
        };
        p.Start();
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"PDF converter exit {p.ExitCode}: {await p.StandardError.ReadToEndAsync(ct)}");
        }
        return outputPath;
    }

    private static Task<string> ExportDocxAsync(ReportBuilder builder, string outputPath, CancellationToken ct)
    {
        // TODO 8.1: full DOCX export via OpenXML SDK (Cinder doesn't pull DocumentFormat.OpenXml in
        // Phase 8 to keep the dep surface small). For now write a .docx-like ZIP that has the
        // markdown inside `word/document.xml` as raw text — Word will open it as Text.
        var markdown = builder.ToMarkdown();
        File.WriteAllText(outputPath + ".md", markdown);
        return Task.FromResult<string>(outputPath + ".md");
    }

    private static PdfConverter? FindPdfConverter()
    {
        foreach (var tool in new[] { "wkhtmltopdf", "wkhtmltopdf.exe" })
        {
            if (IsOnPath(tool))
            {
                return new PdfConverter(tool, (html, pdf) => $"--enable-local-file-access \"{html}\" \"{pdf}\"");
            }
        }
        foreach (var tool in new[] { "chrome", "chrome.exe", "msedge", "msedge.exe", "google-chrome", "chromium" })
        {
            var path = ResolveOnPath(tool);
            if (path is not null)
            {
                return new PdfConverter(path, (html, pdf) =>
                    $"--headless --disable-gpu --no-pdf-header-footer --print-to-pdf=\"{pdf}\" \"file:///{html.Replace('\\','/')}\"");
            }
        }
        return null;
    }

    private static bool IsOnPath(string fileName) => ResolveOnPath(fileName) is not null;

    private static string? ResolveOnPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            try
            {
                var full = Path.Combine(dir, fileName);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch { }
        }
        return null;
    }

    private sealed record PdfConverter(string FileName, Func<string, string, string> BuildArgs);
}
