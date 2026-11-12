using System.IO.Compression;
using System.Runtime.CompilerServices;
using Cinder.App.ViewModels.Tools;
using Cinder.Core.Export;
using Cinder.Filesystems;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>
/// The Filesystem tool's table. The rule under test: a cell shows a value only when the value
/// is known. A record number the walker did not supply is an empty cell, not 0; a folder has
/// no size; and a column that would be empty on every row is not shown at all.
/// </summary>
public class FilesystemToolRowsTests
{
    private static readonly DateTimeOffset T = new(2024, 7, 1, 8, 30, 0, TimeSpan.Zero);

    private static FileEntry Entry(string path, long inode = 0, long size = 0, bool dir = false, bool deleted = false,
                                   Dictionary<string, string>? extras = null) =>
        new(inode, path, path[(path.LastIndexOfAny([(char)92, '/']) + 1)..], size, dir, deleted, T, T, T, null, null, null, null,
            extras ?? new Dictionary<string, string> { ["attributes"] = dir ? "Directory" : "Archive" });

    private static IReadOnlyDictionary<string, string?> Cells(object row) => TabularExporter.ToDictionary(row);

    // ---------------------------------------------------------------- Record numbers

    [Fact]
    public void A_live_entry_the_walker_gave_no_record_number_for_has_an_empty_cell_not_zero()
    {
        // The walker reports Inode 0 for every live entry. Shown as-is that was a column of
        // zeros, which reads as "every file is MFT record 0".
        FilesystemTool.KnownInode(Entry(@"\readme.md", inode: 0, size: 1234)).Should().BeNull();
        FilesystemTool.ToCells(Entry(@"\readme.md", inode: 0, size: 1234), hashed: false).Inode.Should().BeNull();
    }

    [Fact]
    public void A_deleted_ntfs_entry_shows_the_record_it_was_recovered_from()
    {
        var deleted = Entry(@"[deleted]/secret-plans.docx", inode: 58, size: 4096, deleted: true,
            extras: new() { ["mft_index"] = "58", ["mft_sequence"] = "2", ["note"] = "Deleted — MFT record not in use." });

        var cells = FilesystemTool.ToCells(deleted, hashed: false);

        cells.Inode.Should().Be(58);
        cells.Deleted.Should().Be("deleted");
        cells.Note.Should().Contain("MFT record not in use");
    }

    [Fact]
    public void A_record_number_is_shown_whenever_one_is_supplied_including_record_zero_with_its_reference()
    {
        // What the walker will hand over once it reads the number from the filesystem.
        FilesystemTool.KnownInode(Entry(@"\readme.md", inode: 32)).Should().Be(32);
        // Record 0 is real on NTFS ($MFT). With the rest of the reference present it is a value, not a blank.
        FilesystemTool.KnownInode(Entry(@"\$MFT", inode: 0, extras: new() { ["mft_sequence"] = "1" })).Should().Be(0);
    }

    // ---------------------------------------------------------------- Kind, size, deleted

    [Fact]
    public void Kind_says_file_or_folder_and_a_folder_has_no_size()
    {
        var file = FilesystemTool.ToCells(Entry(@"\Users\alice\notes.txt", size: 14), hashed: false);
        var empty = FilesystemTool.ToCells(Entry(@"\empty.txt", size: 0), hashed: false);
        var folder = FilesystemTool.ToCells(Entry(@"\Users", dir: true), hashed: false);

        file.Kind.Should().Be("File");
        file.Size.Should().Be(14);
        empty.Size.Should().Be(0, "an empty file really is zero bytes");
        folder.Kind.Should().Be("Folder");
        folder.Size.Should().BeNull("a folder has no size of its own, and 0 would read as a measurement");
        file.Deleted.Should().BeEmpty("only deleted entries say so");
    }

