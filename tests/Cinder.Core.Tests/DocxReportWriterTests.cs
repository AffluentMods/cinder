using System.IO.Compression;
using Cinder.Reports;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// Smoke + structural tests for <see cref="DocxReportWriter"/>. We don't run Word — we open
/// the produced .docx as the ZIP it really is, and check that the core OOXML parts exist and
/// the document body contains the report text.
/// </summary>
public sealed class DocxReportWriterTests : IDisposable
{
    private readonly string _dir;

    public DocxReportWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cinder-docx-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Export_produces_a_real_docx_file()
    {
        var builder = new ReportBuilder("My Case", "alice", "Q3 Investigation");
        builder.AddSection("Background", "Suspect's laptop was imaged on 2026-05-10.\n\nKey findings below.");
        builder.AddSection("Findings", "- USB plugged in at 14:32\n- Files copied to it");

        var outPath = Path.Combine(_dir, "report.docx");
        var exporter = new ReportExporter();
        var actual = await exporter.ExportAsync(builder, ReportFormat.Docx, outPath);

        actual.Should().Be(outPath);
        File.Exists(outPath).Should().BeTrue();
        new FileInfo(outPath).Length.Should().BeGreaterThan(2000, "any non-trivial .docx is larger than a few KB");

        // .docx is a ZIP. Verify the canonical OOXML parts are present.
        using var zip = ZipFile.OpenRead(outPath);
        zip.GetEntry("word/document.xml").Should().NotBeNull("Word looks for this exact path");
        zip.GetEntry("[Content_Types].xml").Should().NotBeNull();
    }

    [Fact]
    public async Task Document_body_contains_section_titles_and_text()
    {
        var builder = new ReportBuilder("Alpha Case", "bob", "Alpha report");
        builder.AddSection("Methodology", "We acquired the disk image with FTK Imager.");
        builder.AddSection("Findings", "Three browser sessions to evil-domain.example.");

        var outPath = Path.Combine(_dir, "report.docx");
        await new ReportExporter().ExportAsync(builder, ReportFormat.Docx, outPath);

        using var zip = ZipFile.OpenRead(outPath);
        using var stream = zip.GetEntry("word/document.xml")!.Open();
        using var reader = new StreamReader(stream);
        var xml = reader.ReadToEnd();

        xml.Should().Contain("Alpha report");
        xml.Should().Contain("Methodology");
        xml.Should().Contain("FTK Imager");
        xml.Should().Contain("Findings");
        xml.Should().Contain("evil-domain.example");
    }

    [Fact]
    public async Task Exhibit_index_table_appears_when_section_has_exhibits()
    {
        var builder = new ReportBuilder("Beta Case", "alice", "Beta report");
        var exhibit = builder.RegisterExhibit(
            "Decrypted password vault",
            ExhibitKind.File,
            description: "Recovered from the user's Documents folder.",
            filePath: "/cases/beta/pwd.kdbx",
            fileSize: 12345,
            sha256: "abcd0123abcd0123abcd0123abcd0123abcd0123abcd0123abcd0123abcd0123");
        builder.AddSection("Evidence", "See exhibit below.", [exhibit]);

        var outPath = Path.Combine(_dir, "report.docx");
        await new ReportExporter().ExportAsync(builder, ReportFormat.Docx, outPath);

        using var zip = ZipFile.OpenRead(outPath);
        using var stream = zip.GetEntry("word/document.xml")!.Open();
        using var reader = new StreamReader(stream);
        var xml = reader.ReadToEnd();

        xml.Should().Contain("EX-0001");
        xml.Should().Contain("Decrypted password vault");
        xml.Should().Contain("pwd.kdbx");
        xml.Should().Contain("Exhibit Index");
        xml.Should().Contain("abcd0123abcd0123abcd0123abcd0123abcd0123abcd0123abcd0123abcd0123");
    }
}
