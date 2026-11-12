using System.IO.Compression;
using Cinder.Filesystems;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// Live entries carry their real MFT record number. They used to be given 0 unconditionally,
/// which put a column of zeros in the Filesystem table.
/// </summary>
public sealed class WalkerRecordNumberTests
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
        image.Position = 0;
        return image;
    }

    [Fact]
    public void Live_ntfs_entries_have_distinct_non_zero_record_numbers_and_a_sequence()
    {
        using var image = LoadFixture();

        var live = DiscUtilsWalker.Walk(image).Entries.Where(e => !e.IsDeleted && e.Name.Length > 0).ToList();

        live.Should().NotBeEmpty();
        live.Should().OnlyContain(e => e.Inode > 0, "every user file on NTFS has an MFT record past the system records");
        live.Select(e => e.Inode).Should().OnlyHaveUniqueItems("two live files cannot share an MFT record");
        // User files start after the 16 reserved system records (and the records NTFS keeps for itself).
        live.Should().OnlyContain(e => e.Inode >= 16);
        live.Should().OnlyContain(e => e.Extras != null && e.Extras.ContainsKey("mft_sequence"));
    }

    [Fact]
    public void Readme_is_the_record_the_file_system_reports_for_it()
    {
        using var image = LoadFixture();

        var readme = DiscUtilsWalker.Walk(image).Entries.Single(e => e.Name == "readme.md" && !e.IsDeleted);

        // Cross-check against the file system itself rather than a number copied from a run.
        image.Position = 0;
#pragma warning disable CA1416 // read-only use; see DiscUtilsWalker for the rationale
        using var ntfs = new DiscUtils.Ntfs.NtfsFileSystem(image);
        var reference = ntfs.GetFileId(@"\readme.md");
#pragma warning restore CA1416
        readme.Inode.Should().Be(reference & 0x0000_FFFF_FFFF_FFFF);
        readme.Extras!["mft_sequence"].Should().Be(((reference >> 48) & 0xFFFF).ToString());
    }
}
