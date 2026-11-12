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

        var r = DiscUtilsWalker.Walk(image, new WalkOptions(HashFiles: true, HashSizeLimitBytes: 20_000));

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

    // ---- hashing caps are enforced on the bytes read, not on the size the entry claims ----

    [Fact]
    public void A_file_whose_directory_entry_understates_its_size_is_not_hashed_past_the_limit()
    {
        // NTFS keeps a file's size twice: in the directory index entry (what a listing shows)
        // and in the file record's $DATA attribute (what a read returns). Make the index entry
        // claim 10 bytes for the 100,000-byte big.bin, the way a crafted image would.
        using var image = LoadFixture();
        var raw = image.GetBuffer();
        var name = Encoding.Unicode.GetBytes("big.bin");
        var patched = 0;
        for (var i = raw.AsSpan(0, (int)image.Length).IndexOf(name); i >= 0;)
        {
            var value = i - 0x42;                       // start of the $FILE_NAME structure
            if (value >= 0 && raw[i - 2] == 7)          // name length in characters
            {
                BitConverter.TryWriteBytes(raw.AsSpan(value + 0x28, 8), 10L);   // allocated size
                BitConverter.TryWriteBytes(raw.AsSpan(value + 0x30, 8), 10L);   // real size
                patched++;
            }
            var next = raw.AsSpan(i + 1, (int)image.Length - i - 1).IndexOf(name);
            i = next < 0 ? -1 : i + 1 + next;
        }
        patched.Should().BeGreaterThan(0, "the fixture must contain big.bin's $FILE_NAME");

        var r = DiscUtilsWalker.Walk(image, new WalkOptions(HashFiles: true, HashSizeLimitBytes: 20_000));

        var big = r.Entries.First(e => e.Name == "big.bin");
        big.Size.Should().Be(10, "the listing shows what the directory entry says");
        big.Extras.Should().ContainKey("hash_skipped").And.NotContainKey("sha1").And.NotContainKey("md5");
        big.Extras!["hash_skipped"].Should().Contain("over size limit");
        r.HashedBytes.Should().BeLessThan(100_000, "reading stopped at the limit instead of running to the end of the data");
    }

    [Fact]
    public void HashStream_stops_at_the_size_limit_and_at_the_walk_budget()
    {
        var data = new byte[300_000];
        new Random(7).NextBytes(data);

        DiscUtilsWalker.HashStream(new MemoryStream(data), sizeLimit: 64 * 1024, budgetRemaining: long.MaxValue, default,
                out var md5, out _, out var read)
            .Should().Be(DiscUtilsWalker.HashOutcome.OverSizeLimit);
        md5.Should().BeEmpty();
        read.Should().BeInRange(64 * 1024 + 1, 2 * 64 * 1024);

        DiscUtilsWalker.HashStream(new MemoryStream(data), sizeLimit: long.MaxValue, budgetRemaining: 100_000, default,
                out _, out _, out read)
            .Should().Be(DiscUtilsWalker.HashOutcome.OverTotalBudget);
        read.Should().BeLessThan(200_000);

        DiscUtilsWalker.HashStream(new MemoryStream(data), sizeLimit: 300_000, budgetRemaining: 300_000, default,
                out md5, out var sha1, out read)
            .Should().Be(DiscUtilsWalker.HashOutcome.Hashed, "a file exactly at the limit is still hashed");
        read.Should().Be(300_000);
        md5.Should().Be(Convert.ToHexStringLower(MD5.HashData(data)));
        sha1.Should().Be(Convert.ToHexStringLower(SHA1.HashData(data)));
    }

    [Fact]
    public void HashStream_observes_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => DiscUtilsWalker.HashStream(new MemoryStream(new byte[10]), long.MaxValue, long.MaxValue, cts.Token, out _, out _, out _);
        act.Should().Throw<OperationCanceledException>();
    }

    // ---- FAT: zone-less timestamps ------------------------------------------------------------

    private static readonly DateTime FatStamp = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Unspecified);

    /// <summary>A FAT12 floppy image built in memory (FAT formatting, unlike NTFS, works everywhere).</summary>
    private static MemoryStream NewFat(Action<DiscUtils.Fat.FatFileSystem> populate)
    {
        var ms = new MemoryStream();
        using (DiscUtils.Fat.FatFileSystem.FormatFloppy(ms, DiscUtils.FloppyDiskType.HighDensity, "CINDER"))
        {
        }
        // Identity time conversion while writing, so the bytes on disk are exactly the stamps
        // the test names — whatever zone the build machine is in.
        using (var fs = new DiscUtils.Fat.FatFileSystem(ms, DiscUtils.Streams.Ownership.None, static (t, _) => t))
        {
            populate(fs);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Fat_timestamps_are_reported_as_recorded_and_marked_zone_unknown()
    {
        using var image = NewFat(fs =>
        {
            using (var f = fs.OpenFile("A.TXT", FileMode.Create)) f.WriteByte(1);
            fs.SetCreationTime("A.TXT", FatStamp);
            fs.SetLastWriteTime("A.TXT", FatStamp);
        });

        var r = DiscUtilsWalker.Walk(image);

        r.Detected.Should().Contain("fat");
        var e = r.Entries.Single(x => x.Name.Equals("A.TXT", StringComparison.OrdinalIgnoreCase));

        // 12:00:00 is on the disk; 12:00:00 is reported. The old code let DiscUtils assume the
        // examiner's own zone and reported, on a UTC-8 workstation, 20:00:00Z.
        var asRecorded = new DateTimeOffset(FatStamp, TimeSpan.Zero);
        e.CreatedUtc.Should().Be(asRecorded);
        e.ModifiedUtc.Should().Be(asRecorded);

        e.Extras.Should().ContainKey(DiscUtilsWalker.TimeBasisKey)
            .WhoseValue.Should().Be(DiscUtilsWalker.TimeBasisLocalUnknownZone);
        e.TimesAreLocalWallClock.Should().BeTrue("the reading is wall-clock time in an unrecorded zone, not UTC");
    }

    [Fact]
    public void A_timestamp_of_unspecified_kind_is_not_shifted_by_the_workstation_zone()
    {
        // The conversion that made results machine-dependent, pinned in isolation: an
        // Unspecified DateTime must come back with the same clock reading on every machine.
        DiscUtilsWalker.AsUtc(FatStamp).Should().Be(new DateTimeOffset(FatStamp, TimeSpan.Zero));
        DiscUtilsWalker.AsUtc(DateTime.SpecifyKind(FatStamp, DateTimeKind.Utc)).Should().Be(new DateTimeOffset(FatStamp, TimeSpan.Zero));

        var local = DateTime.SpecifyKind(FatStamp, DateTimeKind.Local);
        DiscUtilsWalker.AsUtc(local).Should().Be(new DateTimeOffset(local.ToUniversalTime(), TimeSpan.Zero),
            "a value that really is local time is still converted");
    }

    [Fact]
    public void Ntfs_timestamps_are_utc_and_carry_no_zone_unknown_marker()
    {
        using var image = LoadFixture();
        var r = DiscUtilsWalker.Walk(image);
        r.Entries.Should().OnlyContain(e => !e.TimesAreLocalWallClock);
    }

    // ---- depth, path-length and cycle guards -------------------------------------------------

    [Fact]
    public void Directories_at_the_depth_limit_are_listed_but_not_descended_and_the_walk_says_so()
    {
        using var image = NewFat(fs =>
        {
            fs.CreateDirectory(@"A\B\C\D\E");
            using var f = fs.OpenFile(@"A\B\C\D\DEEP.TXT", FileMode.Create);
            f.WriteByte(1);
        });

        var r = DiscUtilsWalker.Walk(image, new WalkOptions(MaxDepth: 3));

        r.Entries.Select(e => e.Name).Should().BeEquivalentTo(["A", "B", "C"]);
        r.Entries.Single(e => e.Name == "C").Extras.Should().ContainKey(DiscUtilsWalker.NotDescendedKey);
        r.Entries.Single(e => e.Name == "B").Extras.Should().NotContainKey(DiscUtilsWalker.NotDescendedKey);
        r.DepthTruncated.Should().BeTrue();
        r.Truncated.Should().BeTrue("a caller that only checks Truncated must still learn the listing is incomplete");

        image.Position = 0;
        var full = DiscUtilsWalker.Walk(image);
        full.DepthTruncated.Should().BeFalse();
        full.Truncated.Should().BeFalse();
        full.Entries.Should().Contain(e => e.Name == "DEEP.TXT");
    }

    [Fact]
    public void Directories_past_the_path_length_limit_are_not_descended()
    {
        using var image = NewFat(fs => fs.CreateDirectory(@"LONGNAME\LONGNAM2\X"));

        var r = DiscUtilsWalker.Walk(image, new WalkOptions(MaxPathLength: 12));

        r.Entries.Select(e => e.Name).Should().BeEquivalentTo(["LONGNAME", "LONGNAM2"]);
        r.Entries.Single(e => e.Name == "LONGNAM2").Extras.Should().ContainKey(DiscUtilsWalker.NotDescendedKey);
        r.DepthTruncated.Should().BeTrue();
    }

    [Fact(Timeout = 120_000)]
    public Task A_directory_that_contains_itself_does_not_run_the_walk_to_the_entry_budget() => Task.Run(() =>
    {
        // LOOP\SUB's directory entry is repointed at LOOP's own first cluster, so SUB's listing
        // is LOOP's listing: LOOP\SUB\SUB\SUB\… without end.
        using var image = NewFat(fs => fs.CreateDirectory(@"LOOP\SUB"));
        var raw = image.GetBuffer();
        var len = (int)image.Length;
        var loopEntry = FindDirEntry(raw, len, "LOOP       ");
        var subEntry = FindDirEntry(raw, len, "SUB        ");
        raw[subEntry + 26] = raw[loopEntry + 26];   // first cluster, low word
        raw[subEntry + 27] = raw[loopEntry + 27];

        var r = DiscUtilsWalker.Walk(image);

        r.DepthTruncated.Should().BeTrue();
        r.Truncated.Should().BeTrue();
        r.Entries.Count.Should().BeLessThanOrEqualTo(new WalkOptions().MaxDepth + 1,
            "the walk stops at the depth limit instead of following the cycle to the 25,000-entry budget");
        r.Entries.Max(e => e.Path.Length).Should().BeLessThan(1024);
    });

    /// <summary>Offset of a FAT short-name directory entry (attribute byte says "directory").</summary>
    private static int FindDirEntry(byte[] raw, int length, string name83)
    {
        var needle = Encoding.ASCII.GetBytes(name83);
        for (var i = 0; i + 32 <= length; i += 32)
        {
            if (raw.AsSpan(i, 11).SequenceEqual(needle) && (raw[i + 11] & 0x10) != 0)
            {
                return i;
            }
        }
        throw new InvalidOperationException($"directory entry '{name83}' not found");
    }
}