    [Fact]
    public void Remarks_about_the_image_are_marked_as_notes_and_carry_no_numbers()
    {
        var note = FilesystemTool.NoteCells("[hashing]", "Hashed 10 files (1,000 bytes) and skipped 0.");

        note.Kind.Should().Be(FilesystemTool.KindNote);
        note.Size.Should().BeNull();
        note.Inode.Should().BeNull();
        note.Deleted.Should().BeEmpty();
        note.Modified.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Columns

    [Fact]
    public void A_listing_with_no_record_numbers_and_no_hashing_has_neither_column()
    {
        var rows = FilesystemTool.BuildRows(
        [
            FilesystemTool.ToCells(Entry(@"\a.txt", size: 1), hashed: false),
            FilesystemTool.ToCells(Entry(@"\dir", dir: true), hashed: false),
        ], hashed: false);

        Cells(rows[0]).Keys.Should().Equal("Path", "Name", "Kind", "Size", "Deleted", "Modified", "Created", "Accessed", "Note");
        rows.Select(r => r.GetType()).Distinct().Should().ContainSingle("the grid and the export need one shape per result");
    }

    [Fact]
    public void One_known_record_number_brings_the_column_back_and_the_others_stay_empty()
    {
        var rows = FilesystemTool.BuildRows(
        [
            FilesystemTool.NoteCells("[image details]", "E01 image…"),
            FilesystemTool.ToCells(Entry(@"\readme.md", size: 1234), hashed: false),
            FilesystemTool.ToCells(Entry(@"\Users", dir: true), hashed: false),
            FilesystemTool.ToCells(Entry(@"[deleted]/secret-plans.docx", inode: 58, size: 4096, deleted: true), hashed: false),
        ], hashed: false);

        Cells(rows[0]).Keys.First().Should().Be("Inode");
        rows.Select(r => Cells(r)["Inode"]).Should().Equal(null, null, null, "58");
        rows.Select(r => Cells(r)["Kind"]).Should().Equal("Note", "File", "Folder", "File");
        rows.Select(r => Cells(r)["Deleted"]).Should().Equal("", "", "", "deleted");
    }

    [Fact]
    public void Hash_columns_appear_only_when_files_were_hashed()
    {
        var hashedEntry = Entry(@"\kernel32.dll", size: 16, extras: new()
        {
            ["attributes"] = "Archive",
            ["sha1"] = "da39a3ee5e6b4b0d3255bfef95601890afd80709",
            ["verdict"] = "Known",
            ["hash_set"] = "NSRL",
        });

        var with = FilesystemTool.BuildRows([FilesystemTool.ToCells(hashedEntry, hashed: true)], hashed: true);
        var without = FilesystemTool.BuildRows([FilesystemTool.ToCells(hashedEntry, hashed: false)], hashed: false);

        Cells(with[0])["Verdict"].Should().Be("Known");
        Cells(with[0])["Sha1"].Should().Be("da39a3ee5e6b4b0d3255bfef95601890afd80709");
        Cells(with[0])["Note"].Should().StartWith("Known in NSRL");
        Cells(without[0]).Keys.Should().NotContain(["Verdict", "Sha1"]);
    }

    [Fact]
    public void A_hashed_file_no_hash_set_knows_is_unknown_and_folders_have_no_verdict()
    {
        var file = FilesystemTool.ToCells(Entry(@"\unknown.bin", size: 3, extras: new() { ["sha1"] = "0123456789abcdef0123456789abcdef01234567" }), hashed: true);
        var folder = FilesystemTool.ToCells(Entry(@"\Users", dir: true), hashed: true);

        file.Verdict.Should().Be("Unknown");
        folder.Verdict.Should().BeEmpty();
    }

    [Fact]
    public void The_export_carries_no_placeholder_values()
    {
        var rows = FilesystemTool.BuildRows(
        [
            FilesystemTool.ToCells(Entry(@"\readme.md", size: 1234), hashed: false),
            FilesystemTool.ToCells(Entry(@"\Users", dir: true), hashed: false),
            FilesystemTool.ToCells(Entry(@"[deleted]/gone.docx", inode: 58, size: 4096, deleted: true), hashed: false),
        ], hashed: false);

        var csv = TabularExporter.ToCsv(rows).ReplaceLineEndings("\n").Split('\n');

        csv[0].Should().Be("Inode,Path,Name,Kind,Size,Deleted,Modified,Created,Accessed,Note");
        csv[1].Should().StartWith(@",\readme.md,readme.md,File,1234,,", "no record number is an empty cell, and a live file is not marked");
        csv[2].Should().StartWith(@",\Users,Users,Folder,,,", "a folder has neither a record number here nor a size");
        csv[3].Should().StartWith("58,[deleted]/gone.docx,gone.docx,File,4096,deleted,");
    }

    // ---------------------------------------------------------------- Notes

    [Fact]
    public void A_folder_whose_contents_were_not_listed_says_so()
    {
        var cells = FilesystemTool.ToCells(Entry(@"\deep\deeper", dir: true, extras: new()
        {
            ["attributes"] = "Directory",
            [DiscUtilsWalker.NotDescendedKey] = "depth limit (64) reached",
        }), hashed: false);

        cells.Note.Should().StartWith("contents not listed: depth limit (64) reached");
    }

    [Fact]
    public void Fat_times_are_shown_as_local_and_never_marked_utc()
    {
        var cells = FilesystemTool.ToCells(Entry(@"\DCIM\IMG_0001.JPG", size: 10, extras: new()
        {
            ["attributes"] = "Archive",
            [DiscUtilsWalker.TimeBasisKey] = DiscUtilsWalker.TimeBasisLocalUnknownZone,
        }), hashed: false);

        cells.Modified.Should().Be("2024-07-01 08:30:00 (local)");
        cells.Modified.Should().NotContain("Z");
        cells.Note.Should().Contain("time zone not recorded");
    }

    // ---------------------------------------------------------------- The fixture image

    /// <summary>
    /// The checked-in NTFS image, unpacked to a temp file. Looked for in the test output (where
    /// the Core tests' project copies it) and then beside this source file's project. Returns
    /// null when neither exists, so the test is a no-op in a layout that has no fixture.
    /// </summary>
    private static string? UnpackFixture([CallerFilePath] string thisFile = "")
    {
        const string name = "ntfs-walker-fixture.img.gz";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "fixtures", name),
            Path.Combine(Path.GetDirectoryName(thisFile) ?? "", "..", "fixtures", name),
        };
        var gz = candidates.FirstOrDefault(File.Exists);
        if (gz is null)
        {
            return null;
        }
        var image = Path.Combine(Path.GetTempPath(), "cinder-fixture-" + Guid.NewGuid().ToString("N") + ".img");
        using var input = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress);
        using var output = File.Create(image);
        input.CopyTo(output);
        return image;
    }

    [Fact]
    public async Task The_ntfs_fixture_lists_without_a_column_of_zeros_Async()
    {
        var image = UnpackFixture();
        if (image is null)
        {
            return;
        }
        try
        {
            var tool = new FilesystemTool();
            await tool.LoadEvidenceCommand.ExecuteAsync(image);

            tool.ErrorMessage.Should().BeNull();
            var rows = tool.Rows.Select(Cells).ToList();
            rows.Should().NotBeEmpty();

            // No cell anywhere claims record number 0, and no True/False flag columns remain.
            rows.Should().OnlyContain(r => !r.ContainsKey("IsDirectory") && !r.ContainsKey("IsDeleted") && !r.ContainsKey("Owner"));
            // Live entries: empty while the walker supplies no number, a real number once it
            // does. Never 0, which is what every row used to say.
            rows.Where(r => r["Deleted"] != "deleted").Should().OnlyContain(r => r["Inode"] != "0");

            var deleted = rows.Where(r => r["Deleted"] == "deleted").ToList();
            deleted.Should().ContainSingle().Which["Name"].Should().Be("secret-plans.docx");
            deleted[0]["Inode"].Should().Be("58");

            rows.Should().Contain(r => r["Name"] == "notes.txt" && r["Kind"] == "File" && r["Size"] == "14");
            rows.Should().Contain(r => r["Name"] == "alice" && r["Kind"] == "Folder" && r["Size"] == null);
        }
        finally
        {
            File.Delete(image);
        }
    }
}
