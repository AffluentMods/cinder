using System.Text.Json;
using Cinder.Search;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

public sealed class TimelineExporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 4, 5, 6, 7, 123, TimeSpan.Zero);

    private static TimelineEvent Ev(string source, string summary, string? user = null, params string[] tags)
        => new(T0, source, user, summary, tags);

    [Fact]
    public void Timesketch_jsonl_carries_the_three_mandatory_fields_per_line()
    {
        using var w = new StringWriter();
        var n = TimelineExporter.WriteTimesketchJsonl(
            [Ev("prefetch", "Executed: CALC.EXE", tags: "TA0002"), Ev("evtx.security", "[Security] EventID=4624", "alice")],
            w);

        n.Should().Be(2);
        var lines = w.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        root.GetProperty("message").GetString().Should().Be("Executed: CALC.EXE");
        root.GetProperty("datetime").GetString().Should().Be("2026-03-04T05:06:07.123000Z");
        root.GetProperty("timestamp_desc").GetString().Should().Be("Last Run Time");
        root.GetProperty("tag")[0].GetString().Should().Be("TA0002");
    }

    [Fact]
    public void Timesketch_csv_has_the_header_timesketch_expects()
    {
        using var w = new StringWriter();
        TimelineExporter.WriteTimesketchCsv([Ev("browser.history", "Example — https://example.com", "bob")], w);
        var lines = w.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();

        lines[0].Should().Be("message,datetime,timestamp_desc,source,user,tag");
        lines[1].Should().StartWith("Example — https://example.com,2026-03-04T05:06:07.123000Z,Last Visited Time,browser.history,bob");
    }

    [Fact]
    public void Timesketch_csv_neutralises_formula_injection_in_messages()
    {
        using var w = new StringWriter();
        TimelineExporter.WriteTimesketchCsv([Ev("evtx.app", "=HYPERLINK(\"http://evil\")")], w);
        w.ToString().Split('\n')[1].Should().StartWith("\"'=HYPERLINK");
    }

    [Fact]
    public void Bodyfile_has_eleven_pipe_delimited_fields_with_the_time_in_the_right_slot()
    {
        using var w = new StringWriter();
        TimelineExporter.WriteBodyfile(
            [Ev("lnk.created", "target created"), Ev("lnk.accessed", "target accessed"), Ev("recyclebin", "deleted")],
            w);
        var lines = w.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();

        var epoch = T0.ToUnixTimeSeconds().ToString();
        foreach (var line in lines)
        {
            line.Split('|').Should().HaveCount(11);
        }
        // MD5|name|inode|mode|UID|GID|size|atime|mtime|ctime|crtime
        lines[0].Split('|')[10].Should().Be(epoch, "creation goes in crtime");
        lines[0].Split('|')[8].Should().Be("0");
        lines[1].Split('|')[7].Should().Be(epoch, "access goes in atime");
        lines[2].Split('|')[9].Should().Be(epoch, "deletion goes in ctime");
    }

    [Fact]
    public void Bodyfile_replaces_pipes_in_the_message_so_the_record_stays_well_formed()
    {
        using var w = new StringWriter();
        TimelineExporter.WriteBodyfile([Ev("evtx.app", "a|b|c")], w);
        w.ToString().TrimEnd().Split('|').Should().HaveCount(11);
    }

    [Theory]
    [InlineData("evtx.security", "Event Time")]
    [InlineData("EVTX.System", "Event Time")]
    [InlineData("lnk.modified", "Content Modification Time")]
    [InlineData("something.new", "Event Time")]
    public void Timestamp_description_is_derived_from_source(string source, string expected)
        => TimelineExporter.DescribeTimestamp(source).Should().Be(expected);
}
