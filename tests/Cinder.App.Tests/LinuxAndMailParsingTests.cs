using System.Text;
using Cinder.App.ViewModels.Tools;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>
/// Linux log timestamps and login records, and the shared MBOX header reader. The timestamp
/// tests pin the rules that matter to an examiner: no invented year, no shift by this
/// workstation's time zone, and a label that says what kind of time each value is.
/// </summary>
public class LinuxAndMailParsingTests
{
    // ---------------------------------------------------------------- Log timestamps

    [Theory]
    [InlineData("2024-05-01T12:34:56.123456+02:00 host sshd[1]: Accepted password", "2024-05-01 10:34:56Z")]
    [InlineData("2024-05-01T12:34:56-0500 host cron[9]: job", "2024-05-01 17:34:56Z")]
    [InlineData("2024-05-01T12:34:56Z host kernel: up", "2024-05-01 12:34:56Z")]
    public void Iso_stamp_with_an_offset_is_converted_to_utc(string line, string expected)
    {
        var (time, basis) = LinuxArtifactsTool.ParseLogTimestamp(line);
        time.Should().Be(expected);
        basis.Should().Be(LinuxArtifactsTool.BasisUtc);
    }

    [Fact]
    public void Iso_stamp_without_an_offset_is_kept_as_written_and_labelled_local()
    {
        var (time, basis) = LinuxArtifactsTool.ParseLogTimestamp("2024-05-01T12:34:56 host app: started");
        time.Should().Be("2024-05-01 12:34:56");
        basis.Should().Be(LinuxArtifactsTool.BasisLocal);
    }

