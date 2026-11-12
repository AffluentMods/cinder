using System.Diagnostics;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Cinder.Core.Diagnostics;

namespace Cinder.Reports;

/// <summary>
/// Output formats. <see cref="PdfA"/> is a real PDF/A-2b file or an error — it never degrades
/// to an ordinary PDF. <see cref="Pdf"/> is an ordinary PDF and may be produced by an external
/// converter if in-process rendering fails. New members are appended so existing numeric
/// values keep their meaning.
/// </summary>
public enum ReportFormat { Markdown, Html, PdfA, Docx, JsonPlaybook, Pdf }

public sealed class ReportExporter
{
    public async Task<string> ExportAsync(ReportBuilder builder, ReportFormat format, string outputPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        // GetDirectoryName of a bare file name is "" and CreateDirectory("") throws.
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

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
                return await ExportPdfAAsync(builder, outputPath, ct).ConfigureAwait(false);
            case ReportFormat.Pdf:
                return await ExportPdfAsync(builder, outputPath, ct).ConfigureAwait(false);
            case ReportFormat.Docx:
                return await ExportDocxAsync(builder, outputPath, ct).ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>
    /// PDF/A-2b via QuestPDF, in-process. There is deliberately no fallback: neither wkhtmltopdf
    /// nor headless Chromium writes PDF/A, and handing back an ordinary PDF under an archival
    /// format's name is worse than saying it could not be done.
    /// </summary>
    private static async Task<string> ExportPdfAAsync(ReportBuilder builder, string outputPath, CancellationToken ct)
    {
        try
        {
            await RenderWithQuestPdfAsync(builder, outputPath, pdfA: true, ct).ConfigureAwait(false);
            return outputPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"PDF/A generation failed ({ex.Message}). No PDF/A-capable fallback exists; export as " +
                "plain PDF instead if an archival file is not required.", ex);
        }
    }

    /// <summary>
    /// Plain PDF. Default path is QuestPDF in-process — fully managed, no external converter.
    /// Falls back to wkhtmltopdf or headless Chromium only if QuestPDF fails (it shouldn't on
    /// any reasonable report).
    /// </summary>
    private static async Task<string> ExportPdfAsync(ReportBuilder builder, string outputPath, CancellationToken ct)
    {
        try
        {
            await RenderWithQuestPdfAsync(builder, outputPath, pdfA: false, ct).ConfigureAwait(false);
            return outputPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // QuestPDF failed — fall back to the wkhtmltopdf / Chromium path so users with the
            // tool already installed aren't blocked. Surface the QuestPDF error in the fallback's
            // message so a user can report it.
            return await ExportPdfViaExternalAsync(builder, outputPath, ct, fallbackReason: ex.Message)
                .ConfigureAwait(false);
        }
    }

    private static async Task RenderWithQuestPdfAsync(ReportBuilder builder, string outputPath, bool pdfA, CancellationToken ct)
    {
        // QuestPDF Community licence is fine for OSS use. Set once per process.
        QuestPDF.Settings.License = LicenseType.Community;

        var report = builder.Build();
        var markdown = builder.ToMarkdown();
        await Task.Run(() =>
            QuestPdfDocument.Create(report, markdown, pdfA).GeneratePdf(outputPath), ct).ConfigureAwait(false);
    }

    private static async Task<string> ExportPdfViaExternalAsync(ReportBuilder builder, string outputPath, CancellationToken ct, string fallbackReason)
    {
        // SECURITY: write the staging HTML inside a fresh per-invocation temp directory.
        // Don't put the file next to the user-supplied outputPath — that path is user-influenced
        // and could collide with attacker-staged content.
        //
        // The HTML itself is inert: ReportBuilder.ToHtml() renders with raw HTML disabled,
        // restricts links and images, and carries a CSP of default-src 'none'. The converter
        // flags below are a second layer in case a converter ignores the CSP.
        var stagingDir = Path.Combine(Path.GetTempPath(), "cinder-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);
        var htmlPath = Path.Combine(stagingDir, "report.html");
        await File.WriteAllTextAsync(htmlPath, builder.ToHtml(), ct).ConfigureAwait(false);

        try
        {
            var converter = FindPdfConverter(stagingDir);
            if (converter is null)
            {
                throw new InvalidOperationException(
                    $"In-process PDF generation failed ({fallbackReason}) and no external converter is " +
                    "installed (looked for wkhtmltopdf, chrome, msedge).");
            }

            var (exitCode, stdErr) = await RunConverterAsync(
                converter.FileName, converter.BuildArgs(htmlPath, Path.GetFullPath(outputPath)), ct).ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"PDF converter exit {exitCode}: {stdErr}");
            }
            return outputPath;
        }
        finally
        {
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Runs a converter to completion and returns its exit code and stderr.
    ///
    /// <para>Both output pipes are drained while the process runs. Waiting for exit first and
    /// reading afterwards deadlocks as soon as the child writes more than a pipe buffer (4 KB
    /// on Windows): it blocks on the write, and we block on its exit. Chromium is chatty enough
    /// on stderr to do that.</para>
    /// </summary>
    internal static async Task<(int ExitCode, string StdErr)> RunConverterAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = new Process { StartInfo = psi };
        p.Start();

        // Not tied to ct: on cancellation the process is killed, which closes the pipes and
        // completes these on its own.
        var stdOut = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stdErr = p.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        // A helper process that inherited the pipe can hold it open after the converter itself
        // has exited; don't let that turn into a hang of its own.
        var drained = Task.WhenAll(stdOut, stdErr);
        if (await Task.WhenAny(drained, Task.Delay(TimeSpan.FromSeconds(5), ct)).ConfigureAwait(false) != drained)
        {
            return (p.ExitCode, "");
        }
        return (p.ExitCode, await stdErr.ConfigureAwait(false));
    }

