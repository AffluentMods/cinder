using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Cinder.Core.Diagnostics;

namespace Cinder.Network;

/// <summary>
/// PCAP / pcapng analysis via <c>parsers/network/pcap_worker.py</c>. The sidecar wraps
/// dpkt + scapy for parsing and emits flow records, HTTP transactions, DNS queries, and
/// JA3/JA4 fingerprints. NetFlow + Zeek imports run through the same dispatcher.
/// </summary>
public sealed class PcapSidecar
{
    private readonly Func<ProcessStartInfo> _factory;
    private readonly ILogger _logger;

    public PcapSidecar(Func<ProcessStartInfo> factory, ILogger? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Path of the worker module this wrapper launches, relative to the parsers directory.</summary>
    public const string WorkerRelativePath = "network/pcap_worker.py";

    /// <summary>
    /// True when the worker this wrapper drives is actually present under
    /// <paramref name="parsersDir"/>. It is not shipped in current builds — PCAP analysis in
    /// the app runs in-process — so callers should check before offering the sidecar path.
    /// </summary>
    public static bool IsAvailable(string parsersDir) =>
        !string.IsNullOrEmpty(parsersDir) && File.Exists(Path.Combine(parsersDir, "network", "pcap_worker.py"));

    /// <exception cref="SidecarException">
    /// The worker module is not present. Launching <c>python -m network.pcap_worker</c> anyway
    /// would die on import and surface as an opaque "Sidecar process ended."
    /// </exception>
    public static ProcessStartInfo DefaultSidecar(string parsersDir)
    {
        if (!IsAvailable(parsersDir))
        {
            throw new SidecarException(
                $"PCAP analysis via the Python sidecar is not available in this build: '{WorkerRelativePath}' " +
                $"was not found under '{parsersDir}'. Use the in-process PCAP reader instead.");
        }
        return new ProcessStartInfo
        {
            FileName = ExecutableResolver.ResolveRequired(
                OperatingSystem.IsWindows() ? "python.exe" : "python3",
                "Install Python 3.12+ and make sure it is on PATH."),
            ArgumentList = { "-m", "network.pcap_worker" },
            WorkingDirectory = parsersDir,
        };
    }

    private static DateTimeOffset Ts(JsonNode? n)
        => DateTimeOffset.TryParse(n?.GetValue<string?>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto) ? dto : DateTimeOffset.MinValue;

    public async IAsyncEnumerable<TcpFlow> FlowsAsync(string pcap, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var resp = await sc.InvokeAsync("flows", new JsonObject { ["pcap"] = pcap }, ct).ConfigureAwait(false);
        foreach (var r in ((resp as JsonObject)?["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new TcpFlow(
                r["src"]?.GetValue<string>() ?? "",
                r["sport"]?.GetValue<int>() ?? 0,
                r["dst"]?.GetValue<string>() ?? "",
                r["dport"]?.GetValue<int>() ?? 0,
                Ts(r["first"]), Ts(r["last"]),
                r["pkts_in"]?.GetValue<long>() ?? 0,
                r["pkts_out"]?.GetValue<long>() ?? 0,
                r["bytes_in"]?.GetValue<long>() ?? 0,
                r["bytes_out"]?.GetValue<long>() ?? 0,
                r["ja3"]?.GetValue<string?>(),
                r["ja4"]?.GetValue<string?>(),
                r["sni"]?.GetValue<string?>());
        }
    }

    public async IAsyncEnumerable<HttpRequest> HttpRequestsAsync(string pcap, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var resp = await sc.InvokeAsync("http", new JsonObject { ["pcap"] = pcap }, ct).ConfigureAwait(false);
        foreach (var r in ((resp as JsonObject)?["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new HttpRequest(
                Ts(r["timestamp"]),
                r["method"]?.GetValue<string>() ?? "GET",
                r["url"]?.GetValue<string>() ?? "",
                r["status"]?.GetValue<int>() ?? 0,
                r["response_bytes"]?.GetValue<long?>(),
                r["host"]?.GetValue<string?>(),
                r["user_agent"]?.GetValue<string?>());
        }
    }

    public async IAsyncEnumerable<DnsQuery> DnsQueriesAsync(string pcap, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var resp = await sc.InvokeAsync("dns", new JsonObject { ["pcap"] = pcap }, ct).ConfigureAwait(false);
        foreach (var r in ((resp as JsonObject)?["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new DnsQuery(
                Ts(r["timestamp"]),
                r["client"]?.GetValue<string>() ?? "",
                r["server"]?.GetValue<string>() ?? "",
                r["name"]?.GetValue<string>() ?? "",
                r["type"]?.GetValue<string>() ?? "",
                r["answer"]?.GetValue<string?>());
        }
    }
}
