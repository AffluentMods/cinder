using Cinder.Mobile;
using Cinder.Network;
using Cinder.Sidecar;
using FluentAssertions;
using Xunit;

namespace Cinder.Libs.Tests;

public sealed class ZeekImporterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-zeek").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteLog(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }
        return list;
    }

    private const string ConnFields =
        "#fields\tts\tuid\tid.orig_h\tid.orig_p\tid.resp_h\tid.resp_p\tproto\tduration\torig_bytes\tresp_bytes\torig_pkts\tresp_pkts";

    [Fact]
    public async Task What_the_originator_sent_is_out_and_what_came_back_is_in()
    {
        // An exfiltration: the internal host (originator) sends 1 MB and gets 200 bytes back.
        var log = WriteLog("conn.log",
            "#separator \\x09",
            ConnFields,
            "1700000000.500000\tCabc\t10.0.0.5\t49152\t203.0.113.9\t443\ttcp\t12.25\t1000000\t200\t700\t3");

        var flow = (await Collect(new ZeekImporter().ImportConnLog(log))).Single();

        flow.SourceAddress.Should().Be("10.0.0.5", "the originator must be readable from the record, not only from its summary");
        ((Cinder.Artifacts.IArtifact)flow).Source.Should().Be("pcap.tcp");
        flow.SourcePort.Should().Be(49152);
        flow.Destination.Should().Be("203.0.113.9");
        flow.BytesOut.Should().Be(1_000_000, "orig_bytes is what the originator sent");
        flow.BytesIn.Should().Be(200, "resp_bytes is what it received");
        flow.PacketsOut.Should().Be(700);
        flow.PacketsIn.Should().Be(3);
        flow.Summary.Should().Contain("(1000000↑/200↓)", "an upload must not read as a download");
    }

    [Fact]
    public async Task The_flow_ends_duration_after_it_starts()
    {
        var log = WriteLog("conn.log", ConnFields,
            "1700000000.000000\tC1\t10.0.0.5\t1\t10.0.0.6\t2\ttcp\t90.5\t1\t1\t1\t1",
            "1700000100.000000\tC2\t10.0.0.5\t1\t10.0.0.6\t2\ttcp\t-\t-\t-\t-\t-");

        var flows = await Collect(new ZeekImporter().ImportConnLog(log));

        flows[0].First.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        flows[0].Last.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000090500));
        flows[1].Last.Should().Be(flows[1].First, "Zeek writes '-' for an unknown duration");
        flows[1].BytesOut.Should().Be(0);
    }

    [Theory]
    [InlineData("1e30")]
    [InlineData("-1e30")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("99999999999999999999")]
    public async Task One_absurd_timestamp_costs_that_row_not_the_rest_of_the_log(string hostileTs)
    {
        var log = WriteLog("conn.log", ConnFields,
            "1700000000.0\tC1\t10.0.0.1\t1\t10.0.0.2\t2\ttcp\t1\t10\t20\t1\t1",
            hostileTs + "\tC2\t10.0.0.1\t1\t10.0.0.2\t2\ttcp\t1\t10\t20\t1\t1",
            "1700000002.0\tC3\t10.0.0.1\t1\t10.0.0.2\t2\ttcp\t1e300\t10\t20\t1\t1",
            "1700000003.0\tC4\t10.0.0.1\t1\t10.0.0.2\t2\ttcp\t1\t10\t20\t1\t1");
        var dns = WriteLog("dns.log",
            "#fields\tts\tid.orig_h\tid.resp_h\tquery\tqtype_name\tanswers",
            hostileTs + "\t10.0.0.1\t10.0.0.53\tbad.example\tA\t-",
            "1700000001.0\t10.0.0.1\t10.0.0.53\tgood.example\tA\t192.0.2.1");

        var flows = await Collect(new ZeekImporter().ImportConnLog(log));
        var queries = await Collect(new ZeekImporter().ImportDnsLog(dns));

        flows.Select(f => f.First.ToUnixTimeSeconds()).Should().Equal(1700000000, 1700000002, 1700000003);
        flows[1].Last.Should().Be(flows[1].First, "a duration that overflows is ignored, not thrown");
        queries.Single().Name.Should().Be("good.example");
    }
}

/// <summary>
/// <c>PcapSidecar</c> and <c>MobileBackupSidecar</c> drive Python workers that are not shipped
/// (<c>parsers/network</c> and <c>parsers/mobile</c> do not exist). Starting Python on a missing
/// module dies on import, and the caller saw only "Sidecar process ended." They now say what is
/// actually wrong, before any process is started.
/// </summary>
public sealed class SidecarAvailabilityTests : IDisposable
{
    private readonly string _parsers = Directory.CreateTempSubdirectory("cinder-parsers").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_parsers, recursive: true); } catch { }
    }

    [Fact]
    public void Pcap_sidecar_reports_that_its_worker_is_not_available()
    {
        PcapSidecar.IsAvailable(_parsers).Should().BeFalse();

        var act = () => PcapSidecar.DefaultSidecar(_parsers);

        act.Should().Throw<SidecarException>()
            .WithMessage("*not available*").WithMessage("*network/pcap_worker.py*").WithMessage($"*{_parsers}*");
    }

    [Fact]
    public void Mobile_sidecar_reports_that_its_worker_is_not_available()
    {
        MobileBackupSidecar.IsAvailable(_parsers).Should().BeFalse();

        var act = () => MobileBackupSidecar.DefaultSidecar(_parsers);

        act.Should().Throw<SidecarException>()
            .WithMessage("*not available*").WithMessage("*mobile/mobile_worker.py*");
    }

    [Fact]
    public void A_worker_that_is_present_is_reported_available()
    {
        Directory.CreateDirectory(Path.Combine(_parsers, "network"));
        File.WriteAllText(Path.Combine(_parsers, "network", "pcap_worker.py"), "# placeholder");
        Directory.CreateDirectory(Path.Combine(_parsers, "mobile"));
        File.WriteAllText(Path.Combine(_parsers, "mobile", "mobile_worker.py"), "# placeholder");

        PcapSidecar.IsAvailable(_parsers).Should().BeTrue();
        MobileBackupSidecar.IsAvailable(_parsers).Should().BeTrue();
    }
}
