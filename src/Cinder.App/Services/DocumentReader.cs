using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UglyToad.PdfPig;

namespace Cinder.App.Services;

/// <summary>
/// Extracts readable text from common document formats. Used by the Documents tool so the user
/// who opens a .docx / .xlsx / .pdf / .rtf actually sees the body content, not a hex dump.
///
/// All extractors run in process — no Office, no LibreOffice, no Python sidecar. ZIP-based
/// formats (.docx / .xlsx / .pptx / .odt / .ods / .odp / .epub) parse the inner XML by hand;
/// .pdf uses PdfPig; .rtf strips control words; .html / .xml drop tags; plain-text formats
/// are read as-is.
/// </summary>
public static partial class DocumentReader
{
    /// <summary>Hard cap on the size of any single file we'll try to extract. 50 MB.</summary>
    private const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>Hard cap on the total extracted text length. Stops absurd .docx files from OOMing.</summary>
    private const int MaxOutputChars = 2_000_000;

    public static async Task<DocumentExtractResult> ReadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return new DocumentExtractResult("", $"File not found: {path}", false);
        }

        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
        {
            return new DocumentExtractResult("",
                $"File is {info.Length:N0} bytes — Cinder's document preview is capped at {MaxBytes:N0} bytes. " +
                "Open it in the hex viewer for a byte-level view.",
                false);
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".docx" or ".docm" => await Task.Run(() => ExtractDocx(path), ct),
                ".xlsx" or ".xlsm" => await Task.Run(() => ExtractXlsx(path), ct),
                ".pptx"            => await Task.Run(() => ExtractPptx(path), ct),
                ".odt" or ".ods" or ".odp" => await Task.Run(() => ExtractOpenDocument(path), ct),
                ".epub"            => await Task.Run(() => ExtractEpub(path), ct),
                ".pdf"             => await Task.Run(() => ExtractPdf(path), ct),
                ".rtf"             => new DocumentExtractResult(StripRtf(await File.ReadAllTextAsync(path, ct)), $"RTF · {info.Length:N0} bytes", true),
                ".html" or ".htm" or ".xml" or ".xhtml"
                                   => new DocumentExtractResult(StripTags(await File.ReadAllTextAsync(path, ct)), $"{ext.TrimStart('.')} · {info.Length:N0} bytes", true),
                ".txt" or ".md" or ".log" or ".csv" or ".tsv" or ".json" or ".yaml" or ".yml"
                or ".ini" or ".conf" or ".cfg" or ".toml" or ".cs" or ".py" or ".js" or ".ts"
                or ".java" or ".go" or ".rs" or ".rb" or ".php" or ".sh" or ".ps1" or ".sql"
                                   => new DocumentExtractResult(await File.ReadAllTextAsync(path, ct), $"{ext.TrimStart('.')} · {info.Length:N0} bytes", true),
                _ when LooksLikeText(path) => new DocumentExtractResult(await File.ReadAllTextAsync(path, ct), $"text · {info.Length:N0} bytes", true),
                _ => new DocumentExtractResult("",
                    $"Cinder doesn't have a built-in viewer for *{ext} files. Open in the Hex viewer for a byte-level view.",
                    false),
            };
        }
        catch (Exception ex)
        {
            return new DocumentExtractResult("", $"Failed to extract: {ex.Message}", false);
        }
    }

    // ============================================================ DOCX (Word) ===========

    private static DocumentExtractResult ExtractDocx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        // Combine document.xml + headers + footers, in reading order.
        var sb = new StringBuilder();
        var docEntry = archive.GetEntry("word/document.xml");
        if (docEntry is not null)
        {
            AppendDocxBodyText(docEntry, sb);
        }
        // Headers / footers — usually small; included for completeness.
        foreach (var entry in archive.Entries)
        {
            if (sb.Length >= MaxOutputChars) break;
            var name = entry.FullName;
            if ((name.StartsWith("word/header", StringComparison.Ordinal) ||
                 name.StartsWith("word/footer", StringComparison.Ordinal)) &&
                name.EndsWith(".xml", StringComparison.Ordinal))
            {
                sb.AppendLine();
                sb.AppendLine($"--- {name} ---");
                AppendDocxBodyText(entry, sb);
            }
        }
        return new DocumentExtractResult(
            Truncate(sb.ToString()),
            $"DOCX · {sb.Length:N0} chars",
            true);
    }

    /// <summary>
    /// Reads w:t runs from a single OOXML part. Each &lt;w:p&gt; becomes one paragraph, each
    /// &lt;w:tab&gt; becomes a tab, each &lt;w:br&gt; or paragraph end becomes a newline.
    /// </summary>
    private static void AppendDocxBodyText(ZipArchiveEntry entry, StringBuilder sb)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        while (reader.Read() && sb.Length < MaxOutputChars)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "t":
                        sb.Append(reader.ReadElementContentAsString());
                        break;
                    case "tab":
                        sb.Append('\t');
                        break;
                    case "br":
                    case "cr":
                        sb.Append('\n');
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p")
            {
                sb.Append('\n');
            }
        }
    }

    // ============================================================ XLSX (Excel) ==========

    private static DocumentExtractResult ExtractXlsx(string path)
    {
        using var archive = ZipFile.OpenRead(path);

        // Shared strings table first.
        var shared = new List<string>();
        var sst = archive.GetEntry("xl/sharedStrings.xml");
        if (sst is not null)
        {
            using var stream = sst.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
                {
                    // <si> may contain multiple <t> (rich runs); concatenate them.
                    var s = new StringBuilder();
                    using var sub = reader.ReadSubtree();
                    while (sub.Read())
                    {
                        if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "t")
                        {
                            s.Append(sub.ReadElementContentAsString());
                        }
                    }
                    shared.Add(s.ToString());
                }
            }
        }

        var sb = new StringBuilder();
        // Each sheetN.xml gets its own section.
        var sheetEntries = archive.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) &&
                        e.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();
        var sheetIndex = 1;
        foreach (var sheet in sheetEntries)
        {
            if (sb.Length >= MaxOutputChars) break;
            sb.AppendLine($"=== Sheet {sheetIndex++} ({Path.GetFileNameWithoutExtension(sheet.FullName)}) ===");
            AppendXlsxSheet(sheet, shared, sb);
            sb.AppendLine();
        }

        return new DocumentExtractResult(
            Truncate(sb.ToString()),
            $"XLSX · {sheetEntries.Count} sheet{(sheetEntries.Count == 1 ? "" : "s")} · {shared.Count:N0} unique strings",
            true);
    }

    private static void AppendXlsxSheet(ZipArchiveEntry sheet, IReadOnlyList<string> shared, StringBuilder sb)
    {
        using var stream = sheet.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        var firstInRow = true;
        while (reader.Read() && sb.Length < MaxOutputChars)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
            {
                if (!firstInRow) sb.Append('\n');
                firstInRow = true;
                continue;
            }
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
            {
                var type = reader.GetAttribute("t");
                using var sub = reader.ReadSubtree();
                string? value = null;
                while (sub.Read())
                {
                    if (sub.NodeType == XmlNodeType.Element && (sub.LocalName == "v" || sub.LocalName == "t"))
                    {
                        value = sub.ReadElementContentAsString();
                    }
                }
                if (value is null) continue;
                if (type == "s" && int.TryParse(value, out var idx) && idx >= 0 && idx < shared.Count)
                {
                    value = shared[idx];
                }
                if (!firstInRow) sb.Append('\t');
                sb.Append(value);
                firstInRow = false;
            }
        }
        sb.Append('\n');
    }

    // ============================================================ PPTX (PowerPoint) =====

    private static DocumentExtractResult ExtractPptx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        var slides = archive.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) &&
                        e.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();
        var n = 1;
        foreach (var slide in slides)
        {
            if (sb.Length >= MaxOutputChars) break;
            sb.AppendLine($"=== Slide {n++} ===");
            using var stream = slide.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (reader.Read() && sb.Length < MaxOutputChars)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
                {
                    sb.AppendLine(reader.ReadElementContentAsString());
                }
            }
            sb.AppendLine();
        }
        return new DocumentExtractResult(
            Truncate(sb.ToString()),
            $"PPTX · {slides.Count} slide{(slides.Count == 1 ? "" : "s")}",
            true);
    }

    // ============================================================ OpenDocument =========

    private static DocumentExtractResult ExtractOpenDocument(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("content.xml")
            ?? throw new InvalidDataException("OpenDocument file has no content.xml.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        var sb = new StringBuilder();
        while (reader.Read() && sb.Length < MaxOutputChars)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "p":
                    case "h":
                        // Will produce a newline when the element ends.
                        break;
                    case "tab":
                        sb.Append('\t');
                        break;
                    case "line-break":
                        sb.Append('\n');
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.Text)
            {
                sb.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && (reader.LocalName == "p" || reader.LocalName == "h"))
            {
                sb.Append('\n');
            }
        }
        return new DocumentExtractResult(Truncate(sb.ToString()), $"OpenDocument · {sb.Length:N0} chars", true);
    }

    // ============================================================ EPUB ==================

    private static DocumentExtractResult ExtractEpub(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        var chapters = archive.Entries
            .Where(e => e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                        e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
                        e.FullName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();
        var chapterCount = 0;
        foreach (var chapter in chapters)
        {
            if (sb.Length >= MaxOutputChars) break;
            chapterCount++;
            sb.AppendLine($"=== {chapter.FullName} ===");
            using var stream = chapter.Open();
            using var reader = new StreamReader(stream);
            sb.AppendLine(StripTags(reader.ReadToEnd()));
            sb.AppendLine();
        }
        return new DocumentExtractResult(Truncate(sb.ToString()), $"EPUB · {chapterCount} section{(chapterCount == 1 ? "" : "s")}", true);
    }

    // ============================================================ PDF ===================

    private static DocumentExtractResult ExtractPdf(string path)
    {
        using var doc = PdfDocument.Open(path);
        var sb = new StringBuilder();
        var n = 0;
        foreach (var page in doc.GetPages())
        {
            if (sb.Length >= MaxOutputChars) break;
            n++;
            sb.Append("=== Page ").Append(n).AppendLine(" ===");
            sb.AppendLine(page.Text);
            sb.AppendLine();
        }
        return new DocumentExtractResult(Truncate(sb.ToString()), $"PDF · {doc.NumberOfPages} page{(doc.NumberOfPages == 1 ? "" : "s")}", true);
    }

    // ============================================================ RTF ===================

    /// <summary>
    /// Light-weight RTF text extraction. Handles the common cases (control words, control
    /// symbols, hex-encoded chars, unicode escapes, groups). Not a full RTF parser — fonts,
    /// tables, embedded images, drawings, etc. are stripped to empty.
    /// </summary>
    [GeneratedRegex(@"\\u(-?\d+)\??", RegexOptions.Compiled)]
    private static partial Regex RtfUnicode();

    [GeneratedRegex(@"\\'([0-9a-fA-F]{2})", RegexOptions.Compiled)]
    private static partial Regex RtfHexChar();

    [GeneratedRegex(@"\\\*\s*\\[a-zA-Z]+[-\d]*\s?", RegexOptions.Compiled)]
    private static partial Regex RtfDestination();

    [GeneratedRegex(@"\\[a-zA-Z]+-?\d*\s?", RegexOptions.Compiled)]
    private static partial Regex RtfControlWord();

    [GeneratedRegex(@"[{}]", RegexOptions.Compiled)]
    private static partial Regex RtfBrace();

    internal static string StripRtf(string rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return "";
        // Unicode escapes first (ሴ → the character).
        var s = RtfUnicode().Replace(rtf, m =>
            int.TryParse(m.Groups[1].ValueSpan, out var n)
                ? char.ConvertFromUtf32(n & 0xFFFF)
                : "");
        // Hex chars (\'xx → the corresponding Latin-1 byte).
        s = RtfHexChar().Replace(s, m =>
            byte.TryParse(m.Groups[1].ValueSpan, System.Globalization.NumberStyles.HexNumber, null, out var b)
                ? ((char)b).ToString()
                : "");
        // Destination groups (\*\foo …) — strip whole control word, leave content.
        s = RtfDestination().Replace(s, "");
        // Plain control words.
        s = RtfControlWord().Replace(s, m =>
            m.Value.StartsWith("\\par", StringComparison.Ordinal) ||
            m.Value.StartsWith("\\line", StringComparison.Ordinal) ||
            m.Value.StartsWith("\\tab", StringComparison.Ordinal)
                ? (m.Value.StartsWith("\\tab", StringComparison.Ordinal) ? "\t" : "\n")
                : "");
        // Braces.
        s = RtfBrace().Replace(s, "");
        // Escaped braces / backslashes.
        s = s.Replace("\\\\", "\\").Replace("\\{", "{").Replace("\\}", "}");
        return s;
    }

    // ============================================================ HTML / XML ===========

    [GeneratedRegex(@"<script\b[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HtmlScript();

    [GeneratedRegex(@"<style\b[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HtmlStyle();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"[ \t]+\n", RegexOptions.Compiled)]
    private static partial Regex TrailingWhitespace();

    [GeneratedRegex(@"\n{3,}", RegexOptions.Compiled)]
    private static partial Regex BlankLines();

    internal static string StripTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = HtmlScript().Replace(html, "");
        s = HtmlStyle().Replace(s, "");
        s = HtmlTag().Replace(s, "");
        // HTML entities — quick pass for the common ones.
        s = s.Replace("&nbsp;", " ")
             .Replace("&amp;", "&")
             .Replace("&lt;", "<")
             .Replace("&gt;", ">")
             .Replace("&quot;", "\"")
             .Replace("&#39;", "'")
             .Replace("&apos;", "'");
        s = TrailingWhitespace().Replace(s, "\n");
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }

    // ============================================================ Generic helpers ======

    private static bool LooksLikeText(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> buf = stackalloc byte[Math.Min(8192, (int)Math.Min(fs.Length, 8192))];
            var n = fs.Read(buf);
            int printable = 0;
            for (int i = 0; i < n; i++)
            {
                var b = buf[i];
                if (b == 0) return false; // NUL byte → almost certainly binary
                if (b is >= 0x20 and < 0x7F or 0x09 or 0x0A or 0x0D) printable++;
            }
            return n > 0 && printable * 10 / n >= 9; // ≥ 90% printable
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string s)
    {
        if (s.Length <= MaxOutputChars) return s;
        return string.Concat(
            s.AsSpan(0, MaxOutputChars),
            $"\n\n... (output truncated at {MaxOutputChars:N0} chars)");
    }
}

/// <summary>Result of a single document extraction attempt.</summary>
public sealed record DocumentExtractResult(string Text, string Status, bool Success);
