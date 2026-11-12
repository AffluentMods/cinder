using System.IO.Compression;
using System.Security.Cryptography;
using Cinder.Cases;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// The bundle's on-disk format: version 2 is a chunked AEAD stream (so size is no longer bounded
/// by what fits in one array), version 1 bundles from earlier builds still open, and every way
/// of damaging a chunked bundle — flipping, dropping, swapping or appending chunks — is refused
/// before anything is extracted.
///
/// <para>Not covered here: an actual bundle larger than 2 GiB. The tests use a small chunk size
/// so that a few kilobytes exercise many chunks and every boundary case; they do not write
/// gigabytes to the build machine's disk.</para>
/// </summary>
public sealed class EncryptedBundleFormatTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const int HeaderAndPreamble = 16 + 32 + 7 + 4;
    private const int SmallChunk = 256;
    private const int StoredChunk = SmallChunk + 16;

    private readonly string _root = Directory.CreateTempSubdirectory("cinder-bundle-format").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeCaseDirectory(int payloadBytes = 20_000)
    {
        var source = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(source, "nested", "deeper"));
        Directory.CreateDirectory(Path.Combine(source, "empty-dir"));
        File.WriteAllText(Path.Combine(source, "case.txt"), "evidence notes");
        // Random bytes do not compress, so the archive really is larger than many chunks.
        File.WriteAllBytes(Path.Combine(source, "nested", "image.bin"), RandomNumberGenerator.GetBytes(payloadBytes));
        File.WriteAllText(Path.Combine(source, "nested", "deeper", "note.md"), "deep");
        return source;
    }

    private static void AssertSameTree(string expected, string actual)
    {
        var expectedFiles = Directory.EnumerateFiles(expected, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(expected, f)).Order().ToList();
        var actualFiles = Directory.EnumerateFiles(actual, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(actual, f)).Order().ToList();
        actualFiles.Should().Equal(expectedFiles);
        foreach (var rel in expectedFiles)
        {
            File.ReadAllBytes(Path.Combine(actual, rel)).Should().Equal(File.ReadAllBytes(Path.Combine(expected, rel)), rel);
        }
    }

    [Fact]
    public async Task Pack_writes_format_version_2()
    {
        var bundle = Path.Combine(_root, "v2.cinder");
        await EncryptedBundle.PackAsync(MakeCaseDirectory(), bundle, Passphrase);

        var header = File.ReadAllBytes(bundle)[..16];
        header[..10].Should().Equal("CDRBUNDLE\0"u8.ToArray());
        header[10].Should().Be(2);
        header[11..].Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public async Task Round_trips_a_directory_that_spans_many_chunks()
    {
        var source = MakeCaseDirectory();
        var bundle = Path.Combine(_root, "multi.cinder");
        await EncryptedBundle.PackWithChunkSizeAsync(source, bundle, Passphrase, SmallChunk);

        var chunks = (new FileInfo(bundle).Length - HeaderAndPreamble + StoredChunk - 1) / StoredChunk;
        chunks.Should().BeGreaterThan(50, "the point of the test is to cross many chunk boundaries");

        var outDir = Path.Combine(_root, "multi-out");
        await EncryptedBundle.UnpackAsync(bundle, outDir, Passphrase);

        AssertSameTree(source, outDir);
        Directory.Exists(Path.Combine(outDir, "empty-dir")).Should().BeTrue("empty directories are part of a case");
    }

    [Theory]
    [InlineData(0)]                       // empty archive body: one empty final chunk
    [InlineData(1)]
    [InlineData(SmallChunk - 1)]
    [InlineData(SmallChunk)]              // exactly one full chunk, which must itself be the final one
    [InlineData(SmallChunk + 1)]
    [InlineData(SmallChunk * 3)]          // ends exactly on a chunk boundary
    [InlineData(SmallChunk * 3 + 17)]
    public async Task Plaintext_of_any_length_survives_the_chunk_framing(int length)
    {
        // Seal arbitrary bytes (not a ZIP) so the length is exact, then check the stored size is
        // what the format says and that unpacking gets as far as "this is not an archive" —
        // which it can only reach after every chunk has authenticated.
        var plain = Path.Combine(_root, $"plain-{length}.bin");
        File.WriteAllBytes(plain, RandomNumberGenerator.GetBytes(length));
        var bundle = Path.Combine(_root, $"plain-{length}.cinder");
        await EncryptedBundle.EncryptZipWithChunkSizeAsync(plain, bundle, Passphrase, SmallChunk);

        var chunkCount = Math.Max(1, (length + SmallChunk - 1) / SmallChunk);
        new FileInfo(bundle).Length.Should().Be(HeaderAndPreamble + length + 16L * chunkCount);

        var act = async () => await EncryptedBundle.UnpackAsync(bundle, Path.Combine(_root, $"plain-{length}-out"), Passphrase);
        await act.Should().ThrowAsync<InvalidDataException>("authentication passed, so the only complaint left is that it is not a ZIP");
    }

    [Fact]
    public async Task Version_1_bundles_from_earlier_builds_still_open()
    {
        var source = MakeCaseDirectory(4_000);
        var zip = Path.Combine(_root, "legacy.zip");
        ZipFile.CreateFromDirectory(source, zip, CompressionLevel.SmallestSize, includeBaseDirectory: false);
        var bundle = Path.Combine(_root, "legacy.cinder");
        await EncryptedBundle.EncryptZipLegacyV1Async(zip, bundle, Passphrase);
        File.ReadAllBytes(bundle)[10].Should().Be(1);

        var outDir = Path.Combine(_root, "legacy-out");
        await EncryptedBundle.UnpackAsync(bundle, outDir, Passphrase);

        AssertSameTree(source, outDir);
    }

    [Fact]
    public async Task Version_1_bundle_with_a_wrong_passphrase_is_refused()
    {
        var zip = Path.Combine(_root, "legacy2.zip");
        ZipFile.CreateFromDirectory(MakeCaseDirectory(1_000), zip);
        var bundle = Path.Combine(_root, "legacy2.cinder");
        await EncryptedBundle.EncryptZipLegacyV1Async(zip, bundle, Passphrase);

        var outDir = Path.Combine(_root, "legacy2-out");
        var act = async () => await EncryptedBundle.UnpackAsync(bundle, outDir, "wrong");

        await act.Should().ThrowAsync<AuthenticationTagMismatchException>();
        Directory.Exists(outDir).Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_format_version_is_refused_by_name()
    {
        var bundle = Path.Combine(_root, "v9.cinder");
        await EncryptedBundle.PackAsync(MakeCaseDirectory(500), bundle, Passphrase);
        var bytes = File.ReadAllBytes(bundle);
        bytes[10] = 9;
        File.WriteAllBytes(bundle, bytes);

        var act = async () => await EncryptedBundle.UnpackAsync(bundle, Path.Combine(_root, "v9-out"), Passphrase);
        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*version 9*");
    }

    public enum Damage { FlipByteInMiddleChunk, FlipByteInHeaderSalt, FlipChunkSizeField, DropLastChunk, DropMiddleChunk, SwapTwoChunks, DuplicateAChunk, AppendAChunk, TruncateMidChunk }

    [Theory]
    [InlineData(Damage.FlipByteInMiddleChunk)]
    [InlineData(Damage.FlipByteInHeaderSalt)]
    [InlineData(Damage.FlipChunkSizeField)]
    [InlineData(Damage.DropLastChunk)]
    [InlineData(Damage.DropMiddleChunk)]
    [InlineData(Damage.SwapTwoChunks)]
    [InlineData(Damage.DuplicateAChunk)]
    [InlineData(Damage.AppendAChunk)]
    [InlineData(Damage.TruncateMidChunk)]
    public async Task A_damaged_bundle_is_refused_and_nothing_is_extracted(Damage damage)
    {
        var bundle = Path.Combine(_root, $"damaged-{damage}.cinder");
        await EncryptedBundle.PackWithChunkSizeAsync(MakeCaseDirectory(), bundle, Passphrase, SmallChunk);

        var bytes = File.ReadAllBytes(bundle).ToList();
        var chunkCount = (bytes.Count - HeaderAndPreamble + StoredChunk - 1) / StoredChunk;
        chunkCount.Should().BeGreaterThan(10);
        int Start(int chunk) => HeaderAndPreamble + chunk * StoredChunk;

        switch (damage)
        {
            case Damage.FlipByteInMiddleChunk:
                bytes[Start(chunkCount / 2) + 5] ^= 0x01;
                break;
            case Damage.FlipByteInHeaderSalt:
                bytes[16 + 3] ^= 0x01;
                break;
            case Damage.FlipChunkSizeField:
                bytes[16 + 32 + 7] ^= 0x10;   // 256 -> 272: every chunk boundary moves
                break;
            case Damage.DropLastChunk:
                bytes.RemoveRange(Start(chunkCount - 1), bytes.Count - Start(chunkCount - 1));
                break;
            case Damage.DropMiddleChunk:
                bytes.RemoveRange(Start(3), StoredChunk);
                break;
            case Damage.SwapTwoChunks:
                var a = bytes.GetRange(Start(2), StoredChunk);
                var b = bytes.GetRange(Start(5), StoredChunk);
                for (int i = 0; i < StoredChunk; i++)
                {
                    bytes[Start(2) + i] = b[i];
                    bytes[Start(5) + i] = a[i];
                }
                break;
            case Damage.DuplicateAChunk:
                bytes.InsertRange(Start(4), bytes.GetRange(Start(4), StoredChunk));
                break;
            case Damage.AppendAChunk:
                bytes.AddRange(bytes.GetRange(Start(1), StoredChunk));
                break;
            case Damage.TruncateMidChunk:
                bytes.RemoveRange(bytes.Count - 40, 40);
                break;
        }
        File.WriteAllBytes(bundle, [.. bytes]);

        var outDir = Path.Combine(_root, $"damaged-{damage}-out");
        var act = async () => await EncryptedBundle.UnpackAsync(bundle, outDir, Passphrase);

        // The refusal has to come from authentication, not from the ZIP reader happening to
        // choke on what is left: a bundle cut back to a chunk boundary is still a run of
        // individually valid chunks, and only the final-chunk flag in the nonce exposes it.
        // (Cutting inside a chunk can also leave less than one tag, which is caught by length.)
        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        if (damage == Damage.TruncateMidChunk)
        {
            thrown.Should().Match<Exception>(e => e is AuthenticationTagMismatchException || (e is InvalidDataException && e.Message.Contains("truncated")));
        }
        else
        {
            thrown.Should().BeOfType<AuthenticationTagMismatchException>();
        }
        (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
            .Should().BeFalse("a bundle that fails authentication must not leave part of a case behind");
    }

    [Fact]
    public async Task A_wrong_passphrase_fails_authentication_before_anything_is_written()
    {
        var bundle = Path.Combine(_root, "wrongpass.cinder");
        await EncryptedBundle.PackWithChunkSizeAsync(MakeCaseDirectory(), bundle, Passphrase, SmallChunk);

        var outDir = Path.Combine(_root, "wrongpass-out");
        var act = async () => await EncryptedBundle.UnpackAsync(bundle, outDir, "not the passphrase");

        (await act.Should().ThrowAsync<AuthenticationTagMismatchException>()).WithMessage("*passphrase is wrong or the bundle has been modified*");
        Directory.Exists(outDir).Should().BeFalse();
    }

    [Fact]
    public async Task Unpacks_into_an_output_path_that_ends_in_a_separator()
    {
        var source = MakeCaseDirectory(500);
        var bundle = Path.Combine(_root, "trailing.cinder");
        await EncryptedBundle.PackAsync(source, bundle, Passphrase);

        var outDir = Path.Combine(_root, "trailing-out") + Path.DirectorySeparatorChar;
        await EncryptedBundle.UnpackAsync(bundle, outDir, Passphrase);

        AssertSameTree(source, outDir);
    }

    [Fact]
    public async Task Zip_slip_is_still_refused_when_the_output_path_ends_in_a_separator()
    {
        var zipPath = Path.Combine(_root, "slip.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(zip.CreateEntry("../escaped-trailing.txt").Open());
            w.Write("should never land");
        }
        var bundle = Path.Combine(_root, "slip.cinder");
        await EncryptedBundle.EncryptZipAsync(zipPath, bundle, Passphrase);

        var outDir = Path.Combine(_root, "slip-out") + Path.DirectorySeparatorChar;
        var act = async () => await EncryptedBundle.UnpackAsync(bundle, outDir, Passphrase);

        await act.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(_root, "escaped-trailing.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task A_sibling_directory_sharing_the_output_prefix_is_not_inside_it()
    {
        // "<root>/out" must not accept an entry that resolves to "<root>/out-evil/x".
        var zipPath = Path.Combine(_root, "prefix.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(zip.CreateEntry("../prefix-out-evil/x.txt").Open());
            w.Write("no");
        }
        var bundle = Path.Combine(_root, "prefix.cinder");
        await EncryptedBundle.EncryptZipAsync(zipPath, bundle, Passphrase);

        var act = async () => await EncryptedBundle.UnpackAsync(bundle, Path.Combine(_root, "prefix-out"), Passphrase);

        await act.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(Path.Combine(_root, "prefix-out-evil")).Should().BeFalse();
    }

    [Fact]
    public async Task Packing_stages_no_plaintext_and_leaves_no_partial_file()
    {
        var source = MakeCaseDirectory(2_000);
        var outDir = Path.Combine(_root, "bundles");
        var bundle = Path.Combine(outDir, "clean.cinder");
        await EncryptedBundle.PackAsync(source, bundle, Passphrase);

        Directory.EnumerateFiles(outDir).Select(Path.GetFileName).Should().Equal("clean.cinder");
    }

    [Fact]
    public async Task A_bundle_can_be_written_inside_the_directory_it_packs()
    {
        var source = MakeCaseDirectory(2_000);
        var bundle = Path.Combine(source, "self.cinder");
        await EncryptedBundle.PackAsync(source, bundle, Passphrase);

        var outDir = Path.Combine(_root, "self-out");
        await EncryptedBundle.UnpackAsync(bundle, outDir, Passphrase);

        File.Exists(Path.Combine(outDir, "case.txt")).Should().BeTrue();
        File.Exists(Path.Combine(outDir, "self.cinder")).Should().BeFalse("the bundle does not contain itself");
    }

    [Fact]
    public async Task A_failed_pack_leaves_no_bundle_behind()
    {
        var bundle = Path.Combine(_root, "never", "made.cinder");
        var act = async () => await EncryptedBundle.PackAsync(Path.Combine(_root, "does-not-exist"), bundle, Passphrase);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
        File.Exists(bundle).Should().BeFalse();
    }
}
