using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Cinder.App.Services;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

public class DocumentReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-docs-" + Guid.NewGuid().ToString("N"));

    public DocumentReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteZip(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return path;
    }

    /// <summary>Longest text any extraction may return: the cap plus the short "truncated" note.</summary>
    private const int MaxReturned = DocumentReader.MaxOutputChars + 100;

    // ------------------------------------------------------------------ truncation

    [Theory]
    [InlineData("big.log")]
    [InlineData("big.txt")]
    [InlineData("big.csv")]
    [InlineData("big.unknownext")]
    public async Task Plain_text_is_capped_like_every_other_format(string name)
    {
        var line = "2025-06-03 10:00:00 service started ok\n";
        var path = Write(name, string.Concat(Enumerable.Repeat(line, 3 * DocumentReader.MaxOutputChars / line.Length)));

        var result = await DocumentReader.ReadAsync(path);

        result.Success.Should().BeTrue();
        result.Text.Length.Should().BeGreaterThan(DocumentReader.MaxOutputChars).And.BeLessThan(MaxReturned);
        result.Text.Should().StartWith(line).And.Contain("truncated");
        result.Status.Should().Contain("showing the first");
    }

    [Fact]
    public async Task Small_plain_text_comes_back_whole_and_unmarked()
    {
        var result = await DocumentReader.ReadAsync(Write("note.txt", "héllo\nworld"));

        result.Success.Should().BeTrue();
        result.Text.Should().Be("héllo\nworld");
        result.Status.Should().NotContain("showing the first");
    }

    [Fact]
    public async Task Html_and_rtf_output_is_capped_too()
    {
        var html = Write("big.html", "<html><body>" + string.Concat(Enumerable.Repeat("<p>paragraph of text</p>\n", 200_000)) + "</body></html>");
        var rtf = Write("big.rtf", @"{\rtf1 " + string.Concat(Enumerable.Repeat(@"some words here\par ", 200_000)) + "}");

        (await DocumentReader.ReadAsync(html)).Text.Length.Should().BeInRange(DocumentReader.MaxOutputChars, MaxReturned);
        (await DocumentReader.ReadAsync(rtf)).Text.Length.Should().BeInRange(DocumentReader.MaxOutputChars, MaxReturned);
    }

    // ------------------------------------------------------------------ archives

    [Fact]
    public async Task Docx_text_is_extracted_paragraph_by_paragraph()
    {
        var path = WriteZip("small.docx", ("word/document.xml",
            "<w:document xmlns:w=\"urn:w\"><w:body>" +
            "<w:p><w:r><w:t>Hello</w:t></w:r><w:r><w:tab/><w:t xml:space=\"preserve\"> world &amp; all</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t>Second</w:t></w:r></w:p>" +
            "</w:body></w:document>"));

        var result = await DocumentReader.ReadAsync(path);

        result.Success.Should().BeTrue();
        result.Text.Should().Be("Hello\t world & all\nSecond\n");
    }

    [Fact]
    public async Task One_enormous_text_run_in_a_docx_is_cut_off_not_materialised()
    {
        // 12 million characters in a single <w:t>: six times the cap. The reader pulls the
        // value in chunks and stops appending at the cap.
        var path = WriteZip("bomb.docx", ("word/document.xml",
            "<w:document xmlns:w=\"urn:w\"><w:body><w:p><w:r><w:t>" + new string('A', 12_000_000) +
            "</w:t></w:r></w:p></w:body></w:document>"));
        new FileInfo(path).Length.Should().BeLessThan(1_000_000, "it compresses to almost nothing, which is the point");

        var result = await DocumentReader.ReadAsync(path);

        result.Success.Should().BeTrue();
        result.Text.Length.Should().BeInRange(DocumentReader.MaxOutputChars, MaxReturned);
        result.Text.Should().StartWith("AAAA").And.Contain("truncated");
    }

    [Fact]
    public async Task Xlsx_cells_resolve_shared_strings()
    {
        var path = WriteZip("book.xlsx",
            ("xl/sharedStrings.xml", "<sst xmlns=\"urn:x\"><si><t>name</t></si><si><r><t>rich</t></r><r><t> text</t></r></si></sst>"),
            ("xl/worksheets/sheet1.xml",
                "<worksheet xmlns=\"urn:x\"><sheetData>" +
                "<row><c t=\"s\"><v>0</v></c><c t=\"s\"><v>1</v></c></row>" +
                "<row><c><v>42</v></c><c t=\"inlineStr\"><is><t>inline</t></is></c></row>" +
                "</sheetData></worksheet>"));

        var result = await DocumentReader.ReadAsync(path);

        result.Success.Should().BeTrue();
        result.Text.Should().Contain("name\trich text\n42\tinline");
        result.Status.Should().Contain("2 unique strings");
    }

    [Fact]
    public async Task Epub_chapters_are_stripped_of_markup()
    {
        var path = WriteZip("book.epub",
            ("OEBPS/ch1.xhtml", "<html><head><style>p{color:red}</style></head><body><p>Chapter one.</p></body></html>"));

        var result = await DocumentReader.ReadAsync(path);

        result.Success.Should().BeTrue();
        result.Text.Should().Contain("Chapter one.").And.NotContain("color:red").And.NotContain("<p>");
    }

    [Fact]
    public void A_part_that_inflates_past_the_limit_stops_the_read()
    {
        using var limited = new DocumentReader.LimitedStream(new MemoryStream(new byte[10_000]), limit: 4096, "word/document.xml");
        var buffer = new byte[1024];

        var act = () =>
        {
            while (limited.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        };

        act.Should().Throw<InvalidDataException>().WithMessage("*word/document.xml*decompression bomb*");
    }

    [Fact]
    public void A_part_within_the_limit_reads_normally()
    {
        using var limited = new DocumentReader.LimitedStream(new MemoryStream(new byte[4096]), limit: 4096, "x");
        using var copy = new MemoryStream();
        limited.CopyTo(copy);
        copy.Length.Should().Be(4096);
    }

    // ------------------------------------------------------------------ markup stripping: time

    [Fact]
    public void Unclosed_script_tags_do_not_cost_quadratic_time()
    {
        // The regex this replaced took four minutes on this input (measured: 625 KB, 240 s).
        var hostile = string.Concat(Enumerable.Repeat("<script>", 80_000));

        var clock = Stopwatch.StartNew();
        var text = DocumentReader.StripTags(hostile);
        clock.Stop();

        text.Should().BeEmpty();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("<")]
    [InlineData("<style>")]
    [InlineData("<script ")]
    [InlineData("a < b ")]
    [InlineData(" \t ")]
    [InlineData("\n \n")]
    public void Other_repeated_fragments_stay_linear(string fragment)
    {
        var hostile = string.Concat(Enumerable.Repeat(fragment, 600_000 / fragment.Length));

        var clock = Stopwatch.StartNew();
        DocumentReader.StripTags(hostile);
        clock.Stop();

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_hostile_html_file_is_read_end_to_end_without_hanging()
    {
        var path = Write("hostile.html", string.Concat(Enumerable.Repeat("<script>x", 100_000)));

        var clock = Stopwatch.StartNew();
        var result = await DocumentReader.ReadAsync(path);
        clock.Stop();

        result.Success.Should().BeTrue();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    // ------------------------------------------------------------------ markup stripping: output

    [Fact]
    public void StripTags_removes_tags_scripts_and_styles_and_keeps_the_text()
    {
        var html = """
            <html><head><title>T</title>
            <STYLE type="text/css">body { color: red; }</STYLE>
            <script src="x.js">var a = "<p>not text</p>";</script>
            </head><body>
            <h1>Heading</h1>


            <p>First &amp; second&nbsp;line.   </p>
            <p>Literal &amp;lt; stays escaped once; 1 &lt; 2.</p>
            <scripture>kept</scripture>
            </body></html>
            """;

        var text = DocumentReader.StripTags(html);

        text.Should().NotContain("color").And.NotContain("var a").And.NotContain("not text");
        text.Replace("1 < 2", "").Should().NotContain("<");
        text.Should().Contain("Heading").And.Contain("First & second line.").And.Contain("kept");
        text.Should().Contain("Literal &lt; stays escaped once; 1 < 2.");
        text.Should().NotContain("\n\n\n").And.NotContain(" \n");
    }

    [Fact]
    public void StripTags_keeps_text_after_a_script_that_never_closes()
    {
        DocumentReader.StripTags("<p>before</p><script>var x = 1; <p>after</p>")
            .Should().Contain("before").And.Contain("after");
    }

    // ------------------------------------------------------------------ RTF

    [Fact]
    public void Rtf_surrogate_pairs_become_the_character_they_encode()
    {
        // U+1F600 as RTF writes it: two signed 16-bit escapes, each with a '?' fallback.
        var text = DocumentReader.StripRtf(@"{\rtf1\ansi Smile: \u-10179?\u-8704? and caf\u233? and \'e9}");

        text.Should().Contain("Smile: \U0001F600 and café and é");
    }

    [Fact]
    public async Task An_rtf_file_with_an_emoji_is_extracted_rather_than_failing_whole()
    {
        var path = Write("note.rtf", @"{\rtf1\ansi Meeting moved \u-10179?\u-8704?\par See you there}");

        var result = await DocumentReader.ReadAsync(path);

        result.Success.Should().BeTrue(result.Status);
        result.Text.Should().Contain("Meeting moved \U0001F600").And.Contain("See you there");
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public async Task Missing_and_unsupported_files_are_reported_not_thrown()
    {
        (await DocumentReader.ReadAsync(Path.Combine(_dir, "nope.txt"))).Success.Should().BeFalse();

        var binary = Path.Combine(_dir, "blob.dat");
        File.WriteAllBytes(binary, [0, 1, 2, 3, 0, 0, 255]);
        var result = await DocumentReader.ReadAsync(binary);
        result.Success.Should().BeFalse();
        result.Status.Should().Contain("Hex viewer");

        var broken = Write("broken.docx", "this is not a zip archive");
        (await DocumentReader.ReadAsync(broken)).Status.Should().StartWith("Failed to extract");
    }
}
