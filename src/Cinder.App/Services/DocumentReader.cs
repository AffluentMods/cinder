using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UglyToad.PdfPig;

namespace Cinder.App.Services;

/// <summary>
/// Extracts readable text from common document formats. Used by the Documents tool so the user
/// who opens a .docx / .xlsx / .pdf / .rtf actually sees the body content, not a hex dump, and
/// by the Search tool to index a folder of evidence.
///
/// <para>All extractors run in process — no Office, no LibreOffice, no Python sidecar. ZIP-based
/// formats (.docx / .xlsx / .pptx / .odt / .ods / .odp / .epub) parse the inner XML by hand;
/// .pdf uses PdfPig; .rtf strips control words; .html / .xml drop tags; plain-text formats
/// are read as-is.</para>
///
/// <para>The input is evidence, which means it is untrusted and sometimes hostile. Every path
/// here is bounded: the file size, the bytes any one archive part may inflate to, the length of
/// a single text run, and the text returned. Nothing reads "to the end" of something whose end
/// an attacker chose, and no pattern is matched without a time limit.</para>
/// </summary>
public static partial class DocumentReader
{
    /// <summary>Hard cap on the size of any single file we'll try to extract. 50 MB.</summary>
    private const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Hard cap on the extracted text, for every format. Two million characters is more than
    /// anyone reads in a preview pane, and it is what keeps a 50 MB log from being laid out as
    /// a single text block on the UI thread.
    /// </summary>
    internal const int MaxOutputChars = 2_000_000;

    /// <summary>
    /// Most bytes any one part of a ZIP container may inflate to before extraction stops. A
    /// 50 MB archive can declare a part that expands to tens of gigabytes; this is the brake.
    /// </summary>
    internal const long MaxEntryBytes = 256L * 1024 * 1024;

    /// <summary>Shared-string table limits for .xlsx (entries, and characters across all entries).</summary>
    private const int MaxSharedStrings = 2_000_000;
    private const long MaxSharedStringChars = 64L * 1024 * 1024;

    /// <summary>Excel's own limit on the text in one cell is 32,767 characters; allow double.</summary>
    private const int MaxCellChars = 65_536;

    /// <summary>Raw markup read from one EPUB chapter before tags are stripped.</summary>
    private const int MaxChapterChars = 8_000_000;

    private const int RegexTimeoutMs = 2_000;

