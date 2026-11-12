using Cinder.Core.Signatures;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

public sealed class SignatureScannerTests
{
    [Fact]
    public void Detects_PNG()
    {
        var sut = new SignatureScanner();
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
        var hits = sut.Scan(header);
        hits.Should().ContainSingle(h => h.Signature.Label == "PNG");
    }

    [Fact]
    public void Detects_JPEG()
    {
        var sut = new SignatureScanner();
        byte[] header = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0];
        sut.Scan(header).Should().Contain(h => h.Signature.Label == "JPEG");
    }

    [Fact]
    public void Detects_NTFS_boot_sector()
    {
        var sut = new SignatureScanner();
        var header = new byte[512];
        "NTFS    "u8.CopyTo(header.AsSpan(3));
        sut.Scan(header).Should().Contain(h => h.Signature.Label == "NTFS boot sector");
    }

    [Fact]
    public void Extension_mismatch_flagged()
    {
        var sut = new SignatureScanner();
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]; // PNG bytes
        sut.IsExtensionMismatch("not-actually.txt", header, out var match)
            .Should().BeTrue();
        match!.Signature.Extension.Should().Be("png");
    }

    [Fact]
    public void Extension_match_not_flagged()
    {
        var sut = new SignatureScanner();
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        sut.IsExtensionMismatch("photo.png", header, out _).Should().BeFalse();
    }

    private static readonly byte[] Mz = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
    private static readonly byte[] Cfb = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00];
    private static readonly byte[] Tiff = [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00];
    private static readonly byte[] Pst = [0x21, 0x42, 0x44, 0x4E, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] Wav = [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WAVEfmt "u8];
    private static readonly byte[] Mov = [0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70, 0x71, 0x74, 0x20, 0x20];
    private static readonly byte[] Ewf = [0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00];

    public static TheoryData<string, byte[]> OrdinaryAliases => new()
    {
        { "kernel32.dll", Mz }, { "ntfs.sys", Mz }, { "saver.SCR", Mz },
        { "IMG_0001.jpeg", Jpeg }, { "scan.JPE", Jpeg },
        { "budget.xls", Cfb }, { "deck.ppt", Cfb }, { "setup.msi", Cfb }, { "mail.msg", Cfb },
        { "report.docx", Zip }, { "sheet.xlsm", Zip }, { "app.apk", Zip }, { "archive.zip", Zip },
        { "scan.tif", Tiff },
        { "mail.ost", Pst }, { "mail.pst", Pst },
        { "ring.wav", Wav },
        { "clip.mov", Mov }, { "song.m4a", Mov },
        { "disk.E02", Ewf }, { "disk.e01", Ewf },
    };

    [Theory]
    [MemberData(nameof(OrdinaryAliases))]
    public void An_extension_that_normally_holds_this_content_is_not_a_mismatch(string fileName, byte[] header)
    {
        // Every one of these was flagged before: the comparison was against the single
        // canonical extension of the best hit (dll vs exe, jpeg vs jpg, xls vs doc, wav vs avi).
        new SignatureScanner().IsExtensionMismatch(fileName, header, out var best).Should().BeFalse();
        best.Should().NotBeNull("the best match is still reported");
    }

    [Theory]
    [InlineData("invoice.pdf")]
    [InlineData("holiday.jpg")]
    [InlineData("notes.txt")]
    [InlineData("archive.zip")]
    public void An_executable_under_a_document_extension_is_still_a_mismatch(string fileName)
    {
        new SignatureScanner().IsExtensionMismatch(fileName, Mz, out var best).Should().BeTrue();
        best!.Signature.Extension.Should().Be("exe");
    }

    [Fact]
    public void Aliases_do_not_leak_between_formats()
    {
        var sut = new SignatureScanner();
        sut.IsExtensionMismatch("photo.jpeg", Cfb, out _).Should().BeTrue("a compound file is not a JPEG");
        sut.IsExtensionMismatch("library.dll", Zip, out _).Should().BeTrue("a ZIP is not a PE image");
        sut.IsExtensionMismatch("budget.xls", Jpeg, out _).Should().BeTrue();
        SignatureScanner.IsExtensionConsistent(".DLL", "exe").Should().BeTrue();
        SignatureScanner.IsExtensionConsistent("dll", "doc").Should().BeFalse();
    }

    [Fact]
    public void Empty_header_yields_no_hits()
    {
        var sut = new SignatureScanner();
        sut.Scan([]).Should().BeEmpty();
    }
}
