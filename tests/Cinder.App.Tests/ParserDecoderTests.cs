using System.Text;
using Cinder.App.ViewModels.Tools;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>
/// The binary and text decoders behind the Windows artifact tools. Each test builds its input
/// to the published layout of the format, so a decoder that reads the wrong offsets fails here
/// rather than in front of an examiner.
/// </summary>
public class ParserDecoderTests
{
    // ---------------------------------------------------------------- ShimCache

    /// <summary>One Windows 10/11 cache entry: "10ts", unknown, data size, path length, path, FILETIME, data length, data.</summary>
    private static byte[] Win10Entry(string path, DateTimeOffset modified, uint unknown, int dataLength = 0)
    {
        var p = Encoding.Unicode.GetBytes(path);
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((ushort)p.Length));
        body.AddRange(p);
        body.AddRange(BitConverter.GetBytes(modified.ToFileTime()));
        body.AddRange(BitConverter.GetBytes(dataLength));
        body.AddRange(new byte[dataLength]);

        var entry = new List<byte>();
        entry.AddRange("10ts"u8.ToArray());
        entry.AddRange(BitConverter.GetBytes(unknown));
        entry.AddRange(BitConverter.GetBytes(body.Count));
        entry.AddRange(body);
        return [.. entry];
    }

    private static byte[] Win10Cache(int headerSize, params byte[][] entries)
    {
        var header = new byte[headerSize];
        BitConverter.GetBytes(headerSize).CopyTo(header, 0);
        var blob = new List<byte>(header);
        foreach (var e in entries)
        {
            blob.AddRange(e);
        }
        return [.. blob];
    }

    [Theory]
    [InlineData(0x30)]
    [InlineData(0x34)]
    public void ShimCache_decodes_windows_10_and_11_entries_in_order(int headerSize)
    {
        var first = new DateTimeOffset(2024, 3, 9, 14, 5, 6, TimeSpan.Zero);
        var second = new DateTimeOffset(2019, 12, 7, 9, 8, 7, TimeSpan.Zero);
        // The "unknown" field is a large, random-looking value on real hives. A decoder that
        // mistakes it for the entry length finds nothing; that was the bug.
        var blob = Win10Cache(headerSize,
            Win10Entry(@"C:\Windows\System32\cmd.exe", first, unknown: 0x212D3E23, dataLength: 86),
            Win10Entry(@"C:\Users\x\Downloads\tool.exe", second, unknown: 0xF00DBEEF));

        var entries = ShimcacheTool.DecodeAppCompatCache(blob);

        entries.Should().HaveCount(2);
        entries[0].Path.Should().Be(@"C:\Windows\System32\cmd.exe");
        entries[0].FileModifiedUtc.Should().Be(first);
        entries[1].Path.Should().Be(@"C:\Users\x\Downloads\tool.exe");
        entries[1].FileModifiedUtc.Should().Be(second);
    }

    [Fact]
    public void ShimCache_decodes_windows_8_entries_with_their_package_and_flag_fields()
    {
        var modified = new DateTimeOffset(2015, 6, 1, 1, 2, 3, TimeSpan.Zero);
        var path = Encoding.Unicode.GetBytes(@"C:\Program Files\App\app.exe");
        var package = Encoding.Unicode.GetBytes("pkg");

        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((ushort)path.Length));
        body.AddRange(path);
        body.AddRange(BitConverter.GetBytes((ushort)package.Length));
        body.AddRange(package);
        body.AddRange(BitConverter.GetBytes(2));          // insert flags
        body.AddRange(BitConverter.GetBytes(0));          // shim flags
        body.AddRange(BitConverter.GetBytes(modified.ToFileTime()));
        body.AddRange(BitConverter.GetBytes(0));          // data length

        var blob = new List<byte>(new byte[0x80]);
        blob[0] = 0x80;
        blob.AddRange("10ts"u8.ToArray());
        blob.AddRange(BitConverter.GetBytes(0xAABBCCDDu));
        blob.AddRange(BitConverter.GetBytes(body.Count));
        blob.AddRange(body);

        var entries = ShimcacheTool.DecodeAppCompatCache([.. blob]);

        entries.Should().ContainSingle();
        entries[0].Path.Should().Be(@"C:\Program Files\App\app.exe");
        entries[0].FileModifiedUtc.Should().Be(modified);
    }

    [Fact]
    public void ShimCache_returns_nothing_for_a_layout_it_does_not_know_rather_than_guessing()
    {
        // Windows 7 style: 0xBADC0FEE header, no "10ts" entries.
        var blob = new byte[4096];
        BitConverter.GetBytes(0xBADC0FEEu).CopyTo(blob, 0);
        Encoding.Unicode.GetBytes(@"C:\Windows\notepad.exe").CopyTo(blob, 200);

        ShimcacheTool.DecodeAppCompatCache(blob).Should().BeEmpty();
        ShimcacheTool.DecodeAppCompatCache([]).Should().BeEmpty();
    }

    [Fact]
    public void ShimCache_stops_cleanly_at_an_entry_whose_size_runs_past_the_value()
    {
        var good = Win10Entry(@"C:\a.exe", DateTimeOffset.UnixEpoch.AddYears(40), unknown: 1);
        var bad = Win10Entry(@"C:\b.exe", DateTimeOffset.UnixEpoch.AddYears(40), unknown: 2);
        BitConverter.GetBytes(1_000_000).CopyTo(bad, 8);   // claims far more data than exists

        var entries = ShimcacheTool.DecodeAppCompatCache(Win10Cache(0x34, good, bad));

        entries.Should().ContainSingle().Which.Path.Should().Be(@"C:\a.exe");
    }

    // ---------------------------------------------------------------- Amcache

    [Theory]
    // The stored value is "0000" + the 40-digit SHA-1. Only that prefix goes.
    [InlineData("0000da39a3ee5e6b4b0d3255bfef95601890afd80709", "da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    // A hash that itself starts with zeros must keep them: this is the one-in-sixteen case.
    [InlineData("000000a9993e364706816aba3e25717850c26c9cd0d8", "00a9993e364706816aba3e25717850c26c9cd0d8")]
    [InlineData("00000000000000000000000000000000000000000001", "0000000000000000000000000000000000000001")]
    // Anything that is not the 44-character form is shown as stored.
    [InlineData("da39a3ee5e6b4b0d3255bfef95601890afd80709", "da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Amcache_sha1_drops_only_the_four_character_prefix(string? fileId, string expected) =>
        AmcacheTool.Sha1FromFileId(fileId).Should().Be(expected);

    // ---------------------------------------------------------------- Network profile dates

    [Fact]
    public void Network_profile_date_is_decoded_as_local_wall_clock_time_without_a_zone()
    {
        // SYSTEMTIME: year, month, day of week, day, hour, minute, second, milliseconds.
        var raw = new byte[16];
        ushort[] fields = [2023, 11, 4, 2, 21, 7, 9, 0];
        for (var i = 0; i < fields.Length; i++)
        {
            BitConverter.GetBytes(fields[i]).CopyTo(raw, i * 2);
        }

        WifiHistoryTool.DecodeSystemTime(raw).Should().Be("2023-11-02 21:07:09");
    }

    [Fact]
    public void Network_profile_date_is_blank_when_the_value_is_missing_or_not_a_date()
    {
        WifiHistoryTool.DecodeSystemTime(null).Should().BeEmpty();
        WifiHistoryTool.DecodeSystemTime(new byte[8]).Should().BeEmpty();
        WifiHistoryTool.DecodeSystemTime(new byte[16]).Should().BeEmpty();   // year 0
    }

    [Theory]
    [InlineData("0", "Public")]
    [InlineData("1", "Private")]
    [InlineData("2", "Domain")]
    [InlineData("", "")]
    public void Network_category_is_named(string stored, string expected) =>
        WifiHistoryTool.DescribeCategory(stored).Should().Be(expected);

    [Theory]
    [InlineData("71", "Wireless")]
    [InlineData("6", "Wired")]
    [InlineData("23", "Mobile broadband")]
    [InlineData("99", "Type 99")]
    public void Network_medium_is_named(string stored, string expected) =>
        WifiHistoryTool.DescribeNameType(stored).Should().Be(expected);

    // ---------------------------------------------------------------- Browser

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Google\Chrome\User Data\Default\History", "Chrome")]
    [InlineData(@"C:\Users\a\AppData\Local\Microsoft\Edge\User Data\Profile 1\History", "Edge")]
    // These two were never recognised: the folder is BraveSoftware / Opera Software, not \brave\ or \opera\.
    [InlineData(@"C:\Users\a\AppData\Local\BraveSoftware\Brave-Browser\User Data\Default\History", "Brave")]
    [InlineData(@"C:\Users\a\AppData\Roaming\Opera Software\Opera Stable\History", "Opera")]
    [InlineData(@"C:\Users\a\AppData\Local\Vivaldi\User Data\Default\History", "Vivaldi")]
    // Profiles exported from Linux and macOS images use forward slashes.
    [InlineData("/home/a/.config/google-chrome/Default/History", "Chrome")]
    [InlineData("/home/a/.config/BraveSoftware/Brave-Browser/Default/History", "Brave")]
    [InlineData("/Users/a/Library/Application Support/Google/Chrome/Default/History", "Chrome")]
    [InlineData("/home/a/.mozilla/firefox/abc.default/places.sqlite", "Firefox")]
    public void Browser_is_named_from_its_profile_path(string path, string expected) =>
        BrowserHistoryTool.GuessBrowser(path).Should().Be(expected);

    [Fact]
    public void Browser_that_cannot_be_identified_says_so()
    {
        BrowserHistoryTool.GuessBrowser(@"E:\export\History").Should().StartWith("Chromium-based");
    }

    [Fact]
    public void Chromium_time_is_microseconds_since_1601_in_utc()
    {
        // 2021-01-01 00:00:00 UTC = 13253932800 seconds after 1601-01-01.
        BrowserHistoryTool.ChromiumTimeToString(13_253_932_800_000_000).Should().Be("2021-01-01 00:00:00Z");
        BrowserHistoryTool.ChromiumTimeToString(0).Should().BeEmpty();
        BrowserHistoryTool.ChromiumTimeToString(long.MaxValue).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Recycle Bin

    [Fact]
    public void Recycle_bin_record_version_2_gives_path_size_and_deletion_time()
    {
        var deleted = new DateTimeOffset(2024, 1, 15, 8, 30, 0, TimeSpan.Zero);
        const string original = @"C:\Users\x\Desktop\plans.docx";
        var data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(2L));
        data.AddRange(BitConverter.GetBytes(123_456L));
        data.AddRange(BitConverter.GetBytes(deleted.ToFileTime()));
        data.AddRange(BitConverter.GetBytes(original.Length + 1));
        data.AddRange(Encoding.Unicode.GetBytes(original + "\0"));

        var (path, size, when) = RecycleBinTool.DecodeIFile([.. data]);

        path.Should().Be(original);
        size.Should().Be(123_456);
        when.Should().Be(deleted);
    }

    [Fact]
    public void Recycle_bin_record_version_1_reads_the_fixed_width_path()
    {
        const string original = @"D:\old\report.xls";
        var data = new byte[24 + 520];
        BitConverter.GetBytes(1L).CopyTo(data, 0);
        BitConverter.GetBytes(42L).CopyTo(data, 8);
        Encoding.Unicode.GetBytes(original).CopyTo(data, 24);

        var (path, size, when) = RecycleBinTool.DecodeIFile(data);

        path.Should().Be(original);
        size.Should().Be(42);
        when.Should().BeNull("a zero FILETIME is not a date");
    }

    [Fact]
    public void Recycle_bin_record_too_short_to_be_one_is_an_error_not_an_empty_row()
    {
        var act = () => RecycleBinTool.DecodeIFile(new byte[10]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*24*");
    }

    // ---------------------------------------------------------------- Smaller pieces

    [Fact]
    public void Shortcut_header_time_is_blank_when_unset()
    {
        LnkTool.HeaderTime(null).Should().BeEmpty();
        LnkTool.HeaderTime(DateTimeOffset.FromFileTime(0)).Should().BeEmpty("FILETIME 0 is 1601, which means 'not recorded'");
        LnkTool.HeaderTime(new DateTimeOffset(2022, 5, 4, 3, 2, 1, TimeSpan.FromHours(2))).Should().Be("2022-05-04 01:02:01Z");
    }

    [Theory]
    [InlineData("apps/com.example.chat/db/messages.db", "com.example.chat")]
    [InlineData("apps/com.example.chat", "com.example.chat")]
    [InlineData("shared/0/DCIM/1.jpg", "shared")]
    [InlineData("a", "")]
    [InlineData("apps/", "")]
    public void Android_backup_entry_is_attributed_to_its_app(string entry, string expected) =>
        MobileTool.AdbDomain(entry).Should().Be(expected);

    [Fact]
    public void Sid_bytes_are_rendered_in_the_usual_text_form()
    {
        // S-1-5-21-1-2-3-1001
        var sid = new List<byte> { 1, 5, 0, 0, 0, 0, 0, 5 };
        foreach (var sub in new uint[] { 21, 1, 2, 3, 1001 })
        {
            sid.AddRange(BitConverter.GetBytes(sub));
        }
        SrumTool.TrySidString([.. sid]).Should().Be("S-1-5-21-1-2-3-1001");
        SrumTool.TrySidString([1, 5]).Should().BeEmpty();
    }

    [Fact]
    public void Last_line_of_a_traceback_is_the_error_itself()
    {
        const string stderr = "Traceback (most recent call last):\n  File \"<string>\", line 30\nOSError: unable to open file\n";
        EmailTool.LastLine(stderr).Should().Be("OSError: unable to open file");
        EmailTool.LastLine(null).Should().BeEmpty();
    }
}
