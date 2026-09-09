using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Cinder.Filesystems;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// The filesystem walker exercised against a real NTFS volume.
///
/// <para>The volume is a checked-in image (<c>tests/fixtures/ntfs-walker-fixture.img.gz</c>,
/// 8 MiB raw) built by <c>tools/ntfs-fixture-gen</c> on Windows. It is not built here because
/// DiscUtils can only <em>format</em> NTFS where <c>SecurityIdentifier</c> exists — on Linux the
/// constructor throws <c>PlatformNotSupportedException</c>. The walker only reads, and reading
/// works everywhere; these tests are what proves that on Linux in CI.</para>
///
/// <para>Fixture layout (keep in sync with the generator):
/// <c>Users\alice\notes.txt</c> (14 bytes), <c>readme.md</c> (1234), <c>kernel32.dll</c>
/// ("known good bytes"), <c>unknown.bin</c> (3 bytes), <c>big.bin</c> (100,000), <c>f00.txt</c> …
/// <c>f29.txt</c> (1 byte each), <c>secret-plans.docx</c> (4096, then its MFT record's in-use bit
/// cleared the way Windows deletes — attributes intact), <c>gone.bin</c> (deleted through
/// DiscUtils, which resets the record — nothing recoverable).</para>
/// </summary>
public sealed class DiscUtilsWalkerTests
{
    private static MemoryStream LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "ntfs-walker-fixture.img.gz");
        File.Exists(path).Should().BeTrue($"the fixture must be copied to the test output ({path})");

        var image = new MemoryStream();
        using (var fs = File.OpenRead(path))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        {
            gz.CopyTo(image);
        }
        image.Length.Should().Be(8L * 1024 * 1024);
        image.Position = 0;
        return image;
    }

    [Fact]
    public void Lists_live_files_and_directories_with_sizes_and_descends_subdirectories()
    {
        using var image = LoadFixture();

        var r = DiscUtilsWalker.Walk(image);

        r.Detected.Should().Contain("ntfs");
        r.Truncated.Should().BeFalse();

        var live = r.Entries.Where(e => !e.IsDeleted).ToList();
        live.Should().Contain(e => e.Name == "notes.txt" && e.Size == 14 && !e.IsDirectory);
        live.Should().Contain(e => e.Name == "readme.md" && e.Size == 1234);
        live.Should().Contain(e => e.Name == "alice" && e.IsDirectory);
        live.First(e => e.Name == "notes.txt").Path.Should().EndWith(@"Users\alice\notes.txt");
        live.First(e => e.Name == "notes.txt").ModifiedUtc.Should().NotBeNull();
        live.First(e => e.Name == "notes.txt").Extras.Should().ContainKey("attributes");
        live.Count(e => e.Name.StartsWith("f") && e.Name.EndsWith(".txt")).Should().Be(30);
    }

    [Fact]
    public void Recovers_the_name_of_a_deleted_file_from_a_not_in_use_mft_record()
    {
        using var image = LoadFixture();

        var r = DiscUtilsWalker.Walk(image);

        r.DeletedRecoveryError.Should().BeNull();

        var deleted = r.Entries.Where(e => e.IsDeleted).ToList();
        deleted.Should().ContainSingle(e => e.Name == "secret-plans.docx",
            "$FILE_NAME survives in the not-in-use record, and the long name is preferred over SECRET~1.DOC");
        var d = deleted.Single(e => e.Name == "secret-plans.docx");
        d.Path.Should().Contain("[deleted]/");
        d.Size.Should().Be(4096);
        d.CreatedUtc.Should().NotBeNull();
        d.Extras.Should().ContainKey("mft_index").And.ContainKey("mft_sequence");
    }

    [Fact]
    public void A_record_reset_by_the_library_yields_no_ghost_entry()
    {
        // gone.bin was deleted through DiscUtils, which wipes the record's attributes. There is
        // nothing to recover, and the walk must say nothing rather than invent an entry.
        using var image = LoadFixture();

        var r = DiscUtilsWalker.Walk(image);

        r.Entries.Should().NotContain(e => e.Name == "gone.bin");
        r.Entries.Where(e => e.IsDeleted).Should().OnlyContain(e => e.Name == "secret-plans.docx");
    }

    [Fact]
    public void Hashes_files_and_applies_the_verdict_callback()
    {
        var content = Encoding.ASCII.GetBytes("known good bytes");
        var expectedSha1 = Convert.ToHexStringLower(SHA1.HashData(content));
        var expectedMd5 = Convert.ToHexStringLower(MD5.HashData(content));

        using var image = LoadFixture();
        var r = DiscUtilsWalker.Walk(image, new WalkOptions(
            HashFiles: true,
            Lookup: (sha1, md5) => sha1 == expectedSha1 && md5 == expectedMd5
                ? new HashVerdict("Known", "NSRL_test", "kernel32.dll")
                : null));

        var known = r.Entries.First(e => e.Name == "kernel32.dll");
        known.Extras!["sha1"].Should().Be(expectedSha1);
        known.Extras!["md5"].Should().Be(expectedMd5);
        known.Extras!["verdict"].Should().Be("Known");
        known.Extras!["hash_set"].Should().Be("NSRL_test");

        r.Entries.First(e => e.Name == "unknown.bin").Extras!["verdict"].Should().Be("Unknown");
        r.Entries.First(e => e.Name == "unknown.bin").Extras!["sha1"]
            .Should().Be(Convert.ToHexStringLower(SHA1.HashData(new byte[] { 1, 2, 3 })));
        r.HashedFiles.Should().BeGreaterThan(30);
        r.Entries.Where(e => e.IsDeleted).Should().OnlyContain(e => e.Extras == null || !e.Extras.ContainsKey("sha1"),
            "deleted entries have no readable content to hash");
    }

    [Fact]
    public void Skips_hashing_files_over_the_size_limit_and_says_why()
    {
        using var image = LoadFixture();

        var r = DiscUtilsWalker.Walk(image, new WalkOptions(HashFiles: true, HashSizeLimitBytes: 50_000));

        var big = r.Entries.First(e => e.Name == "big.bin");
        big.Size.Should().Be(100_000);
        big.Extras.Should().ContainKey("hash_skipped").And.NotContainKey("sha1");
        r.HashSkipped.Should().Be(1);
        r.Entries.First(e => e.Name == "readme.md").Extras.Should().ContainKey("sha1", "files under the limit are still hashed");
    }

    [Fact]
    public void Flags_truncation_instead_of_silently_stopping()
    {
        using var image = LoadFixture();

        var r = DiscUtilsWalker.Walk(image, new WalkOptions(MaxEntries: 10));

        r.Truncated.Should().BeTrue();
        r.Entries.Count(e => !e.IsDeleted).Should().Be(10);
    }

    [Fact]
    public void Non_filesystem_bytes_produce_no_entries_and_no_exception()
    {
        using var junk = new MemoryStream(new byte[1 << 20]);
        var r = DiscUtilsWalker.Walk(junk);
        r.Entries.Should().BeEmpty();
        r.Detected.Should().BeEmpty();
    }
}