    private static async Task<string> ExportDocxAsync(ReportBuilder builder, string outputPath, CancellationToken ct)
    {
        // Real Word document via DocumentFormat.OpenXml. We don't try to reproduce the
        // QuestPDF visual layout 1:1 — Word's typography is its own thing — but we DO emit a
        // well-structured document: title page, per-section bodies with paragraphs and
        // bullets, an exhibit index table, document metadata.
        var report = builder.Build();
        var markdown = builder.ToMarkdown();
        await Task.Run(() => DocxReportWriter.Write(report, markdown, outputPath), ct).ConfigureAwait(false);
        return outputPath;
    }

    private static PdfConverter? FindPdfConverter(string stagingDir)
    {
        foreach (var tool in new[] { "wkhtmltopdf", "wkhtmltopdf.exe" })
        {
            var wk = ResolveOnPath(tool);
            if (wk is not null)
            {
                return new PdfConverter(wk, BuildWkhtmltopdfArgs);
            }
        }
        foreach (var tool in new[] { "chrome", "chrome.exe", "msedge", "msedge.exe", "google-chrome", "chromium" })
        {
            var path = ResolveOnPath(tool);
            if (path is not null)
            {
                var profileDir = Path.Combine(stagingDir, "profile");
                return new PdfConverter(path, (html, pdf) => BuildChromiumArgs(html, pdf, profileDir));
            }
        }
        return null;
    }

    /// <summary>
    /// wkhtmltopdf arguments. SECURITY: returned as a list, never a string. Script is off, and
    /// the proxy points at the discard port so no request can leave the machine. NEVER add
    /// <c>--enable-local-file-access</c> — it lets the rendered HTML reach arbitrary local
    /// files (it has been off by default since 0.12.6).
    /// </summary>
    internal static IReadOnlyList<string> BuildWkhtmltopdfArgs(string htmlPath, string pdfPath) =>
    [
        "--disable-javascript",
        "--disable-plugins",
        "--proxy", "http://127.0.0.1:9",
        htmlPath,
        pdfPath,
    ];

    /// <summary>
    /// Headless Chromium arguments. SECURITY: a throwaway profile (no extensions, no saved
    /// state, nothing shared with the examiner's own browser), background networking off, and
    /// a resolver rule that makes every host name fail — so even markup that got past the
    /// report's CSP could not fetch anything. The <c>file://</c> URI is built here, and only
    /// here, from a path Cinder controls.
    /// </summary>
    internal static IReadOnlyList<string> BuildChromiumArgs(string htmlPath, string pdfPath, string profileDir) =>
    [
        "--headless",
        "--disable-gpu",
        "--no-pdf-header-footer",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-extensions",
        "--disable-background-networking",
        "--disable-sync",
        "--disable-component-update",
        "--disable-default-apps",
        "--host-resolver-rules=MAP * ~NOTFOUND",
        "--user-data-dir=" + profileDir,
        "--print-to-pdf=" + pdfPath,
        new Uri(htmlPath).AbsoluteUri,
    ];

    // Consolidated onto the shared resolver so there is one definition of "where may a helper
    // binary come from".
    private static string? ResolveOnPath(string fileName) => ExecutableResolver.Resolve(fileName);

    private sealed record PdfConverter(string FileName, Func<string, string, IReadOnlyList<string>> BuildArgs);
}
