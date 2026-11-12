using System.Text;
using Cinder.App.Services;
using Cinder.Core.Analysis;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

public class IocScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-ioc-" + Guid.NewGuid().ToString("N"));

    public IocScannerTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public async Task Content_hits_are_found_whatever_the_case_length_or_encoding_of_the_indicator()
    {
        // Each of these was missed before: 9+ letters (no ASCII pattern was built at all),
        // more than 16 bytes (matched case-sensitively), and UTF-16 in a different case.
        File.WriteAllText(Path.Combine(_dir, "a.log"), "2025-01-01 process MIMIKATZ.EXE started");
        File.WriteAllText(Path.Combine(_dir, "b.log"), "dns query Update-Check.Malicious-Domain.Example answered");
        File.WriteAllBytes(Path.Combine(_dir, "c.bin"),
            [0, 1, 2, 3, .. Encoding.Unicode.GetBytes("C:\\Users\\Public\\BADTOOL-Loader.exe"), 9, 9]);

        var iocs = IocList.Parse("""
            mimikatz.exe
            update-check.malicious-domain.example
            badtool-loader.exe
            """);

        var (rows, stats) = await IocScanner.ScanAsync(iocs, _dir, progress: null, maxRows: 1000, CancellationToken.None);

        var content = rows.Where(r => r.Where == "content").ToList();
        content.Should().Contain(r => r.Indicator == "mimikatz.exe" && r.Location.EndsWith("a.log") && r.Context == "ASCII");
        content.Should().Contain(r => r.Indicator == "update-check.malicious-domain.example" && r.Location.EndsWith("b.log"));
        content.Should().Contain(r => r.Indicator == "badtool-loader.exe" && r.Location.EndsWith("c.bin") && r.Context == "UTF-16LE");
        stats.FilesScanned.Should().Be(3);
        stats.Errors.Should().Be(0);
    }

    [Fact]
    public async Task The_same_hash_listed_twice_does_not_abort_the_scan()
    {
        var file = Path.Combine(_dir, "sample.bin");
        File.WriteAllText(file, "hello");
        const string Sha256OfHello = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
        var iocs = new[]
        {
            new Indicator(Sha256OfHello, IndicatorType.Sha256, 1),
            new Indicator(Sha256OfHello.ToUpperInvariant(), IndicatorType.Sha256, 2),
        };

        var (rows, _) = await IocScanner.ScanAsync(iocs, _dir, progress: null, maxRows: 100, CancellationToken.None);

        rows.Should().ContainSingle(r => r.Where == "hash").Which.Location.Should().Be(file);
    }
}