    /// <summary>
    /// Extractors collect one character more than the cap, so that "there was more" is still
    /// visible to <see cref="Finish"/> and the result says it was cut short.
    /// </summary>
    private const int Budget = MaxOutputChars + 1;

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
            // Reading, inflating, parsing and tag-stripping all happen on the thread pool. The
            // callers are view-model commands; none of this may run on the UI thread.
            return await Task.Run(() => Extract(path, ext, info.Length, ct), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DocumentExtractResult("", $"Failed to extract: {ex.Message}", false);
        }
    }

    private static DocumentExtractResult Extract(string path, string ext, long length, CancellationToken ct)
    {
        switch (ext)
        {
            case ".docx" or ".docm":
                return ExtractDocx(path, ct);
            case ".xlsx" or ".xlsm":
                return ExtractXlsx(path, ct);
            case ".pptx":
                return ExtractPptx(path, ct);
            case ".odt" or ".ods" or ".odp":
                return ExtractOpenDocument(path, ct);
            case ".epub":
                return ExtractEpub(path, ct);
            case ".pdf":
                return ExtractPdf(path, ct);
            case ".rtf":
                return Finish(StripRtf(File.ReadAllText(path)), $"RTF · {length:N0} bytes");
            case ".html" or ".htm" or ".xml" or ".xhtml":
                return Finish(StripTags(File.ReadAllText(path)), $"{ext.TrimStart('.')} · {length:N0} bytes");
            case ".txt" or ".md" or ".log" or ".csv" or ".tsv" or ".json" or ".yaml" or ".yml"
                or ".ini" or ".conf" or ".cfg" or ".toml" or ".cs" or ".py" or ".js" or ".ts"
                or ".java" or ".go" or ".rs" or ".rb" or ".php" or ".sh" or ".ps1" or ".sql":
                return PlainText(path, $"{ext.TrimStart('.')} · {length:N0} bytes", ct);
            default:
                return LooksLikeText(path)
                    ? PlainText(path, $"text · {length:N0} bytes", ct)
                    : new DocumentExtractResult("",
                        $"Cinder doesn't have a built-in viewer for *{ext} files. Open in the Hex viewer for a byte-level view.",
                        false);
        }
    }

    // ============================================================ Plain text ============

    /// <summary>Reads at most <see cref="MaxOutputChars"/> characters; the rest of the file is never loaded.</summary>
    private static DocumentExtractResult PlainText(string path, string status, CancellationToken ct)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = ReadBounded(reader, MaxOutputChars, out var more, ct);
        return more
            ? new DocumentExtractResult(text + TruncationNote, status + TruncatedStatus, true)
            : new DocumentExtractResult(text, status, true);
    }

    /// <summary>Reads up to <paramref name="maxChars"/> characters. <paramref name="more"/> says whether input remained.</summary>
    private static string ReadBounded(TextReader reader, int maxChars, out bool more, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[81920];
        while (sb.Length < maxChars)
        {
            ct.ThrowIfCancellationRequested();
            var n = reader.Read(buffer, 0, Math.Min(buffer.Length, maxChars - sb.Length));
            if (n <= 0)
            {
                more = false;
                return sb.ToString();
            }
            sb.Append(buffer, 0, n);
        }
        more = reader.Peek() >= 0;
        return sb.ToString();
    }

    // ============================================================ ZIP / XML plumbing ====

    /// <summary>
    /// Opens a ZIP part for XML reading with the inflated size capped, DTDs ignored and no
    /// resolver, so neither a decompression bomb nor an external entity gets anywhere.
    /// </summary>
    private static XmlReader OpenXml(ZipArchiveEntry entry) =>
        XmlReader.Create(
            new LimitedStream(entry.Open(), MaxEntryBytes, entry.FullName),
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                CloseInput = true,
            });

    /// <summary>
    /// Appends the text content of the element the reader is on, never more than leaves
    /// <paramref name="sb"/> at <paramref name="limit"/> characters. The value is pulled in
    /// chunks, so a single text run of a billion characters is skipped past rather than
    /// materialised. Leaves the reader on the element's end tag.
    /// </summary>
    private static void AppendElementText(XmlReader reader, StringBuilder sb, int limit, char[] scratch)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }
        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                return;
            }
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                AppendCurrentValue(reader, sb, limit, scratch);
            }
        }
    }

    /// <summary>Appends the value of the text node the reader is on, bounded by <paramref name="limit"/>.</summary>
    private static void AppendCurrentValue(XmlReader reader, StringBuilder sb, int limit, char[] scratch)
    {
        int n;
        while (sb.Length < limit && (n = reader.ReadValueChunk(scratch, 0, scratch.Length)) > 0)
        {
            sb.Append(scratch, 0, Math.Min(n, limit - sb.Length));
        }
        // Whatever is left of the value is discarded by the reader's next Read().
    }

    /// <summary>Cancellation check that is cheap enough to sit inside a per-node loop.</summary>
    private static void Tick(ref int counter, CancellationToken ct)
    {
        if ((++counter & 0xFFF) == 0)
        {
            ct.ThrowIfCancellationRequested();
        }
    }

    // ============================================================ DOCX (Word) ===========

    private static DocumentExtractResult ExtractDocx(string path, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(path);
        // Combine document.xml + headers + footers, in reading order.
        var sb = new StringBuilder();
        var docEntry = archive.GetEntry("word/document.xml");
        if (docEntry is not null)
        {
            AppendDocxBodyText(docEntry, sb, ct);
        }
        // Headers / footers — usually small; included for completeness.
        foreach (var entry in archive.Entries)
        {
            if (sb.Length >= Budget) break;
            var name = entry.FullName;
            if ((name.StartsWith("word/header", StringComparison.Ordinal) ||
                 name.StartsWith("word/footer", StringComparison.Ordinal)) &&
                name.EndsWith(".xml", StringComparison.Ordinal))
            {
                sb.AppendLine();
                sb.AppendLine($"--- {name} ---");
                AppendDocxBodyText(entry, sb, ct);
            }
        }
        return Finish(sb.ToString(), $"DOCX · {Math.Min(sb.Length, MaxOutputChars):N0} chars");
    }

    /// <summary>
    /// Reads w:t runs from a single OOXML part. Each &lt;w:p&gt; becomes one paragraph, each
    /// &lt;w:tab&gt; becomes a tab, each &lt;w:br&gt; or paragraph end becomes a newline.
    /// </summary>
    private static void AppendDocxBodyText(ZipArchiveEntry entry, StringBuilder sb, CancellationToken ct)
    {
        using var reader = OpenXml(entry);
        var scratch = new char[8192];
        var ticks = 0;
        while (sb.Length < Budget && reader.Read())
        {
            Tick(ref ticks, ct);
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "t":
                        AppendElementText(reader, sb, Budget, scratch);
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

    private static DocumentExtractResult ExtractXlsx(string path, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(path);

        // Shared strings table first.
        var shared = new List<string>();
        var sharedTruncated = false;
        var sst = archive.GetEntry("xl/sharedStrings.xml");
        if (sst is not null)
        {
            using var reader = OpenXml(sst);
            var scratch = new char[8192];
            var one = new StringBuilder();
            long totalChars = 0;
            var ticks = 0;
            while (reader.Read())
            {
                Tick(ref ticks, ct);
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si")
                {
                    continue;
                }
                if (shared.Count >= MaxSharedStrings || totalChars >= MaxSharedStringChars)
                {
                    // Stop here rather than grow without bound; cells that point past this
                    // are labelled as such instead of being shown a wrong string.
                    sharedTruncated = true;
                    break;
                }
                // <si> may contain multiple <t> (rich runs); concatenate them.
                one.Clear();
                if (!reader.IsEmptyElement)
                {
                    var depth = reader.Depth;
                    while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
                    {
                        Tick(ref ticks, ct);
                        if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
                        {
                            AppendElementText(reader, one, MaxCellChars, scratch);
                        }
                    }
                }
                totalChars += one.Length;
                shared.Add(one.ToString());
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
            if (sb.Length >= Budget) break;
            sb.AppendLine($"=== Sheet {sheetIndex++} ({Path.GetFileNameWithoutExtension(sheet.FullName)}) ===");
            AppendXlsxSheet(sheet, shared, sharedTruncated, sb, ct);
            sb.AppendLine();
        }

        var status = $"XLSX · {sheetEntries.Count} sheet{(sheetEntries.Count == 1 ? "" : "s")} · {shared.Count:N0} unique strings";
        if (sharedTruncated)
        {
            status += " (string table cut short; later cells show their index)";
        }
        return Finish(sb.ToString(), status);
    }

    private static void AppendXlsxSheet(
        ZipArchiveEntry sheet, IReadOnlyList<string> shared, bool sharedTruncated, StringBuilder sb, CancellationToken ct)
    {
        using var reader = OpenXml(sheet);
        var scratch = new char[8192];
        var cell = new StringBuilder();
        var firstInRow = true;
        var ticks = 0;
        while (sb.Length < Budget && reader.Read())
        {
            Tick(ref ticks, ct);
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
            {
                if (!firstInRow) sb.Append('\n');
                firstInRow = true;
                continue;
            }
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
            {
                var type = reader.GetAttribute("t");
                string? value = null;
                if (!reader.IsEmptyElement)
                {
                    var depth = reader.Depth;
                    while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
                    {
                        Tick(ref ticks, ct);
                        if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "v" or "t")
                        {
                            cell.Clear();
                            AppendElementText(reader, cell, MaxCellChars, scratch);
                            value = cell.ToString();
                        }
                    }
                }
                if (value is null) continue;
                if (type == "s" && int.TryParse(value, out var idx) && idx >= 0)
                {
                    value = idx < shared.Count ? shared[idx]
                        : sharedTruncated ? $"[shared string #{idx} not loaded]"
                        : value;
                }
                if (!firstInRow) sb.Append('\t');
                sb.Append(value);
                firstInRow = false;
            }
        }
        sb.Append('\n');
    }

    // ============================================================ PPTX (PowerPoint) =====

    private static DocumentExtractResult ExtractPptx(string path, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        var slides = archive.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) &&
                        e.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();
        var scratch = new char[8192];
        var n = 1;
        var ticks = 0;
        foreach (var slide in slides)
        {
            if (sb.Length >= Budget) break;
            sb.AppendLine($"=== Slide {n++} ===");
            using var reader = OpenXml(slide);
            while (sb.Length < Budget && reader.Read())
            {
                Tick(ref ticks, ct);
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
                {
                    AppendElementText(reader, sb, Budget, scratch);
                    sb.AppendLine();
                }
            }
            sb.AppendLine();
        }
        return Finish(sb.ToString(), $"PPTX · {slides.Count} slide{(slides.Count == 1 ? "" : "s")}");
    }

    // ============================================================ OpenDocument =========

    private static DocumentExtractResult ExtractOpenDocument(string path, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("content.xml")
            ?? throw new InvalidDataException("OpenDocument file has no content.xml.");
        using var reader = OpenXml(entry);
        var sb = new StringBuilder();
        var scratch = new char[8192];
        var ticks = 0;
        while (sb.Length < Budget && reader.Read())
        {
            Tick(ref ticks, ct);
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
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
                AppendCurrentValue(reader, sb, Budget, scratch);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && (reader.LocalName == "p" || reader.LocalName == "h"))
            {
                sb.Append('\n');
            }
        }
        return Finish(sb.ToString(), $"OpenDocument · {Math.Min(sb.Length, MaxOutputChars):N0} chars");
    }

    // ============================================================ EPUB ==================

    private static DocumentExtractResult ExtractEpub(string path, CancellationToken ct)
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
            if (sb.Length >= Budget) break;
            ct.ThrowIfCancellationRequested();
            chapterCount++;
            sb.AppendLine($"=== {chapter.FullName} ===");
            using var stream = new LimitedStream(chapter.Open(), MaxEntryBytes, chapter.FullName);
            using var reader = new StreamReader(stream);
            // A chapter is read up to a fixed length, never "to the end": the end is wherever
            // the archive's author put it.
            var markup = ReadBounded(reader, MaxChapterChars, out var more, ct);
            sb.AppendLine(StripTags(markup));
            if (more)
            {
                sb.AppendLine($"[chapter cut short at {MaxChapterChars:N0} characters of markup]");
            }
            sb.AppendLine();
        }
        return Finish(sb.ToString(), $"EPUB · {chapterCount} section{(chapterCount == 1 ? "" : "s")}");
    }

    // ============================================================ PDF ===================

    private static DocumentExtractResult ExtractPdf(string path, CancellationToken ct)
    {
        using var doc = PdfDocument.Open(path);
        var sb = new StringBuilder();
        var n = 0;
        foreach (var page in doc.GetPages())
        {
            if (sb.Length >= Budget) break;
            ct.ThrowIfCancellationRequested();
            n++;
            sb.Append("=== Page ").Append(n).AppendLine(" ===");
            sb.AppendLine(page.Text);
            sb.AppendLine();
        }
        return Finish(sb.ToString(), $"PDF · {doc.NumberOfPages} page{(doc.NumberOfPages == 1 ? "" : "s")}");
    }

    // ============================================================ RTF ===================

    /// <summary>
    /// Light-weight RTF text extraction. Handles the common cases (control words, control
    /// symbols, hex-encoded chars, unicode escapes, groups). Not a full RTF parser — fonts,
    /// tables, embedded images, drawings, etc. are stripped to empty.
    /// </summary>
    [GeneratedRegex(@"\\u(-?\d+)\??", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex RtfUnicode();

    [GeneratedRegex(@"\\'([0-9a-fA-F]{2})", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex RtfHexChar();

    [GeneratedRegex(@"\\\*\s*\\[a-zA-Z]+[-\d]*\s?", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex RtfDestination();

    [GeneratedRegex(@"\\[a-zA-Z]+-?\d*\s?", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex RtfControlWord();

    [GeneratedRegex(@"[{}]", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex RtfBrace();

    internal static string StripRtf(string rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return "";
        // Unicode escapes first (\uN is a signed 16-bit UTF-16 code unit). A character outside
        // the BMP — any emoji — is written as two of them, a surrogate pair, so each escape
        // must become exactly one char; treating one as a whole code point throws on the pair.
        var s = RtfUnicode().Replace(rtf, m =>
            int.TryParse(m.Groups[1].ValueSpan, out var n)
                ? ((char)(n & 0xFFFF)).ToString()
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

    /// <summary>
    /// Drops tags, and the contents of &lt;script&gt; and &lt;style&gt;, in one pass over the
    /// input. Written by hand rather than as a regular expression on purpose: the obvious
    /// patterns rescan to the end of the document for every unclosed &lt;script&gt; (or every
    /// '&lt;' with no '&gt;' after it), which turns a 600 KB hostile page into minutes of work.
    /// Here each "is there a closing tag ahead?" question is asked at most once.
    /// </summary>
    internal static string StripTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        var sb = new StringBuilder(html.Length);
        var i = 0;
        var noCloseAhead = false;        // there is no '>' at or after the last place we looked
        var noScriptEnd = false;         // … no "</script>" …
        var noStyleEnd = false;          // … no "</style>" …
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                sb.Append(html, i, html.Length - i);
                break;
            }
            sb.Append(html, i, lt - i);

            var gt = noCloseAhead ? -1 : html.IndexOf('>', lt + 1);
            if (gt < 0)
            {
                // Not a tag: a bare '<' in text. Remember, so the next one costs nothing.
                noCloseAhead = true;
                sb.Append('<');
                i = lt + 1;
                continue;
            }
            if (gt == lt + 1)
            {
                sb.Append("<>");
                i = gt + 1;
                continue;
            }

            i = gt + 1;   // the tag itself is dropped
            if (!noScriptEnd && OpensElement(html, lt, "script"))
            {
                var end = html.IndexOf("</script>", i, StringComparison.OrdinalIgnoreCase);
                if (end < 0) noScriptEnd = true; else i = end + "</script>".Length;
            }
            else if (!noStyleEnd && OpensElement(html, lt, "style"))
            {
                var end = html.IndexOf("</style>", i, StringComparison.OrdinalIgnoreCase);
                if (end < 0) noStyleEnd = true; else i = end + "</style>".Length;
            }
        }

        // Entities in one pass (so "&amp;lt;" decodes once, to "&lt;", not twice).
        var text = WebUtility.HtmlDecode(sb.ToString()).Replace('\u00A0', ' ');
        return TidyWhitespace(text);
    }

    /// <summary>True when the tag starting at <paramref name="lt"/> is <c>&lt;name</c> followed by a non-name character.</summary>
    private static bool OpensElement(string html, int lt, string name)
    {
        var start = lt + 1;
        if (start + name.Length >= html.Length ||
            string.Compare(html, start, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }
        var next = html[start + name.Length];
        return !char.IsLetterOrDigit(next) && next != '_' && next != '-';
    }

    /// <summary>Removes trailing blanks on each line, collapses runs of blank lines to one, and trims. Linear.</summary>
    private static string TidyWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var trailingBlanks = 0;   // spaces / tabs at the end of sb
        var newlineRun = 0;       // consecutive '\n' at the end of sb, ignoring blanks between them
        foreach (var c in text)
        {
            switch (c)
            {
                case '\r':
                    break;
                case '\n':
                    sb.Length -= trailingBlanks;
                    trailingBlanks = 0;
                    if (newlineRun < 2)
                    {
                        sb.Append('\n');
                    }
                    newlineRun++;
                    break;
                case ' ' or '\t':
                    sb.Append(c);
                    trailingBlanks++;
                    break;
                default:
                    sb.Append(c);
                    trailingBlanks = 0;
                    newlineRun = 0;
                    break;
            }
        }
        return sb.ToString().Trim();
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

    private static readonly string TruncationNote = $"\n\n... (output truncated at {MaxOutputChars:N0} chars)";
    private static readonly string TruncatedStatus = $" · showing the first {MaxOutputChars:N0} characters";

    /// <summary>Every successful extraction leaves through here (or <see cref="PlainText"/>), so every one is capped.</summary>
    private static DocumentExtractResult Finish(string text, string status) =>
        text.Length <= MaxOutputChars
            ? new DocumentExtractResult(text, status, true)
            : new DocumentExtractResult(
                string.Concat(text.AsSpan(0, MaxOutputChars), TruncationNote),
                status + TruncatedStatus,
                true);

    /// <summary>
    /// Read-only pass-through that refuses to yield more than a fixed number of bytes. Put
    /// around a decompression stream, it turns "inflate until the archive says stop" into
    /// "inflate until we say stop".
    /// </summary>
    internal sealed class LimitedStream(Stream inner, long limit, string name) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            _read += n;
            if (_read > limit)
            {
                throw new InvalidDataException(
                    $"'{name}' expands to more than {limit:N0} bytes. Extraction stopped: this is what a decompression bomb looks like.");
            }
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>Result of a single document extraction attempt.</summary>
public sealed record DocumentExtractResult(string Text, string Status, bool Success);
