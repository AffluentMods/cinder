using Cinder.Artifacts;
using Cinder.Artifacts.Linux;
using FluentAssertions;
using Xunit;

namespace Cinder.Sidecar.Tests;

/// <summary>LinuxArtifactSidecar against canned replies shaped like <c>parsers/linux/linux_worker.py</c>'s.</summary>
public sealed class LinuxArtifactSidecarTests
{
    private const string Empty = """{"entries": [], "refused": [], "truncated": []}""";

    private const string AuthLogReply = """
        {"entries": [
          {"timestamp": "2018-12-31T23:59:58", "timestamp_raw": "Dec 31 23:59:58", "year_inferred": true, "tz_known": false,
           "host": "web01", "process": "sshd", "message": "Failed password for invalid user admin from 203.0.113.9 port 4242 ssh2",
           "user": "admin", "remote_host": "203.0.113.9"},
          {"timestamp": "2021-03-04T03:06:07.123456+00:00", "timestamp_raw": "2021-03-04T05:06:07.123456+02:00",
           "year_inferred": false, "tz_known": true,
           "host": "web01", "process": "sudo", "message": "session opened", "user": null, "remote_host": null},
          {"timestamp": null, "timestamp_raw": "Xyz 99 04:05:06", "year_inferred": false, "tz_known": false,
           "host": "web01", "process": "sshd", "message": "Accepted publickey for carol from 10.1.1.1 port 5 ssh2",
           "user": "carol", "remote_host": "10.1.1.1"},
          {"timestamp": "2021-03-04T05:06:07", "timestamp_raw": "2021-03-04T05:06:07", "year_inferred": false, "tz_known": false,
           "host": 17, "process": null, "message": ["not", "text"], "user": 0, "remote_host": {}}
        ],
         "refused": [{"path": "/mnt/ev/var/log/auth.log.1", "reason": "symlink resolves outside the evidence root"}],
         "truncated": ["/mnt/ev/var/log/auth.log"],
         "year_source": "file_mtime", "year_reference": "2019-01-02T12:00:00Z"}
        """;

    [Fact]
    public async Task Auth_log_is_asked_for_with_the_evidence_root()
    {
        var factory = FakeChannelFactory.Returning(Empty);
        var sidecar = new LinuxArtifactSidecar(factory.Open);

        await sidecar.LoadAuthLogAsync("/mnt/ev/var/log/auth.log", "/mnt/ev",
            referenceMtime: DateTimeOffset.FromUnixTimeSeconds(1_546_430_400));
        await sidecar.LoadAuthLogAsync("/tmp/loose/auth.log", rootPath: null);

        var calls = factory.Calls.ToList();
        calls[0].Method.Should().Be("auth_log");
        calls[0].Params["path"]!.GetValue<string>().Should().Be("/mnt/ev/var/log/auth.log");
        calls[0].Params["root"]!.GetValue<string>().Should().Be("/mnt/ev");
        calls[0].Params["reference_mtime"]!.GetValue<double>().Should().Be(1_546_430_400);
        calls[1].Params.ContainsKey("root").Should().BeFalse("no root was given, so none is made up");
        calls[1].Params.ContainsKey("reference_mtime").Should().BeFalse();
    }

    [Fact]
    public async Task The_streaming_overloads_pass_the_root_too()
    {
        var factory = FakeChannelFactory.Returning(Empty);
        var sidecar = new LinuxArtifactSidecar(factory.Open);

        await foreach (var _ in sidecar.ReadAuthLogAsync("/mnt/ev/var/log/auth.log", "/mnt/ev")) { }
        await foreach (var _ in sidecar.ReadJournalAsync("/mnt/ev/var/log/journal", "/mnt/ev")) { }
        await foreach (var _ in sidecar.ReadAuthLogAsync("/loose/auth.log")) { }

        var calls = factory.Calls.ToList();
        calls[0].Params["root"]!.GetValue<string>().Should().Be("/mnt/ev");
        calls[1].Method.Should().Be("journalctl");
        calls[1].Params["root"]!.GetValue<string>().Should().Be("/mnt/ev");
        calls[2].Params.ContainsKey("root").Should().BeFalse();
    }