    [Theory]
    [InlineData("Nov 12 10:00:00 host sshd[1]: Failed password", "11-12 10:00:00")]
    // Days 1 to 9 are space-padded in classic syslog. These used to come out blank.
    [InlineData("Nov  2 10:00:00 host sshd[1]: Failed password", "11-02 10:00:00")]
    // 29 February must parse whatever year the examiner's clock says it is.
    [InlineData("Feb 29 23:59:59 host su: session opened", "02-29 23:59:59")]
    public void Classic_syslog_stamp_keeps_its_clock_time_and_gets_no_year(string line, string expected)
    {
        var (time, basis) = LinuxArtifactsTool.ParseLogTimestamp(line);

        // The hour is exactly what the log says: not shifted by this machine's UTC offset,
        // and not marked "Z", because the log does not say what zone it is in.
        time.Should().Be(expected);
        time.Should().NotContain("Z").And.NotMatchRegex(@"\d{4}-");
        basis.Should().Be(LinuxArtifactsTool.BasisNoYear);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ls -la /tmp")]
    [InlineData("root:x:0:0:root:/root:/bin/bash")]
    [InlineData("Nov 32 10:00:00 host x: impossible day")]
    [InlineData("Nov 12 25:00:00 host x: impossible hour")]
    [InlineData("Xyz 12 10:00:00 host x: not a month")]
    public void Line_without_a_recognisable_stamp_has_no_time(string line)
    {
        LinuxArtifactsTool.ParseLogTimestamp(line).Should().Be(("", ""));
    }

    // ---------------------------------------------------------------- wtmp / btmp

    private static byte[] Utmp(short type, int pid, string line, string user, string host, DateTimeOffset when)
    {
        var r = new byte[384];
        BitConverter.GetBytes(type).CopyTo(r, 0);
        BitConverter.GetBytes(pid).CopyTo(r, 4);
        Encoding.ASCII.GetBytes(line).CopyTo(r, 8);
        Encoding.ASCII.GetBytes(user).CopyTo(r, 44);
        Encoding.ASCII.GetBytes(host).CopyTo(r, 76);
        BitConverter.GetBytes((int)when.ToUnixTimeSeconds()).CopyTo(r, 340);
        return r;
    }

    [Fact]
    public void Login_records_are_read_as_records_not_as_lines_of_text()
    {
        var boot = new DateTimeOffset(2024, 2, 1, 6, 0, 0, TimeSpan.Zero);
        var login = new DateTimeOffset(2024, 2, 1, 6, 5, 30, TimeSpan.Zero);
        using var ms = new MemoryStream();
        ms.Write(Utmp(2, 0, "~", "reboot", "6.1.0-18-amd64", boot));
        ms.Write(new byte[384]);                                              // empty slot
        ms.Write(Utmp(7, 4321, "pts/0", "alice", "203.0.113.7", login));
        ms.Position = 0;

        var records = LinuxArtifactsTool.ReadUtmp(ms);

        records.Should().HaveCount(2, "the empty slot is skipped");
        records[0].Type.Should().Be("boot");
        records[0].TimeUtc.Should().Be(boot);
        records[1].Should().Be(new UtmpRecord("session start", 4321, "pts/0", "alice", "203.0.113.7", login));
    }

    [Fact]
    public void Login_record_file_with_a_different_record_size_is_refused()
    {
        using var ms = new MemoryStream(new byte[400]);
        var act = () => LinuxArtifactsTool.ReadUtmp(ms);
        act.Should().Throw<InvalidOperationException>().WithMessage("*384*");
    }

    // ---------------------------------------------------------------- MBOX headers

    private const string Mbox =
        "From alice@example.com Mon Jan  1 10:00:00 2024\n" +
        "From: Alice <alice@example.com>\n" +
        "To: bob@example.com,\n" +
        "\tcarol@example.com,\n" +
        "  dave@example.com\n" +
        "Cc: erin@example.com\n" +
        "Subject: Quarterly numbers and\n" +
        " the plan for next year\n" +
        "Received: from a by b; Mon, 1 Jan 2024 10:00:00 +0000\n" +
        "Received: from c by d;\n" +
        "\tMon, 1 Jan 2024 09:59:00 +0000\n" +
        "Date: Mon, 1 Jan 2024 10:00:00 +0000\n" +
        "\n" +
        "Body line one.\n" +
        "From now on this is still the body, not a new message.\n" +
        "Subject: this is body text too\n" +
        "\n" +
        "From bob@example.com Mon Jan  1 11:00:00 2024\n" +
        "From: bob@example.com\n" +
        "To: alice@example.com\n" +
        "Subject: Re: Quarterly numbers\n" +
        "\n" +
        "Second body.\n";

    [Fact]
    public void Folded_header_lines_are_joined_so_no_recipient_is_lost()
    {
        var messages = MboxHeaderReader.ReadMessages(new StringReader(Mbox)).ToList();

        messages.Should().HaveCount(2);
        MboxHeaderReader.Get(messages[0], "To").Should().Be("bob@example.com, carol@example.com, dave@example.com");
        MboxHeaderReader.Get(messages[0], "Subject").Should().Be("Quarterly numbers and the plan for next year");
        MboxHeaderReader.Get(messages[0], "Cc").Should().Be("erin@example.com");
        MboxHeaderReader.Get(messages[0], "date").Should().Be("Mon, 1 Jan 2024 10:00:00 +0000", "header names are case-insensitive");
    }

    [Fact]
    public void Body_text_is_never_read_as_headers_or_as_a_new_message()
    {
        var messages = MboxHeaderReader.ReadMessages(new StringReader(Mbox)).ToList();

        // "From now on…" follows a non-blank line, so it is body text, and the "Subject:" line
        // in the body does not replace the real subject.
        messages.Should().HaveCount(2);
        MboxHeaderReader.Get(messages[0], "Subject").Should().StartWith("Quarterly numbers");
        MboxHeaderReader.Get(messages[1], "From").Should().Be("bob@example.com");
    }

    [Fact]
    public void Continuation_of_a_repeated_header_does_not_leak_into_another_header()
    {
        var first = MboxHeaderReader.ReadMessages(new StringReader(Mbox)).First();

        MboxHeaderReader.Get(first, "Received").Should().Be("from a by b; Mon, 1 Jan 2024 10:00:00 +0000");
        MboxHeaderReader.Get(first, "Date").Should().NotContain("09:59");
    }

    [Fact]
    public void Missing_header_is_an_empty_string_and_an_empty_file_has_no_messages()
    {
        var second = MboxHeaderReader.ReadMessages(new StringReader(Mbox)).Last();
        MboxHeaderReader.Get(second, "Cc").Should().BeEmpty();
        MboxHeaderReader.ReadMessages(new StringReader("")).Should().BeEmpty();
        MboxHeaderReader.ReadMessages(new StringReader("not an mbox at all\n")).Should().BeEmpty();
    }

    [Fact]
    public void Email_tool_lists_every_recipient_of_a_folded_to_header()
    {
        var rows = EmailTool.ReadMbox(new StringReader(Mbox), "mail.mbox", budget: 100, CancellationToken.None);

        rows.Should().HaveCount(2);
        var first = Cinder.Core.Export.TabularExporter.ToDictionary(rows[0]);
        first["To"].Should().Be("bob@example.com, carol@example.com, dave@example.com");
        first["Cc"].Should().Be("erin@example.com");
        first["Sent"].Should().Be("Mon, 1 Jan 2024 10:00:00 +0000", "the Date header is shown as the message carries it");
        first["Source"].Should().Be("mail.mbox");
    }

    [Fact]
    public void Email_tool_stops_at_its_budget()
    {
        EmailTool.ReadMbox(new StringReader(Mbox), "mail.mbox", budget: 1, CancellationToken.None).Should().HaveCount(1);
    }
}
