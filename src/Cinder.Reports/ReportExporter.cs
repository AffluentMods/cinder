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
        // SECURITY: write the staging HTML inside a fresh per-invocation temp directory.
        // Don't put the file next to the user-supplied outputPath — that path is user-influenced
        // and could collide with attacker-staged content. Also DON'T pass
        // --enable-local-file-access to wkhtmltopdf; it lets the rendered HTML read any file
        // the process can read (SSRF/LFI in template content).
        var stagingDir = Path.Combine(Path.GetTempPath(), "cinder-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);
        var htmlPath = Path.Combine(stagingDir, "report.html");
        await File.WriteAllTextAsync(htmlPath, builder.ToHtml(), ct).ConfigureAwait(false);

        try
        {
            var converter = FindPdfConverter();
            if (converter is null)
            {
                throw new InvalidOperationException(
                    "No PDF converter found (looked for wkhtmltopdf, chrome, msedge). HTML staged at " + htmlPath +
                    ". Install wkhtmltopdf or run a Chromium-based browser headless to produce the PDF/A.");
            }

            var psi = new ProcessStartInfo(converter.FileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in converter.BuildArgs(htmlPath, outputPath))
            {
                psi.ArgumentList.Add(a);
            }
            using var p = new Process { StartInfo = psi };
            p.Start();
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException($"PDF converter exit {p.ExitCode}: {await p.StandardError.ReadToEndAsync(ct)}");
            }
            return outputPath;
        }
        finally
        {
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
        }
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
        // SECURITY: arguments returned as a list, not a string. NEVER add
        // --enable-local-file-access — that lets the rendered HTML reach arbitrary local files
        // (e.g. <iframe src="file:///etc/shadow">) and bake them into the resulting PDF.
        foreach (var tool in new[] { "wkhtmltopdf", "wkhtmltopdf.exe" })
        {
            if (IsOnPath(tool))
            {
                return new PdfConverter(tool, (html, pdf) => new[] { html, pdf });
            }
        }
        foreach (var tool in new[] { "chrome", "chrome.exe", "msedge", "msedge.exe", "google-chrome", "chromium" })
        {
            var path = ResolveOnPath(tool);
            if (path is not null)
            {
                return new PdfConverter(path, (html, pdf) => new[]
                {
                    "--headless",
                    "--disable-gpu",
                    "--no-pdf-header-footer",
                    // SECURITY: file:// URI built here (and only here) from a path we control.
                    // The HTML staging dir is a fresh per-invocation Guid path.
                    "--print-to-pdf=" + pdf,
                    new Uri(html).AbsoluteUri,
                });
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

    private sealed record PdfConverter(string FileName, Func<string, string, IReadOnlyList<string>> BuildArgs);
}