    [Fact]
    public async Task A_yearless_zoneless_line_keeps_its_reading_and_says_what_it_is()
    {
        var result = await new LinuxArtifactSidecar(FakeChannelFactory.Returning(AuthLogReply).Open)
            .LoadAuthLogAsync("/mnt/ev/var/log/auth.log", "/mnt/ev");

        var classic = result.Entries[0];
        classic.Timestamp.Should().NotBeNull();
        classic.Timestamp!.Value.DateTime.Should().Be(new DateTime(2018, 12, 31, 23, 59, 58), "the digits in the log are not shifted");
        classic.TimeZoneKnown.Should().BeFalse("classic syslog records no zone: this is not a UTC instant");
        classic.YearInferred.Should().BeTrue();
        classic.TimestampRaw.Should().Be("Dec 31 23:59:58");
        classic.User.Should().Be("admin");
        classic.RemoteHost.Should().Be("203.0.113.9");

        // Code that only sees IArtifact can tell as well.
        IArtifact artifact = classic;
        artifact.Extras.Should().NotBeNull();
        artifact.Extras![SidecarJson.TimeBasisKey].Should().Be(SidecarJson.TimeBasisLocalUnknownZone);
        artifact.Extras["year_inferred"].Should().Be("true");
    }

    [Fact]
    public async Task A_line_with_an_offset_is_a_plain_instant()
    {
        var result = await new LinuxArtifactSidecar(FakeChannelFactory.Returning(AuthLogReply).Open)
            .LoadAuthLogAsync("/mnt/ev/var/log/auth.log", "/mnt/ev");

        var iso = result.Entries[1];
        iso.Timestamp.Should().Be(new DateTimeOffset(2021, 3, 4, 3, 6, 7, TimeSpan.Zero).AddTicks(1234560));
        iso.TimeZoneKnown.Should().BeTrue();
        iso.YearInferred.Should().BeFalse();
        iso.User.Should().BeNull();
        ((IArtifact)iso).Extras.Should().BeNull();
    }

