using System.Text.Json.Nodes;
using Cinder.Artifacts.Windows;
using Cinder.Memory;
using FluentAssertions;
using Xunit;

namespace Cinder.Sidecar.Tests;

/// <summary>WindowsArtifactSidecar against canned replies shaped like <c>parsers/windows/win_worker.py</c>'s.</summary>
public sealed class WindowsArtifactSidecarTests
{
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }
        return list;
    }

    private const string PrefetchReply = """
        {"entries": [
          {"executable": "CALC.EXE", "hash": "DEADBEEF", "run_count": 7, "last_run": "2021-03-04T05:06:07+00:00",
           "all_run_times": ["2021-03-04T05:06:07+00:00", "2020-03-04T05:06:07+00:00", null, "garbage"],
           "loaded_files": [], "version": 26, "source_file": "CALC.EXE-11111111.pf"},
          {"executable": "OLD.EXE", "hash": "0000ABCD", "run_count": 0, "last_run": null, "all_run_times": [],
           "loaded_files": [], "version": 17, "source_file": "OLD.EXE-22222222.pf"}
        ],
         "skipped": [
          {"file": "CHROME.EXE-33333333.pf", "reason": "compressed (MAM), not decoded"},
          {"file": "EDGE.EXE-44444444.pf", "reason": "compressed (MAM), not decoded"},
          {"file": "JUNK.pf", "reason": "not a Prefetch file (no SCCA signature at offset 4)"}
         ],
         "compressed_not_decoded": 2}
        """;

    [Fact]
    public async Task Prefetch_reports_the_files_it_did_not_parse()
    {
        var factory = FakeChannelFactory.Returning(PrefetchReply);

        var result = await new WindowsArtifactSidecar(factory.Open).LoadPrefetchAsync(@"C:\Windows\Prefetch");

        factory.Calls.Single().Params["prefetch_dir"]!.GetValue<string>().Should().Be(@"C:\Windows\Prefetch");
        result.Entries.Should().HaveCount(2);
        result.Skipped.Should().HaveCount(3);
        result.Skipped[0].Should().Be(new SkippedPrefetchFile("CHROME.EXE-33333333.pf", "compressed (MAM), not decoded"));
        result.CompressedNotDecoded.Should().Be(2);

        var calc = result.Entries[0];
        calc.ExecutableName.Should().Be("CALC.EXE");
        calc.RunCount.Should().Be(7);
        calc.LastRun.Should().Be(new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero));
        calc.AllRunTimes.Should().HaveCount(2, "unusable run times are left out, not replaced");
        calc.FormatVersion.Should().Be(26);
        calc.SourceFile.Should().Be("CALC.EXE-11111111.pf");
        result.Entries[1].LastRun.Should().BeNull();
    }

    [Fact]
    public async Task A_directory_of_only_compressed_files_is_not_an_empty_result()
    {
        var result = await new WindowsArtifactSidecar(FakeChannelFactory.Returning("""
            {"entries": [], "skipped": [{"file": "A.pf", "reason": "compressed (MAM), not decoded"},
                                        {"file": "B.pf", "reason": "compressed (MAM), not decoded"}]}
            """).Open).LoadPrefetchAsync("p");

        result.Entries.Should().BeEmpty();
        result.CompressedNotDecoded.Should().Be(2, "counted from the reasons when the worker sends no total");
        (await Collect(new WindowsArtifactSidecar(FakeChannelFactory.Returning(PrefetchReply).Open).ReadPrefetchAsync("p")))
            .Should().HaveCount(2);
    }

    private const string HistoryReply = """
        {"entries": [
          {"user": "alice", "browser": "chrome", "url": "https://example.com/good", "title": "Good", "visit_count": 3,
           "timestamp": "2021-03-04T05:06:07+00:00", "visit_type": null},
          {"user": "alice", "browser": "chrome", "url": "https://example.com/never", "title": "", "visit_count": 0,
           "timestamp": null, "visit_type": null},
          {"user": "alice", "browser": "chrome", "url": "https://evil/text-count", "title": "txt", "visit_count": null,
           "timestamp": null, "visit_type": null},
          {"url": "https://evil/huge", "title": null, "visit_count": 99999999999, "timestamp": "30000-01-01T00:00:00+00:00"}
        ],
         "row_errors": 1, "timestamp_errors": 2, "read_error": "database disk image is malformed"}
        """;

    [Fact]
    public async Task Browser_history_does_not_invent_a_visit_or_a_date()
    {
        var factory = FakeChannelFactory.Returning(HistoryReply);

        var result = await new WindowsArtifactSidecar(factory.Open).LoadBrowserHistoryAsync(@"C:\p\Default", "Chrome", "alice");

        var call = factory.Calls.Single();
        call.Method.Should().Be("browser_history");
        call.Params["profile_path"]!.GetValue<string>().Should().Be(@"C:\p\Default");
        call.Params["browser"]!.GetValue<string>().Should().Be("Chrome");

        result.Entries.Should().HaveCount(4);
        result.Entries[0].VisitCount.Should().Be(3);
        result.Entries[0].Timestamp.Should().Be(new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero));
        result.Entries[1].VisitCount.Should().Be(0, "never visited is 0 visits, not 1");
        result.Entries[1].Timestamp.Should().BeNull("never visited has no visit time, not year 0001");
        result.Entries[2].VisitCount.Should().BeNull("the database held no usable count");
        result.Entries[3].VisitCount.Should().BeNull("a count that does not fit is not wrapped into one that does");
        result.Entries[3].Timestamp.Should().BeNull();
        result.Entries[3].User.Should().Be("alice", "the request's user fills a field the worker left out");
        result.Entries[3].Browser.Should().Be("Chrome");
        result.Entries.Should().NotContain(e => e.Timestamp == DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task Browser_history_errors_are_part_of_the_result()
    {
        var result = await new WindowsArtifactSidecar(FakeChannelFactory.Returning(HistoryReply).Open)
            .LoadBrowserHistoryAsync("p", "chrome", "alice");

        result.RowErrors.Should().Be(1);
        result.TimestampErrors.Should().Be(2);
        result.ReadError.Should().Be("database disk image is malformed");
        result.IsComplete.Should().BeFalse();

        var clean = await new WindowsArtifactSidecar(FakeChannelFactory.Returning(
                """{"entries": [], "row_errors": 0, "timestamp_errors": 0}""").Open)
            .LoadBrowserHistoryAsync("p", "firefox", "bob");
        clean.IsComplete.Should().BeTrue();
        clean.ReadError.Should().BeNull();
    }

    [Fact]
    public async Task Event_log_time_is_utc_by_definition_and_null_when_absent()
    {
        var factory = FakeChannelFactory.Returning("""
            {"entries": [
              {"record_id": 12345678901, "event_id": 4624, "provider": "Microsoft-Windows-Security-Auditing", "channel": "Security",
               "computer": "WS01", "user": null, "timestamp": "2021-03-04 05:06:07.500000", "level": "0", "summary": "LogonType=10"},
              {"record_id": 2, "event_id": 1102, "provider": "p", "channel": "Security", "computer": "WS01", "user": null,
               "timestamp": "", "level": "4", "summary": "EventID 1102"}
            ]}
            """);

        var records = await Collect(new WindowsArtifactSidecar(factory.Open).ReadEvtxAsync(@"C:\ev\Security.evtx"));

        records[0].EventRecordId.Should().Be(12345678901);
        records[0].Timestamp.Should().Be(new DateTimeOffset(2021, 3, 4, 5, 6, 7, 500, TimeSpan.Zero));
        records[1].Timestamp.Should().BeNull("a record without a readable time is not dated to year 0001");
        factory.Calls.Should().ContainSingle().Which.Params["cursor"]!.GetValue<long>().Should().Be(0);
    }

    [Fact]
    public async Task Event_log_pages_until_a_short_page()
    {
        static string Page(int count)
        {
            var rows = new JsonArray();
            for (var i = 0; i < count; i++)
            {
                rows.Add(new JsonObject { ["record_id"] = i, ["event_id"] = 1, ["timestamp"] = "2021-03-04 05:06:07" });
            }
            return new JsonObject { ["entries"] = rows }.ToJsonString();
        }
        var factory = new FakeChannelFactory(() => FakeSidecarChannel.Sequence(Page(1000), Page(1000), Page(4)));

        var records = await Collect(new WindowsArtifactSidecar(factory.Open).ReadEvtxAsync("x.evtx"));

        records.Should().HaveCount(2004);
        factory.Calls.Select(c => c.Params["cursor"]!.GetValue<long>()).Should().Equal(0, 1000, 2000);
        factory.Opened.Should().HaveCount(1);
    }

    [Fact]
    public async Task Registry_and_srum_rows_tolerate_nulls()
    {
        var sidecar = new WindowsArtifactSidecar(new FakeChannelFactory(() => new FakeSidecarChannel(call => JsonNode.Parse(call.Method switch
        {
            "registry_userassist" => """
                {"entries": [{"user": "", "program": "cmd.exe", "run_count": 4, "focus_ms": 1500, "last_executed": "2021-03-04T05:06:07+00:00"},
                             {"user": "", "program": "x.exe", "run_count": 0, "focus_ms": null, "last_executed": null},
                             {"user": "", "program": "y.exe", "run_count": 1, "focus_ms": 9223372036854775807, "last_executed": "nonsense"}]}
                """,
            "srum_applications" => """{"entries": [{"user": "?", "application": "?", "timestamp": null, "fg_cpu_ms": 0, "bytes_read": 0, "bytes_written": 0}]}""",
            _ => throw new InvalidOperationException(call.Method),
        }))).Open);

        var assist = await Collect(sidecar.ReadUserAssistAsync("NTUSER.DAT"));
        assist[0].FocusTime.Should().Be(TimeSpan.FromMilliseconds(1500));
        assist[0].LastExecuted.Should().Be(new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero));
        assist[1].FocusTime.Should().BeNull();
        assist[1].LastExecuted.Should().BeNull();
        assist[2].FocusTime.Should().BeNull("a duration that does not fit is unknown");
        assist[2].LastExecuted.Should().BeNull();

        (await Collect(sidecar.ReadSrumApplicationsAsync("SRUDB.dat"))).Single().Timestamp.Should().BeNull();
    }
}

