using System.Buffers.Binary;
using System.Text.Json;
using Cinder.App.ViewModels;
using Cinder.App.ViewModels.Tools;
using Cinder.Core.Export;
using Cinder.Reports;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>
/// Second pass over the parser tools: what the indicator tool records and clears, what the
/// tools say when something they need is missing, folder walks that survive a locked
/// subfolder, and where export and report failures are reported.
/// </summary>
public class ParserToolsRoundTwoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-tests-" + Guid.NewGuid().ToString("N"));

    public ParserToolsRoundTwoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = default }))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that outlives the run is not a test failure.
        }
    }

    private string NewFolder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string NewFile(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static IReadOnlyDictionary<string, string?> Cells(object row) => TabularExporter.ToDictionary(row);

    // ---------------------------------------------------------------- Indicator tool

    [Fact]
    public async Task The_scanned_folder_is_the_evidence_and_the_list_is_what_it_was_checked_with_Async()
    {
        var folder = NewFolder("triage");
        var list = NewFile("iocs.txt", "evil.example\n");
        var tool = new IocMatchTool { TargetFolder = folder };
        await tool.SetIndicatorListAsync(list);

        var entry = JsonSerializer.SerializeToElement(tool.DescribeRunForCustody(rows: 3, truncated: false));

        entry.GetProperty("Evidence").GetString().Should().Be(folder, "the folder is what was examined");
        entry.GetProperty("Input").GetString().Should().Be(list, "the list is what it was examined with");
        entry.GetProperty("Tool").GetString().Should().Be("ioc");
        entry.GetProperty("Rows").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task For_an_ordinary_parser_the_picked_file_is_the_evidence_and_there_is_no_input_Async()
    {
        var file = NewFile("notes.eml", "From: a@example.com\n\nbody\n");
        var tool = new EmailTool();
        await tool.LoadEvidenceCommand.ExecuteAsync(file);

        var entry = JsonSerializer.SerializeToElement(tool.DescribeRunForCustody(1, false));

        entry.GetProperty("Evidence").GetString().Should().Be(file);
        entry.GetProperty("Input").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private async Task<IocMatchTool> ScannedAsync()
    {
        var folder = NewFolder("triage");
        File.WriteAllText(Path.Combine(folder, "beacon.cfg"), "callback=evil-domain.example;sleep=60");
        var list = NewFile("iocs.txt", "evil-domain.example\n");
        var tool = new IocMatchTool { TargetFolder = folder };
        await tool.SetIndicatorListAsync(list);
        await tool.LoadEvidenceCommand.ExecuteAsync(list);
        tool.ErrorMessage.Should().BeNull();
        tool.Rows.Should().NotBeEmpty("the indicator is in a file in the folder");
        return tool;
    }

    [Fact]
    public async Task A_scan_reports_what_it_looked_at_and_where_it_matched_Async()
    {
        var tool = await ScannedAsync();

        tool.IndicatorSummary.Should().StartWith("1 indicator checked against 1 file:");
        tool.Rows.Select(r => Cells(r)["Location"]).Should().OnlyContain(l => l!.EndsWith("beacon.cfg"));
        tool.StatusLine.Should().StartWith("Scanned ");
        tool.CanRunScan.Should().BeTrue();
    }

    [Fact]
    public async Task Picking_another_folder_clears_the_matches_from_the_previous_one_Async()
    {
        var tool = await ScannedAsync();

        tool.TargetFolder = NewFolder("other");

        tool.Rows.Should().BeEmpty("those matches were for a different folder");
        tool.VisibleRows.Should().BeEmpty();
        tool.StatusLine.Should().Be("The folder changed. Run the scan again to see matches for it.");
        tool.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task Picking_another_list_clears_the_matches_from_the_previous_one_Async()
    {
        var tool = await ScannedAsync();
        var otherList = NewFile("other-iocs.txt", "# nothing that matches\n203.0.113.9\n44d88612fea8a8f36de82e1278abb02f\n");

        await tool.SetIndicatorListAsync(otherList);

        tool.Rows.Should().BeEmpty("those matches were for a different list");
        tool.EvidencePath.Should().Be(otherList);
        tool.StatusLine.Should().Be("The indicator list changed. Run the scan again to see matches for it.");
        tool.IndicatorSummary.Should().StartWith("2 indicators:");
    }

    [Fact]
    public async Task Stopping_a_scan_shows_no_matches_and_says_why_Async()
    {
        var folder = NewFolder("triage");
        File.WriteAllText(Path.Combine(folder, "beacon.cfg"), "callback=evil-domain.example");
        var list = NewFile("iocs.txt", "evil-domain.example\n");
        var tool = new IocMatchTool { TargetFolder = folder };
        await tool.SetIndicatorListAsync(list);

        var run = tool.LoadEvidenceCommand.ExecuteAsync(list);
        tool.LoadEvidenceCancelCommand.Execute(null);
        await run;

        tool.StatusLine.Should().Be("Stopped. The scan did not finish, so no matches are shown.");
        tool.Rows.Should().BeEmpty();
        tool.ErrorMessage.Should().BeNull("stopping is not an error");
        tool.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task Running_without_a_folder_is_a_scan_error_not_a_file_error_Async()
    {
        var list = NewFile("iocs.txt", "evil-domain.example\n");
        var tool = new IocMatchTool();
        await tool.SetIndicatorListAsync(list);
        tool.CanRunScan.Should().BeFalse();

        await tool.LoadEvidenceCommand.ExecuteAsync(list);

        tool.ErrorTitle.Should().Be("Could not run the scan");
        tool.ErrorMessage.Should().Be("Pick the folder to scan, then run the scan again.");
        tool.StatusLine.Should().Be("The scan did not run.");
    }

    [Fact]
    public void Scan_summary_states_everything_that_could_have_hidden_a_match()
    {
        IocMatchTool.DescribeScan(12, 3400, 3398, 3100, 250, unreadable: 0, damagedChunks: 0)
            .Should().Be("12 indicators checked against 3,400 files: 3,398 hashed, 3,100 searched for text, 250 timeline events read.");

        var weak = IocMatchTool.DescribeScan(1, 1, 1, 1, 1, unreadable: 2, damagedChunks: 7);
        weak.Should().Contain("2 files could not be read, so a match in them could have been missed.");
        weak.Should().EndWith("7 damaged chunks in the source read as zeros, so a match there could have been missed.");

        IocMatchTool.DescribeScan(1, 1, 1, 1, 1, unreadable: 1, damagedChunks: 1)
            .Should().Contain("1 file could not be read").And.Contain("1 damaged chunk in the source read as zeros");
    }

    [Fact]
    public void Scan_summary_names_the_images_that_had_damaged_chunks()
    {
        string[] images = [@"E:\case\disk1.E01", @"E:\case\disk2.E01", @"E:\case\stick.aff4", @"E:\case\d4.E01", @"E:\case\d5.E01"];

        var text = IocMatchTool.DescribeScan(5, 5, 0, 5, 0, unreadable: 0, damagedChunks: 12, images);

        text.Should().Contain("12 damaged chunks in the source read as zeros, so a match there could have been missed.");
        text.Should().EndWith("Affected: disk1.E01, disk2.E01, stick.aff4 and 2 more.");
    }

    [Fact]
    public async Task What_weakens_a_run_goes_into_its_custody_entry_Async()
    {
        var tool = await ScannedAsync();

        var entry = JsonSerializer.SerializeToElement(tool.DescribeRunForCustody(1, false,
            new { FilesUnreadable = 2, DamagedChunks = 7 }));

        entry.GetProperty("Detail").GetProperty("DamagedChunks").GetInt32().Should().Be(7);
        entry.GetProperty("Detail").GetProperty("FilesUnreadable").GetInt32().Should().Be(2);
    }

    // ---------------------------------------------------------------- Network captures

    [Fact]
    public void A_missing_capture_library_is_named_with_what_to_install()
    {
        NetworkTool.MissingLibraryMessage(onWindows: true).Should().StartWith("Npcap is not installed;").And.Contain("npcap.com");
        NetworkTool.MissingLibraryMessage(onWindows: false).Should().StartWith("libpcap is not installed;");
    }

    [Fact]
    public void A_library_that_cannot_be_loaded_is_recognised_however_deeply_it_is_wrapped()
    {
        NetworkTool.IsMissingLibrary(new DllNotFoundException("Unable to load DLL 'wpcap'")).Should().BeTrue();
        NetworkTool.IsMissingLibrary(new TypeInitializationException("SharpPcap.LibPcap.LibPcapSafeNativeMethods",
            new DllNotFoundException("wpcap"))).Should().BeTrue();
        NetworkTool.IsMissingLibrary(new InvalidOperationException("outer", new InvalidDataException("bad magic"))).Should().BeFalse();
        NetworkTool.IsMissingLibrary(null).Should().BeFalse();
    }

    [Fact]
    public void A_capture_that_ends_in_a_damaged_record_is_cut_short_with_the_count_read()
    {
        NetworkTool.DescribeBrokenEnd(endedNormally: true, packetsRead: 1200, "NoRemainingPackets").Should().BeNull();

        var broken = NetworkTool.DescribeBrokenEnd(endedNormally: false, packetsRead: 1200, "Error");
        broken.Should().StartWith("Showing the 1,200 packets read before a damaged record");
        broken.Should().Contain("cut short or corrupt").And.Contain("Export and bookmarks cover only the packets shown");

        NetworkTool.DescribeBrokenEnd(endedNormally: false, packetsRead: 0, "Error").Should().StartWith("No packets could be read");
    }

    /// <summary>A classic pcap: one Ethernet/IPv4/TCP packet repeated, then an optional record that promises more bytes than exist.</summary>
    private static byte[] Pcap(int packets, bool damagedTail)
    {
        var packet = new byte[54];
        packet[12] = 0x08;                                   // EtherType IPv4
        packet[14] = 0x45;                                   // version 4, header 20 bytes
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(16), 40);
        packet[22] = 64;                                     // TTL
        packet[23] = 6;                                      // TCP
        new byte[] { 10, 0, 0, 1 }.CopyTo(packet, 26);
        new byte[] { 10, 0, 0, 2 }.CopyTo(packet, 30);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(34), 49152);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(36), 443);
        packet[46] = 0x50;                                   // data offset 5
        packet[47] = 0x02;                                   // SYN

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0xA1B2C3D4u);
        w.Write((ushort)2);
        w.Write((ushort)4);
        w.Write(0);
        w.Write(0u);
        w.Write(65535u);
        w.Write(1u);                                         // Ethernet
        for (var i = 0; i < packets; i++)
        {
            w.Write(1_700_000_000u + (uint)i);
            w.Write(0u);
            w.Write((uint)packet.Length);
            w.Write((uint)packet.Length);
            w.Write(packet);
        }
        if (damagedTail)
        {
            w.Write(1_700_000_999u);
            w.Write(0u);
            w.Write((uint)packet.Length);
            w.Write((uint)packet.Length);
            w.Write(packet, 0, 10);                          // the record says 54 bytes; 10 are there
        }
        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public async Task A_capture_is_listed_or_the_missing_library_is_named_never_a_raw_loader_message_Async()
    {
        var file = Path.Combine(_dir, "clean.pcap");
        await File.WriteAllBytesAsync(file, Pcap(packets: 3, damagedTail: false));
        var tool = new NetworkTool();

        await tool.LoadEvidenceCommand.ExecuteAsync(file);

        if (tool.ErrorMessage is { } error)
        {
            // No capture library on this computer: the message must be the specific one.
            error.Should().Be(NetworkTool.MissingLibraryMessage(OperatingSystem.IsWindows()));
            error.Should().NotContain("Unable to load").And.NotContain("DllNotFound");
            return;
        }
        tool.Rows.Should().HaveCount(3);
        tool.IsTruncated.Should().BeFalse();
        Cells(tool.Rows[0])["Destination"].Should().Be("10.0.0.2");
        Cells(tool.Rows[0])["DestinationPort"].Should().Be("443");
        Cells(tool.Rows[0])["TcpFlags"].Should().Be("S");
    }

    [Fact]
    public async Task A_damaged_capture_shows_what_was_read_under_a_cut_short_banner_Async()
    {
        var file = Path.Combine(_dir, "damaged.pcap");
        await File.WriteAllBytesAsync(file, Pcap(packets: 2, damagedTail: true));
        var tool = new NetworkTool();

        await tool.LoadEvidenceCommand.ExecuteAsync(file);

        if (tool.ErrorMessage == NetworkTool.MissingLibraryMessage(OperatingSystem.IsWindows()))
        {
            return; // nothing to read captures with on this computer
        }
        tool.ErrorMessage.Should().BeNull();
        tool.Rows.Should().HaveCount(2);
        tool.IsTruncated.Should().BeTrue("the capture did not end cleanly");
        tool.TruncationNotice.Should().StartWith("Showing the 2 packets read before a damaged record");
    }

    [Fact]
    public async Task A_file_that_is_not_a_capture_is_an_error_that_names_the_formats_Async()
    {
        var file = NewFile("notes.pcap", "this is not a packet capture");
        var tool = new NetworkTool();

        await tool.LoadEvidenceCommand.ExecuteAsync(file);

        tool.Rows.Should().BeEmpty();
        tool.ErrorMessage.Should().NotBeNull();
        if (tool.ErrorMessage != NetworkTool.MissingLibraryMessage(OperatingSystem.IsWindows()))
        {
            tool.ErrorMessage.Should().StartWith("notes.pcap is not a packet capture Cinder can read").And.EndWith("Network reads .pcap and .pcapng files.");
            tool.ErrorMessage.Should().NotContain("offline adapter", "the library's internal wording is not shown");
        }
    }

    [Fact]
    public void The_capture_librarys_internal_wording_is_reduced_to_the_actual_problem()
    {
        NetworkTool.NotACaptureMessage("notes.pcap", "Unable to open offline adapter: unknown file format")
            .Should().Be("notes.pcap is not a packet capture Cinder can read (unknown file format). Network reads .pcap and .pcapng files.");
        NetworkTool.NotACaptureMessage("x.pcap", "")
            .Should().Be("x.pcap is not a packet capture Cinder can read. Network reads .pcap and .pcapng files.");
    }

    // ---------------------------------------------------------------- Windows-only parsers

    [Fact]
    public void Srum_on_another_system_says_it_needs_windows()
    {
        SrumTool.PlatformProblem(onWindows: true).Should().BeNull();
        SrumTool.PlatformProblem(onWindows: false).Should().Be(SrumTool.NeedsWindows);
        SrumTool.NeedsWindows.Should().Contain("only be read when Cinder runs on Windows").And.NotContain("DLL").And.NotContain("esent");
    }

    [Fact]
    public void Compressed_prefetch_on_another_system_says_it_needs_windows()
    {
        byte[] compressed = [(byte)'M', (byte)'A', (byte)'M', 0x04, 0x10, 0x20];
        byte[] windows7 = [0x17, 0, 0, 0, (byte)'S', (byte)'C', (byte)'C', (byte)'A'];

        PrefetchTool.IsCompressed(compressed).Should().BeTrue();
        PrefetchTool.IsCompressed([(byte)'M', (byte)'A', (byte)'M', 0x84]).Should().BeTrue("the checksum variant has the high bit set");
        PrefetchTool.IsCompressed(windows7).Should().BeFalse("older Prefetch files are not compressed and read anywhere");
        PrefetchTool.IsCompressed([(byte)'M', (byte)'A']).Should().BeFalse();

        PrefetchTool.PlatformProblem(compressed, onWindows: false).Should().Be(PrefetchTool.NeedsWindows);
        PrefetchTool.PlatformProblem(compressed, onWindows: true).Should().BeNull();
        PrefetchTool.PlatformProblem(windows7, onWindows: false).Should().BeNull();
        PrefetchTool.NeedsWindows.Should().Contain("Cinder running on Windows").And.NotContain("DLL").And.NotContain("ntdll");
    }

    // ---------------------------------------------------------------- Folder walks

    [Fact]
    public void A_subfolder_that_cannot_be_opened_is_counted_and_the_walk_carries_on()
    {
        var tree = new Dictionary<string, (string[] Files, string[] Folders)>
        {
            ["root"] = (["root/a.lnk"], ["root/locked", "root/open"]),
            ["root/open"] = (["root/open/b.lnk"], ["root/open/deep"]),
            ["root/open/deep"] = (["root/open/deep/c.lnk"], []),
        };
        IEnumerable<string> Files(string dir) => tree.TryGetValue(dir, out var e) ? e.Files : throw new UnauthorizedAccessException(dir);
        IEnumerable<string> Folders(string dir) => tree.TryGetValue(dir, out var e) ? e.Folders : throw new UnauthorizedAccessException(dir);
        var skips = new FolderScan.Skips();

        var found = FolderScan.FilesUnder("root", skips, CancellationToken.None, Files, Folders).ToList();

        // The old enumeration threw at "root/locked" and the whole load failed.
        found.Should().Equal("root/a.lnk", "root/open/b.lnk", "root/open/deep/c.lnk");
        skips.Folders.Should().Be(1);
    }

    [Fact]
    public void A_root_that_cannot_be_opened_is_an_error_not_an_empty_result()
    {
        var act = () => FolderScan.FilesUnder("root", new FolderScan.Skips(), CancellationToken.None,
            _ => throw new UnauthorizedAccessException("Access to the path 'root' is denied."), _ => []).ToList();

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "1 subfolder could not be opened and was skipped, so anything in it is not shown.")]
    [InlineData(12, "12 subfolders could not be opened and were skipped, so anything in them is not shown.")]
    public void Skipped_subfolders_are_reported_in_a_sentence(int skipped, string? expected) =>
        FolderScan.SkippedNote(skipped).Should().Be(expected);

    [Fact]
    public void Scan_note_combines_unparsed_files_and_skipped_folders()
    {
        FolderScan.Note(files: 10, failed: 0).Should().BeNull();
        FolderScan.Note(files: 10, failed: 2).Should().StartWith("Read 8 of 10 files; 2 could not be parsed");
        FolderScan.Note(files: 10, failed: 0, skippedFolders: 3).Should().StartWith("3 subfolders could not be opened");
        FolderScan.Note(files: 10, failed: 2, skippedFolders: 1).Should().Contain("2 could not be parsed").And.EndWith("so anything in it is not shown.");
    }

    [Fact]
    public void A_real_walk_finds_hidden_files_and_goes_into_hidden_folders()
    {
        var visible = NewFile(Path.Combine("walk", "seen.txt"), "x");
        var hidden = NewFile(Path.Combine("walk", "AppData", "inner", "hidden.txt"), "x");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        new DirectoryInfo(Path.Combine(_dir, "walk", "AppData")).Attributes |= FileAttributes.Hidden;
        var skips = new FolderScan.Skips();

        var found = FolderScan.FilesUnder(Path.Combine(_dir, "walk"), skips, CancellationToken.None).ToList();

        // Evidence is full of hidden and system entries: AppData, $Recycle.Bin, most of a profile.
        found.Should().BeEquivalentTo([visible, hidden]);
        skips.Folders.Should().Be(0);
    }

    [Fact]
    public void Browser_history_is_found_inside_a_hidden_profile_folder()
    {
        var db = Path.Combine(_dir, "user", "AppData", "Local", "Google", "Chrome", "User Data", "Default", "History");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE urls (url TEXT, title TEXT, visit_count INTEGER, last_visit_time INTEGER);" +
                              "INSERT INTO urls VALUES ('https://example.com/', 'Example', 2, 13253932800000000);";
            cmd.ExecuteNonQuery();
        }
        new DirectoryInfo(Path.Combine(_dir, "user", "AppData")).Attributes |= FileAttributes.Hidden;

        var result = BrowserHistoryTool.Parse(Path.Combine(_dir, "user"), CancellationToken.None);

        result.Rows.Should().ContainSingle();
        Cells(result.Rows[0])["Browser"].Should().Be("Chrome");
        result.Note.Should().BeNull("nothing was skipped");
    }

    [Fact]
    public async Task Recycle_bin_records_are_read_even_though_windows_marks_them_hidden_and_system_Async()
    {
        var record = new List<byte>();
        record.AddRange(BitConverter.GetBytes(2L));
        record.AddRange(BitConverter.GetBytes(1024L));
        record.AddRange(BitConverter.GetBytes(new DateTimeOffset(2024, 4, 1, 9, 0, 0, TimeSpan.Zero).ToFileTime()));
        const string original = @"C:\Users\x\Documents\ledger.xlsx";
        record.AddRange(BitConverter.GetBytes(original.Length + 1));
        record.AddRange(System.Text.Encoding.Unicode.GetBytes(original + "\0"));
        var userBin = NewFolder(Path.Combine("$Recycle.Bin", "S-1-5-21-1-2-3-1001"));
        var iFile = Path.Combine(userBin, "$IABC123.xlsx");
        await File.WriteAllBytesAsync(iFile, [.. record]);
        File.SetAttributes(iFile, FileAttributes.Hidden | FileAttributes.System);
        new DirectoryInfo(userBin).Attributes |= FileAttributes.Hidden | FileAttributes.System;
        var tool = new RecycleBinTool();

        await tool.LoadEvidenceCommand.ExecuteAsync(Path.Combine(_dir, "$Recycle.Bin"));

        tool.ErrorMessage.Should().BeNull();
        tool.Rows.Should().ContainSingle();
        var row = Cells(tool.Rows[0]);
        row["Owner"].Should().Be("S-1-5-21-1-2-3-1001");
        row["OriginalPath"].Should().Be(original);
        row["Deleted"].Should().Be("2024-04-01 09:00:00Z");
    }

    [Fact]
    public void Gallery_status_mentions_folders_it_could_not_open()
    {
        GalleryTool.DescribeLoad(40, more: false, skippedFolders: 2)
            .Should().Be("40 images. 2 subfolders could not be opened and were skipped, so anything in them is not shown.");
        GalleryTool.DescribeLoad(40, more: false, skippedFolders: 0).Should().Be("40 images.");
    }

    // ---------------------------------------------------------------- Banners

    private sealed class RowsTool : SidecarToolViewModel
    {
        public override string Id => "rows";
        public override string Title => "Rows";
        public override string Icon => "";
        public override string Kind => "rows";

        public ParseResult? Next { get; set; }

        protected override string? CheckInput(string path) => null;

        protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
            Next is { } result ? Task.FromResult(result) : Task.FromException<ParseResult>(new InvalidDataException("bad header"));
    }

    private static List<object> ThreeRows() => [new { Name = "a" }, new { Name = "b" }, new { Name = "c" }];

    [Fact]
    public async Task A_failed_export_has_its_own_heading_and_leaves_the_rows_alone_Async()
    {
        var tool = new RowsTool { Next = new ParseResult(ThreeRows()) };
        await tool.LoadEvidenceCommand.ExecuteAsync("evidence.bin");

        await tool.ExportToAsync(Path.Combine(_dir, "no-such-folder", "out.csv"), "csv", null, CancellationToken.None);

        // The old banner read "The parser couldn't read this file." above a perfectly good grid.
        tool.ErrorTitle.Should().Be("Could not save the export");
        tool.ErrorMessage.Should().NotBeNullOrEmpty();
        tool.StatusLine.Should().StartWith("Could not export to ");
        tool.Rows.Should().HaveCount(3);

        var good = Path.Combine(_dir, "out.csv");
        await tool.ExportToAsync(good, "csv", null, CancellationToken.None);

        tool.ErrorMessage.Should().BeNull("the export worked, so its earlier failure is no longer true");
        tool.StatusLine.Should().Be($"Exported 3 rows to {good}");
        (await File.ReadAllLinesAsync(good)).Should().Equal("Name", "a", "b", "c");
    }

    [Fact]
    public async Task A_failed_read_after_a_good_one_shows_the_read_heading_and_no_stale_count_Async()
    {
        var tool = new RowsTool { Next = new ParseResult(ThreeRows()) };
        await tool.LoadEvidenceCommand.ExecuteAsync("first.bin");
        await tool.ExportToAsync(Path.Combine(_dir, "no-such-folder", "out.csv"), "csv", null, CancellationToken.None);
        tool.RowCountDisplay.Should().Be("3 rows");

        tool.Next = null;
        await tool.LoadEvidenceCommand.ExecuteAsync("second.bin");

        tool.ErrorTitle.Should().Be("Could not read this file");
        tool.ErrorMessage.Should().Be("bad header");
        tool.RowCountDisplay.Should().Be("0 rows", "the footer must not keep the first file's count");
        tool.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_parser_can_say_in_its_own_words_why_the_result_is_cut_short_Async()
    {
        const string reason = "Showing the 2 packets read before a damaged record (Error).";
        var tool = new RowsTool { Next = new ParseResult(ThreeRows(), Truncated: true, TruncationReason: reason) };

        await tool.LoadEvidenceCommand.ExecuteAsync("capture.pcap");

        tool.IsTruncated.Should().BeTrue();
        tool.TruncationNotice.Should().Be(reason);
    }

    // ---------------------------------------------------------------- Reports

    [Fact]
    public void A_failed_pdf_export_says_what_failed_and_that_nothing_lesser_was_written()
    {
        var wrapped = new InvalidOperationException(
            "PDF/A generation failed (x). No PDF/A-capable fallback exists; export as plain PDF instead.",
            new InvalidOperationException("The font 'Inter' could not be embedded."));

        var message = ReportsToolViewModel.DescribeExportFailure(ReportFormat.PdfA, @"C:\out\report.pdf", wrapped);

        message.Should().StartWith(@"Could not write the PDF (PDF/A-2b) to C:\out\report.pdf: The font 'Inter' could not be embedded.");
        message.Should().Contain("does not fall back to an ordinary PDF").And.Contain("Export as Word or HTML");
        message.Should().NotContain("export as plain PDF", "there is no plain-PDF option to send the examiner to");
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), "not allowed to write to that location")]
    [InlineData(typeof(DirectoryNotFoundException), "the folder no longer exists")]
    public void A_failed_export_explains_common_causes_in_plain_words(Type exception, string expected)
    {
        var ex = (Exception)Activator.CreateInstance(exception)!;

        var message = ReportsToolViewModel.DescribeExportFailure(ReportFormat.Docx, "report.docx", ex);

        message.Should().StartWith("Could not write the Word document to report.docx:").And.Contain(expected);
        message.Should().NotContain("PDF");
    }

    [Fact]
    public void A_file_open_elsewhere_is_named_as_the_cause()
    {
        var ex = new IOException("The process cannot access the file 'report.html' because it is being used by another process.");
        ReportsToolViewModel.DescribeExportFailure(ReportFormat.Html, "report.html", ex)
            .Should().Contain("the file is open in another program. Close it there and export again.");
    }

    [Fact]
    public void Help_and_hints_say_what_these_tools_need()
    {
        new ReportsTool().HelpMarkdown.Should().Contain("PDF/A-2b").And.Contain("does not write an ordinary PDF in its place");
        new NetworkTool().HelpMarkdown.Should().Contain("Npcap").And.Contain("libpcap");
        new NetworkTool().EmptyStateHint.Should().ContainAny("Npcap must be installed", "libpcap must be installed");
        new SrumTool().HelpMarkdown.Should().Contain("works only on Windows");
        new PrefetchTool().HelpMarkdown.Should().Contain("only be").And.Contain("Windows");
        new IocMatchTool().HelpMarkdown.Should().Contain("the folder you scanned is the evidence").And.Contain("\"Stop\"");
    }
}