    [Fact]
    public async Task A_null_timestamp_stays_null()
    {
        var result = await new LinuxArtifactSidecar(FakeChannelFactory.Returning(AuthLogReply).Open)
            .LoadAuthLogAsync("/mnt/ev/var/log/auth.log", "/mnt/ev");

        var unparsed = result.Entries[2];
        unparsed.Timestamp.Should().BeNull("an unknown time is not year 0001");
        ((IArtifact)unparsed).Timestamp.Should().BeNull();
        unparsed.TimeZoneKnown.Should().BeFalse();
        unparsed.TimestampRaw.Should().Be("Xyz 99 04:05:06", "what the log actually said is kept");
        unparsed.User.Should().Be("carol");
        result.Entries.Should().NotContain(e => e.Timestamp == DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task Fields_of_the_wrong_kind_cost_the_field_not_the_listing()
    {
        var result = await new LinuxArtifactSidecar(FakeChannelFactory.Returning(AuthLogReply).Open)
            .LoadAuthLogAsync("/mnt/ev/var/log/auth.log", "/mnt/ev");

        result.Entries.Should().HaveCount(4);
        var hostile = result.Entries[3];
        hostile.Host.Should().Be("17");
        hostile.Process.Should().BeEmpty();
        hostile.Message.Should().BeEmpty();
        hostile.User.Should().Be("0");
        hostile.RemoteHost.Should().BeNull();
        hostile.TimeZoneKnown.Should().BeFalse();
    }

    [Fact]
    public async Task What_the_parser_refused_and_truncated_is_part_of_the_result()
    {
        var result = await new LinuxArtifactSidecar(FakeChannelFactory.Returning(AuthLogReply).Open)
            .LoadAuthLogAsync("/mnt/ev/var/log/auth.log", "/mnt/ev");

        result.Refused.Should().Equal(new RefusedPath("/mnt/ev/var/log/auth.log.1", "symlink resolves outside the evidence root"));
        result.Truncated.Should().Equal("/mnt/ev/var/log/auth.log");
        result.YearSource.Should().Be("file_mtime");
        result.YearReference.Should().Be(new DateTimeOffset(2019, 1, 2, 12, 0, 0, TimeSpan.Zero));
        result.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task A_clean_listing_is_complete()
    {
        var result = await new LinuxArtifactSidecar(FakeChannelFactory.Returning(Empty).Open).LoadCronAsync("/mnt/ev");

        result.IsComplete.Should().BeTrue();
        result.Refused.Should().BeEmpty();
        result.Truncated.Should().BeEmpty();
    }

    [Fact]
    public async Task Journal_carries_root_limit_and_what_cut_it_short()
    {
        var factory = FakeChannelFactory.Returning("""
            {"entries": [
              {"timestamp": "2021-03-04T05:06:07+00:00", "unit": "ssh.service", "priority": "6", "message": "Accepted", "user": "0"},
              {"timestamp": null, "unit": "kernel", "priority": 3, "message": [222, 173, 190, 239], "user": null}
            ], "refused": [], "truncated": [], "truncated_by": "entries"}
            """);

        var result = await new LinuxArtifactSidecar(factory.Open).LoadJournalAsync("/mnt/ev/var/log/journal", "/mnt/ev", limit: 2);

        var call = factory.Calls.Single();
        call.Method.Should().Be("journalctl");
        call.Params["journal_dir"]!.GetValue<string>().Should().Be("/mnt/ev/var/log/journal");
        call.Params["root"]!.GetValue<string>().Should().Be("/mnt/ev");
        call.Params["limit"]!.GetValue<int>().Should().Be(2);

        result.TruncatedBy.Should().Be("entries");
        result.IsComplete.Should().BeFalse("the journal holds more records than were returned");
        result.Entries[0].Timestamp.Should().Be(new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero));
        result.Entries[1].Timestamp.Should().BeNull();
        result.Entries[1].Priority.Should().Be("3");
        result.Entries[1].Message.Should().Be("<4 bytes, not text>", "journalctl writes a non-UTF-8 message as a byte array");
    }

    [Fact]
    public async Task A_missing_journalctl_is_reported_not_swallowed()
    {
        var result = await new LinuxArtifactSidecar(FakeChannelFactory.Returning(
                """{"entries": [], "refused": [], "truncated": [], "todo": "install journalctl or wait for native parser (5.1)"}""").Open)
            .LoadJournalAsync("/j", null);

        result.Note.Should().Contain("install journalctl");
    }

    [Fact]
    public async Task Trash_size_and_time_are_not_invented()
    {
        var result = await new LinuxArtifactSidecar(FakeChannelFactory.Returning("""
            {"entries": [
              {"user": "alice", "original_path": "/home/alice/secret.txt", "size": 5, "deleted_at": "2021-03-04T05:06:07"},
              {"user": "alice", "original_path": "/home/alice/gone", "size": null, "deleted_at": null}
            ], "refused": [], "truncated": []}
            """).Open).LoadTrashAsync("/mnt/ev");

        var present = result.Entries[0];
        present.Size.Should().Be(5);
        present.DeletedAt!.Value.DateTime.Should().Be(new DateTime(2021, 3, 4, 5, 6, 7));
        present.TimeZoneKnown.Should().BeFalse("a Trash info file stores local time and no zone");
        ((IArtifact)present).Extras![SidecarJson.TimeBasisKey].Should().Be(SidecarJson.TimeBasisLocalUnknownZone);

        var gone = result.Entries[1];
        gone.Size.Should().BeNull("the trashed file is missing: its size is unknown, not 0");
        gone.DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Package_log_times_keep_their_zone_status_and_versions()
    {
        var result = await new LinuxArtifactSidecar(FakeChannelFactory.Returning("""
            {"entries": [
              {"timestamp": "2021-03-04T05:06:07", "pm": "apt", "action": "upgrade", "package": "openssl",
               "version": "1.1.1f-1ubuntu2.1", "previous_version": "1.1.1f-1ubuntu2"},
              {"timestamp": "2021-03-04T05:06:07+00:00", "pm": "pacman", "action": "installed", "package": "nmap", "version": "7.91-1"},
              {"timestamp": null, "pm": "apt", "action": "remove", "package": "telnet", "version": "0.17-41.2build1"}
            ], "refused": [], "truncated": []}
            """).Open).LoadPackageLogsAsync("/mnt/ev");

        result.Entries[0].TimeZoneKnown.Should().BeFalse("apt logs local time");
        result.Entries[0].PreviousVersion.Should().Be("1.1.1f-1ubuntu2");
        result.Entries[0].Version.Should().Be("1.1.1f-1ubuntu2.1");
        result.Entries[1].TimeZoneKnown.Should().BeTrue("pacman logs an offset");
        result.Entries[1].Timestamp.Should().Be(new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero));
        result.Entries[2].Timestamp.Should().BeNull("a malformed Start-Date is unknown");
        result.Entries[2].Package.Should().Be("telnet");
    }

    [Fact]
    public async Task Shell_history_cron_and_ssh_rows()
    {
        var sidecar = new LinuxArtifactSidecar(new FakeChannelFactory(() => new FakeSidecarChannel(call => System.Text.Json.Nodes.JsonNode.Parse(call.Method switch
        {
            "shell_history" => """
                {"entries": [{"user": "alice", "shell": "bash", "command": "whoami", "timestamp": "2021-03-04T05:06:07+00:00"},
                             {"user": "alice", "shell": "bash", "command": "id", "timestamp": null}],
                 "refused": [{"path": "/mnt/ev/home/mallory", "reason": "symlink resolves outside the evidence root"}], "truncated": []}
                """,
            "cron" => """
                {"entries": [{"user": "www-data", "schedule": "@reboot", "command": "/usr/local/bin/beacon --quiet", "source": "system"}],
                 "refused": [], "truncated": [], "unparsed_lines": 2}
                """,
            "ssh_known_hosts" => """
                {"entries": [{"user": "alice", "host": "*.example.com", "marker": "@cert-authority", "key_type": "ssh-ed25519", "fingerprint": "SHA256:abc"},
                             {"user": "alice", "host": "h", "marker": null, "key_type": "ssh-rsa", "fingerprint": null}],
                 "refused": [], "truncated": [], "unparsed_lines": 0}
                """,
            _ => throw new InvalidOperationException(call.Method),
        }))).Open);

        var history = await sidecar.LoadShellHistoryAsync("/mnt/ev");
        history.Entries.Select(e => e.Timestamp).Should().Equal(new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero), null);
        history.Refused.Should().ContainSingle().Which.Path.Should().Be("/mnt/ev/home/mallory");

        var cron = await sidecar.LoadCronAsync("/mnt/ev");
        cron.Entries.Single().Should().Be(new CronEntry("www-data", "@reboot", "/usr/local/bin/beacon --quiet", "system"));
        cron.UnparsedLines.Should().Be(2);
        cron.IsComplete.Should().BeFalse();

        var hosts = await sidecar.LoadSshKnownHostsAsync("/mnt/ev");
        hosts.Entries[0].Marker.Should().Be("@cert-authority");
        hosts.Entries[0].Fingerprint.Should().Be("SHA256:abc");
        hosts.Entries[1].Fingerprint.Should().BeNull("an undecodable key has no fingerprint, not an empty one");
    }
}
