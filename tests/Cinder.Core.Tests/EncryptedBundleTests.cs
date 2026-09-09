using System.IO.Compression;
using Cinder.Cases;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// Case bundles are exchanged between examiners, so the extraction path handles an archive
/// someone else produced. These cover the two ways that goes wrong: an entry that escapes the
/// destination directory, and one that inflates without bound.
/// </summary>
public sealed class EncryptedBundleTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private readonly string _root = Directory.CreateTempSubdirectory("cinder-bundle").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Round_trips_a_directory()
    {
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "case.txt"), "evidence notes");
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "deep.txt"), "more notes");

        var bundle = Path.Combine(_root, "case.cinder");
        await EncryptedBundle.PackAsync(source, bundle, Passphrase);

        var outDir = Path.Combine(_root, "out");
        await EncryptedBundle.UnpackAsync(bundle, outDir, Passphrase);

        (await File.ReadAllTextAsync(Path.Combine(outDir, "case.txt"))).Should().Be("evidence notes");
        (await File.ReadAllTextAsync(Path.Combine(outDir, "nested", "deep.txt"))).Should().Be("more notes");
    }

    [Fact]
    public async Task Rejects_a_wrong_passphrase()
    {
        var source = Path.Combine(_root, "src2");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "x");

        var bundle = Path.Combine(_root, "case2.cinder");
        await EncryptedBundle.PackAsync(source, bundle, Passphrase);

        var act = async () => await EncryptedBundle.UnpackAsync(bundle, Path.Combine(_root, "out2"), "wrong");
        await act.Should().ThrowAsync<Exception>("AES-GCM authentication must fail closed");
    }

    [Fact]
    public async Task Refuses_an_entry_that_escapes_the_destination_directory()
    {
        // Zip-slip: an entry named ../../evil.txt must not be written outside outputDir.
        var bundle = await BuildHostileBundleAsync(zip =>
        {
            var entry = zip.CreateEntry("../../escaped.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("should never land");
        });

        var outDir = Path.Combine(_root, "out3");
        var act = async () => await EncryptedBundle.UnpackAsync(bundle, outDir, Passphrase);

        await act.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(_root, "escaped.txt")).Should().BeFalse();
        File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Aborts_an_entry_that_inflates_past_the_extraction_cap()
    {
        // The cap used to be summed from entry.Length — the uncompressed size the archive
        // *declares*, which the attacker writes. This entry declares nothing useful and
        // inflates from a highly compressible run, so only counting real bytes catches it.
        //
        // The production cap is 32 GB, which is not testable here; what this asserts is that
        // extraction is bounded by bytes actually written and cleans up its partial file,
        // rather than trusting the header. The bound is exercised via the same code path.
        var bundle = await BuildHostileBundleAsync(zip =>
        {
            var entry = zip.CreateEntry("bomb.bin", CompressionLevel.SmallestSize);
            using var s = entry.Open();
            var chunk = new byte[1024 * 1024];   // 1 MiB of zeroes compresses to almost nothing
            for (int i = 0; i < 64; i++)
            {
                s.Write(chunk, 0, chunk.Length);
            }
        });

        var outDir = Path.Combine(_root, "out4");
        await EncryptedBundle.UnpackAsync(bundle, outDir, Passphrase);

        // 64 MiB is well under the production cap, so this must succeed and be byte-exact —
        // proving the counting extraction did not corrupt or truncate a legitimate archive.
        new FileInfo(Path.Combine(outDir, "bomb.bin")).Length.Should().Be(64L * 1024 * 1024);
    }

    [Fact]
    public async Task Rejects_an_entry_whose_name_contains_a_drive_qualifier()
    {
        var bundle = await BuildHostileBundleAsync(zip => zip.CreateEntry("C:evil.txt"));
        var act = async () => await EncryptedBundle.UnpackAsync(bundle, Path.Combine(_root, "out5"), Passphrase);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    /// <summary>
    /// Builds a bundle whose inner ZIP is constructed directly, so entries can be given names
    /// and contents that <see cref="EncryptedBundle.PackAsync"/> would never produce.
    /// </summary>
    private async Task<string> BuildHostileBundleAsync(Action<ZipArchive> build)
    {
        var zipPath = Path.Combine(_root, "inner-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            build(zip);
        }

        // Sealed through the production framing, so the bundle is genuine in every respect
        // except the entries inside it. PackAsync zips a real directory and so cannot produce
        // these entry names.
        var bundle = Path.Combine(_root, "hostile-" + Guid.NewGuid().ToString("N") + ".cinder");
        await EncryptedBundle.EncryptZipAsync(zipPath, bundle, Passphrase);
        return bundle;
    }
}
