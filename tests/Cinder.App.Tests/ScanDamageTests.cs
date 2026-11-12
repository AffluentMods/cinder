using System.Text;
using Cinder.App.Services;
using Cinder.Core.Analysis;
using Cinder.Imaging;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>
/// A chunk of an E01 / AFF4 image that cannot be decoded is read as zeros. A scan over such a
/// source that finds nothing has not shown the pattern is absent, and the scan result has to
/// say how much of the source was in that state.
/// </summary>
public class ScanDamageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-damage-" + Guid.NewGuid().ToString("N"));

    public ScanDamageTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>
    /// A container stream as the evidence opener hands it out: readable like any other, and
    /// reporting a damaged chunk once the read position has passed each given offset — the
    /// list grows as the stream is read, exactly as the real readers' does.
    /// </summary>
    private sealed class DamagedContainer(byte[] media, params long[] damagedAt) : MemoryStream(media), IEvidenceDamageSource
    {
        public IReadOnlyList<int> DamagedChunks =>
            damagedAt.Select((offset, index) => (offset, index)).Where(d => Position > d.offset).Select(d => d.index).ToList();
    }

    private static YaraLiteRuleset Rules(string source) => YaraLiteRuleset.Compile(YaraLiteParser.Parse(source));

    private static byte[] Media(int length, string? needle = null, int at = 0)
    {
        var bytes = new byte[length];
        if (needle is not null)
        {
            Encoding.ASCII.GetBytes(needle).CopyTo(bytes, at);
        }
        return bytes;
    }

    // ------------------------------------------------------------------ YARA-lite

    [Fact]
    public async Task A_scan_that_finds_nothing_in_a_damaged_container_says_how_damaged_it_was()
    {
        var set = Rules("rule R { strings: $a = \"indicator\" condition: $a }");
        using var source = new DamagedContainer(Media(10_000), 1_000, 5_000);
        var info = new YaraLiteScanInfo();
        var hits = 0;

        await foreach (var _ in set.ScanAsync(source, "image.E01", bufferSize: 512, info))
        {
            hits++;
        }

        hits.Should().Be(0);
        info.DamagedChunks.Should().Be(2, "no match here is not a clean negative, and the caller must be able to tell");
        info.ReachedEnd.Should().BeTrue();
        info.BytesScanned.Should().Be(10_000);
    }

    [Fact]
    public async Task An_undamaged_source_reports_no_damage()
    {
        var set = Rules("rule R { strings: $a = \"indicator\" condition: $a }");
        var info = new YaraLiteScanInfo();

        using (var container = new DamagedContainer(Media(4_000)))
        {
            await foreach (var _ in set.ScanAsync(container, "clean.E01", bufferSize: 512, info))
            {
            }
        }
        info.DamagedChunks.Should().Be(0);
        info.ReachedEnd.Should().BeTrue();

        // A plain file is not a container at all.
        var plain = new YaraLiteScanInfo();
        using (var file = new MemoryStream(Media(4_000)))
        {
            await foreach (var _ in set.ScanAsync(file, "plain.bin", bufferSize: 512, plain))
            {
            }
        }
        plain.DamagedChunks.Should().Be(0);
        plain.BytesScanned.Should().Be(4_000);
    }

    [Fact]
    public async Task Damage_met_before_a_match_is_known_even_if_the_caller_stops_at_that_match()
    {
        var set = Rules("rule R { strings: $a = \"indicator\" $b = \"never-present\" condition: any of them }");
        using var source = new DamagedContainer(Media(10_000, "indicator", at: 6_000), 1_000, 9_000);
        var info = new YaraLiteScanInfo();

        await foreach (var _ in set.ScanAsync(source, "image.E01", bufferSize: 512, info))
        {
            break;   // e.g. a row limit reached
        }

        info.DamagedChunks.Should().Be(1, "the chunk at 1,000 was read on the way to the match; the one at 9,000 was not reached");
        info.ReachedEnd.Should().BeFalse();
        info.BytesScanned.Should().BeLessThan(10_000);
    }

    [Fact]
    public async Task A_scan_that_stops_because_everything_was_found_says_it_did_not_reach_the_end()
    {
        var set = Rules("rule R { strings: $a = \"indicator\" condition: $a }");
        using var source = new DamagedContainer(Media(10_000, "indicator", at: 100), 9_000);
        var info = new YaraLiteScanInfo();
        var hits = 0;

        await foreach (var _ in set.ScanAsync(source, "image.E01", bufferSize: 512, info))
        {
            hits++;
        }

        hits.Should().Be(1);
        info.ReachedEnd.Should().BeFalse("the damage count then covers only what was read");
        info.DamagedChunks.Should().Be(0);
    }

    [Fact]
    public async Task The_public_file_scan_fills_the_info_and_the_old_signature_still_works()
    {
        var path = Path.Combine(_dir, "plain.bin");
        await File.WriteAllBytesAsync(path, Media(3_000, "indicator", at: 2_000));
        var set = Rules("rule R { strings: $a = \"indicator\" $b = \"absent\" condition: any of them }");

        var info = new YaraLiteScanInfo();
        var withInfo = new List<YaraLiteMatch>();
        await foreach (var hit in set.ScanAsync(path, info))
        {
            withInfo.Add(hit);
        }
        var without = new List<YaraLiteMatch>();
        await foreach (var hit in set.ScanAsync(path))
        {
            without.Add(hit);
        }

        withInfo.Should().BeEquivalentTo(without).And.ContainSingle();
        info.BytesScanned.Should().Be(3_000);
        info.ReachedEnd.Should().BeTrue();
        info.DamagedChunks.Should().Be(0);
    }

    // ------------------------------------------------------------------ IOC scanner

    [Fact]
    public async Task The_ioc_scan_totals_damaged_chunks_and_names_the_files()
    {
        var damaged = Path.Combine(_dir, "laptop.E01");
        var clean = Path.Combine(_dir, "notes.txt");
        File.WriteAllBytes(damaged, Media(8_000));
        File.WriteAllText(clean, "nothing of interest here");
        var iocs = IocList.Parse("evil-domain.example");

        // The opener is the seam: laptop.E01 "is" a container with three damaged chunks.
        Stream Open(string path) => path == damaged
            ? new DamagedContainer(File.ReadAllBytes(path), 100, 2_000, 7_000)
            : EvidenceOpener.Open(path);

        var (rows, stats) = await IocScanner.ScanCoreAsync(iocs, _dir, progress: null, maxRows: 100, Open, CancellationToken.None);

        rows.Should().BeEmpty();
        stats.FilesScanned.Should().Be(2);
        stats.DamagedChunks.Should().Be(3);
        stats.DamagedFiles.Should().Be(1);
        stats.DamagedFilePaths.Should().Equal(damaged);
        stats.ToString().Should().Contain("WARNING: 3 damaged chunks in 1 container").And.Contain("not proof of absence");
    }

    [Fact]
    public async Task A_scan_of_undamaged_files_carries_no_warning()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "contact evil-domain.example today");
        var iocs = IocList.Parse("evil-domain.example");

        var (rows, stats) = await IocScanner.ScanAsync(iocs, _dir, progress: null, maxRows: 100, CancellationToken.None);

        rows.Should().Contain(r => r.Where == "content");
        stats.DamagedChunks.Should().Be(0);
        stats.DamagedFiles.Should().Be(0);
        stats.DamagedFilePaths.Should().BeEmpty();
        stats.ToString().Should().NotContain("WARNING");
    }

    [Fact]
    public async Task Damage_is_counted_even_when_the_file_then_fails_to_read()
    {
        var path = Path.Combine(_dir, "broken.E01");
        File.WriteAllBytes(path, Media(8_000));
        var iocs = IocList.Parse("evil-domain.example");

        var (_, stats) = await IocScanner.ScanCoreAsync(
            iocs, _dir, progress: null, maxRows: 100, _ => new FailsPartWay(Media(8_000)), CancellationToken.None);

        stats.Errors.Should().Be(1);
        stats.DamagedChunks.Should().Be(1);
        stats.DamagedFiles.Should().Be(1);
    }

    /// <summary>One damaged chunk in the first read, then an I/O error.</summary>
    private sealed class FailsPartWay(byte[] media) : MemoryStream(media), IEvidenceDamageSource
    {
        private int _reads;

        public IReadOnlyList<int> DamagedChunks => _reads > 0 ? [0] : [];

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_reads++ > 0)
            {
                throw new IOException("segment file missing");
            }
            return base.Read(buffer, offset, Math.Min(count, 1_000));
        }
    }
}
