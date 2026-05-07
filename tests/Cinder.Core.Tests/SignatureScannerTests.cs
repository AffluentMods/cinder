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

    [Fact]
    public void Empty_header_yields_no_hits()
    {
        var sut = new SignatureScanner();
        sut.Scan([]).Should().BeEmpty();
    }
}
