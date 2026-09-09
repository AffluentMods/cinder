using System.Security.Cryptography;
using System.Text;
using Cinder.Filesystems;
using DiscUtils;
using DiscUtils.Ntfs;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

// CA1416: same over-broad Windows annotation on DiscUtils' NTFS API as in DiscUtilsWalker;
// formatting an NTFS volume in a MemoryStream is pure managed code.
#pragma warning disable CA1416

/// <summary>
/// The filesystem walker exercised against a real NTFS volume that DiscUtils formats in memory
/// — the first parser in Cinder with a deterministic fixture rather than "it compiled".
/// </summary>
public sealed class DiscUtilsWalkerTests
{
    private const long VolumeBytes = 16L * 1024 * 1024;

    /// <summary>DiscUtils formats NTFS with 1 KiB MFT records; the fixture verifies the FILE magic before relying on it.</summary>
    private const int MftRecordSize = 1024;

    /// <summary>Formats an NTFS volume, lets <paramref name="populate"/> fill it, and returns the raw image.</summary>
    private static MemoryStream BuildNtfs(Action<NtfsFileSystem> populate)
    {
        var image = new MemoryStream();
        image.SetLength(VolumeBytes);
        var geometry = Geometry.FromCapacity(VolumeBytes);
        using (var fs = NtfsFileSystem.Format(image, "CINDER", geometry, 0, geometry.TotalSectorsLong))
        {
            populate(fs);
        }
        image.Position = 0;
        return image;
    }

    private static void WriteFile(NtfsFileSystem fs, string path, byte[] content)
    {
        using var s = fs.OpenFile(path, FileMode.Create, FileAccess.ReadWrite);
        s.Write(content, 0, content.Length);
    }

    /// <summary>
    /// Byte offset of a file's MFT record, computed while the filesystem is still open. Must be
    /// applied to the image after the filesystem is disposed (disposal flushes the MFT).
    /// </summary>
    private static long MftRecordOffset(NtfsFileSystem fs, string path)
    {
        var index = fs.GetFileId(path) & 0xFFFF_FFFF_FFFFL;
        var mftFirstCluster = fs.PathToClusters(@"$MFT")[0].Offset;
        return fs.ClusterToOffset(mftFirstCluster) + index * MftRecordSize;
    }

    /// <summary>
    /// Emulates a Windows delete. Windows clears the in-use flag in the record header and leaves
    /// every attribute in place, which is what makes deleted-name recovery possible. DiscUtils'
    /// own <c>DeleteFile</c> resets the record instead, so it cannot produce the on-disk state
    /// the walker exists to read; the flag is flipped in the image bytes directly.
    /// </summary>
    private static void ClearInUseFlag(MemoryStream image, long recordOffset)
    {
        var bytes = image.GetBuffer();
        Encoding.ASCII.GetString(bytes, (int)recordOffset, 4).Should().Be("FILE", "the offset must land on an MFT record");
        const int flagsOffset = 0x16;
        bytes[recordOffset + flagsOffset] &= 0xFE;   // MFT_RECORD_IN_USE = 0x0001
        image.Position = 0;
    }

    [Fact]
    public void Lists_live_files_and_directories_with_sizes_and_descends_subdirectories()
    {
        using var image = BuildNtfs(fs =>
        {
            fs.CreateDirectory(@"Users\alice");
            WriteFile(fs, @"Users\alice\notes.txt", Encoding.ASCII.GetBytes("hello evidence"));
            WriteFile(fs, @"readme.md", new byte[1234]);
        });

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
    }

    [Fact]
    public void Recovers_the_name_of_a_deleted_file_from_a_not_in_use_mft_record()
    {
        long recordOffset = 0;
        using var image = BuildNtfs(fs =>
        {
            WriteFile(fs, @"keep.txt", Encoding.ASCII.GetBytes("stays"));
            WriteFile(fs, @"secret-plans.docx", new byte[4096]);
            recordOffset = MftRecordOffset(fs, @"secret-plans.docx");
        });
        ClearInUseFlag(image, recordOffset);

        var r = DiscUtilsWalker.Walk(image);

        r.DeletedRecoveryError.Should().BeNull();
        r.Entries.Should().Contain(e => !e.IsDeleted && e.Name == "keep.txt");

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
    public void A_record_reset_by_the_library_yields_no_ghost_entry_and_no_error()
    {
        // DiscUtils' DeleteFile wipes the record's attributes. There is nothing to recover from
        // it, and the walk must say nothing rather than invent an entry or fail.
        using var image = BuildNtfs(fs =>
        {
            WriteFile(fs, @"gone.bin", new byte[512]);
            fs.DeleteFile(@"gone.bin");
        });

        var r = DiscUtilsWalker.Walk(image);

        r.DeletedRecoveryError.Should().BeNull();
        r.Entries.Where(e => e.IsDeleted).Should().BeEmpty();
        r.Entries.Should().NotContain(e => e.Name == "gone.bin");
    }

    [Fact]
    public void Hashes_files_and_applies_the_verdict_callback()
    {
        var content = Encoding.ASCII.GetBytes("known good bytes");
        var expectedSha1 = Convert.ToHexStringLower(SHA1.HashData(content));
        var expectedMd5 = Convert.ToHexStringLower(MD5.HashData(content));

        using var image = BuildNtfs(fs =>
        {
            WriteFile(fs, @"kernel32.dll", content);
            WriteFile(fs, @"unknown.bin", new byte[] { 1, 2, 3 });
        });

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
        r.HashedFiles.Should().Be(2);
        r.HashedBytes.Should().Be(content.Length + 3);
    }

    [Fact]
    public void Skips_hashing_files_over_the_size_limit_and_says_why()
    {
        using var image = BuildNtfs(fs => WriteFile(fs, @"big.bin", new byte[100_000]));

        var r = DiscUtilsWalker.Walk(image, new WalkOptions(HashFiles: true, HashSizeLimitBytes: 1024));

        var big = r.Entries.First(e => e.Name == "big.bin");
        big.Extras.Should().ContainKey("hash_skipped").And.NotContainKey("sha1");
        r.HashSkipped.Should().Be(1);
    }

    [Fact]
    public void Flags_truncation_instead_of_silently_stopping()
    {
        using var image = BuildNtfs(fs =>
        {
            for (int i = 0; i < 30; i++)
            {
                WriteFile(fs, $"f{i:D2}.txt", new byte[] { (byte)i });
            }
        });

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