/// <summary>VolatilitySidecar against canned replies shaped like <c>parsers/memory/vol_worker.py</c>'s.</summary>
public sealed class VolatilitySidecarTests
{
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }
        return list;
    }

    [Fact]
    public async Task Fields_volatility_could_not_read_stay_unknown()
    {
        var factory = FakeChannelFactory.Returning("""
            {"entries": [
              {"pid": 4, "ppid": 0, "image": "System", "cmdline": null, "created_at": "2021-03-04T05:06:07+00:00", "exited_at": null,
               "threads": 164, "handles": null, "session": null, "integrity": null, "anomalies": []},
              {"pid": null, "ppid": null, "image": "", "cmdline": "evil.exe -hidden", "created_at": null, "exited_at": null,
               "threads": null, "handles": 12, "session": "1", "integrity": null, "anomalies": ["hidden", 7]},
              {"pid": 0, "ppid": 0, "image": "Idle", "created_at": "2021-03-04 05:06:07", "threads": 99999999999, "handles": "12"}
            ]}
            """);

        var processes = await Collect(new VolatilitySidecar(factory.Open).ProcessTreeAsync("memory.raw"));

        factory.Calls.Single().Method.Should().Be("pstree");
        factory.Calls.Single().Params["image"]!.GetValue<string>().Should().Be("memory.raw");

        var system = processes[0];
        system.Pid.Should().Be(4);
        system.ParentPid.Should().Be(0);
        system.Handles.Should().BeNull("not recovered is not 'no handles'");
        system.Threads.Should().Be(164);
        system.CreatedAt.Should().Be(new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero));
        system.ExitedAt.Should().BeNull();
        system.Suspicious.Should().BeFalse();

        var smeared = processes[1];
        smeared.Pid.Should().BeNull("an unreadable PID is not PID 0, which is a real process");
        smeared.ParentPid.Should().BeNull();
        smeared.Threads.Should().BeNull();
        smeared.Handles.Should().Be(12);
        smeared.CreatedAt.Should().BeNull();
        smeared.Suspicious.Should().BeTrue();
        smeared.Anomalies.Should().Equal("hidden", "7");
        smeared.Summary.Should().Be(" pid=?");

        var idle = processes[2];
        idle.Pid.Should().Be(0, "a real 0 is still 0");
        idle.Threads.Should().BeNull("a count that does not fit is not wrapped");
        idle.Handles.Should().Be(12);
        idle.CreatedAt.Should().Be(new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero), "kernel times are UTC whether or not the text says so");
    }

    [Fact]
    public async Task Connections_modules_and_injections()
    {
        var sidecar = new VolatilitySidecar(new FakeChannelFactory(() => new FakeSidecarChannel(call => JsonNode.Parse(call.Method switch
        {
            "netscan" => """
                {"entries": [{"pid": null, "proto": "TCPv4", "local_addr": "0.0.0.0", "local_port": 445, "remote_addr": "*",
                              "remote_port": null, "state": "LISTENING", "created_at": null}]}
                """,
            "dlllist" => """
                {"entries": [{"pid": 4, "name": "ntdll.dll", "path": "", "base": "140715000000000", "size": null, "signed": false},
                             {"pid": 4, "name": "big.dll", "path": "C:\\big.dll", "base": "4096", "size": 6000000000, "signed": false}]}
                """,
            "malfind" => """
                {"entries": [{"pid": 620, "image_name": "lsass.exe", "type": "malfind", "address": "2031616", "length": null,
                              "notes": "f0f1f2f34d5a9000"}]}
                """,
            _ => throw new InvalidOperationException(call.Method),
        }))).Open);

        var connection = (await Collect(sidecar.NetScanAsync("m.raw"))).Single();
        connection.Pid.Should().BeNull();
        connection.LocalPort.Should().Be(445);
        connection.RemotePort.Should().BeNull("a listening socket has no remote port; that is not port 0");
        connection.Summary.Should().Be("TCPv4 0.0.0.0:445 → *:? (LISTENING)");

        var modules = await Collect(sidecar.ListModulesAsync("m.raw", pid: 4));
        modules[0].Size.Should().BeNull();
        modules[1].Size.Should().Be(6_000_000_000);

        var finding = (await Collect(sidecar.MalfindAsync("m.raw"))).Single();
        finding.Length.Should().BeNull();
        finding.Notes.Should().Be("f0f1f2f34d5a9000");
        finding.Pid.Should().Be(620);
    }

    [Fact]
    public async Task The_pid_filter_is_sent_only_when_given()
    {
        var factory = FakeChannelFactory.Returning("""{"entries": []}""");
        var sidecar = new VolatilitySidecar(factory.Open);

        await Collect(sidecar.ListModulesAsync("m.raw", pid: 4));
        await Collect(sidecar.ListModulesAsync("m.raw"));

        var calls = factory.Calls.ToList();
        calls[0].Params["pid"]!.GetValue<int>().Should().Be(4);
        calls[1].Params.ContainsKey("pid").Should().BeFalse();
    }

    [Fact]
    public async Task Hashes_that_were_not_recovered_are_null_not_an_empty_pair()
    {
        var hashes = await Collect(new VolatilitySidecar(FakeChannelFactory.Returning("""
            {"entries": [
              {"plugin": "hashdump", "account": "Administrator", "domain": null, "rid": 500,
               "lm": "aad3b435b51404eeaad3b435b51404ee", "nt": "31d6cfe0d16ae931b73c59d7e0c089c0",
               "hash": "aad3b435b51404eeaad3b435b51404ee:31d6cfe0d16ae931b73c59d7e0c089c0", "last_change": null},
              {"plugin": "hashdump", "account": "ghost", "domain": null, "rid": null, "lm": null, "nt": null, "hash": null, "last_change": null}
            ]}
            """).Open).HashDumpAsync("m.raw"));

        hashes[0].Hash.Should().Be("aad3b435b51404eeaad3b435b51404ee:31d6cfe0d16ae931b73c59d7e0c089c0");
        hashes[0].Rid.Should().Be(500);
        hashes[0].NtHash.Should().Be("31d6cfe0d16ae931b73c59d7e0c089c0");
        hashes[1].Hash.Should().BeNull();
        hashes[1].Rid.Should().BeNull();
        hashes[1].LmHash.Should().BeNull();
        hashes[1].Account.Should().Be("ghost");
    }
}
