using System.Globalization;
using System.Text.Json;

namespace Cinder.Network;

/// <summary>
/// Zeek (formerly Bro) emits one log per protocol — conn.log, http.log, dns.log, ssl.log, etc.
/// Cinder ingests them as TSV, mapping each row to the same artifact records the PCAP path
/// produces so the timeline doesn't care whether evidence came from a PCAP or pre-parsed Zeek
/// logs.
/// </summary>
public sealed class ZeekImporter
{
    public IAsyncEnumerable<TcpFlow> ImportConnLog(string path, CancellationToken ct = default)
        => ImportTsvAsync<TcpFlow>(path, ct, RowToFlow);

    public IAsyncEnumerable<DnsQuery> ImportDnsLog(string path, CancellationToken ct = default)
        => ImportTsvAsync<DnsQuery>(path, ct, RowToDns);

    private static async IAsyncEnumerable<T> ImportTsvAsync<T>(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, Func<IReadOnlyDictionary<string, string>, T?> map)
    {
        using var reader = new StreamReader(path);
        var headers = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (line.StartsWith("#fields", StringComparison.Ordinal))
            {
                headers = [.. line[8..].Split('\t')];
                continue;
            }
            if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            var fields = line.Split('\t');
            if (headers.Count == 0 || fields.Length != headers.Count)
            {
                continue;
            }
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < headers.Count; i++)
            {
                row[headers[i]] = fields[i];
            }
            var mapped = map(row);
            if (mapped is not null)
            {
                yield return mapped;
            }
        }
    }

    private static TcpFlow? RowToFlow(IReadOnlyDictionary<string, string> r)
    {
        if (!double.TryParse(r.GetValueOrDefault("ts", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tsSec))
        {
            return null;
        }
        if (!TryFromEpochSeconds(tsSec, out var ts))
        {
            return null;
        }

        // Zeek's duration is the flow's length in seconds ("-" when unknown).
        var last = ts;
        if (double.TryParse(r.GetValueOrDefault("duration", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            && duration > 0
            && TryFromEpochSeconds(tsSec + duration, out var end))
        {
            last = end;
        }

        // Direction is from the originator's side, which is TcpFlow.Source: what the originator
        // sent is "out" (rendered ↑), what the responder sent back is "in" (↓). These were
        // passed the other way round, so an upload read as a download.
        var packetsOut = long.TryParse(r.GetValueOrDefault("orig_pkts", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var po) ? po : 0;
        var packetsIn = long.TryParse(r.GetValueOrDefault("resp_pkts", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pi) ? pi : 0;
        var bytesOut = long.TryParse(r.GetValueOrDefault("orig_bytes", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bo) ? bo : 0;
        var bytesIn = long.TryParse(r.GetValueOrDefault("resp_bytes", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bi) ? bi : 0;

        return new TcpFlow(
            Source: r.GetValueOrDefault("id.orig_h", ""),
            SourcePort: int.TryParse(r.GetValueOrDefault("id.orig_p", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sp) ? sp : 0,
            Destination: r.GetValueOrDefault("id.resp_h", ""),
            DestinationPort: int.TryParse(r.GetValueOrDefault("id.resp_p", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dp) ? dp : 0,
            First: ts,
            Last: last,
            PacketsIn: packetsIn,
            PacketsOut: packetsOut,
            BytesIn: bytesIn,
            BytesOut: bytesOut,
            Ja3: null, Ja4: null, ServerName: null);
    }

    /// <summary>
    /// Epoch seconds to a timestamp, refusing NaN, infinities and values outside what
    /// <see cref="DateTimeOffset"/> can hold. A log is evidence: one absurd <c>ts</c> must cost
    /// that row, not throw out of the enumeration and take every later row with it.
    /// </summary>
    private static bool TryFromEpochSeconds(double seconds, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!double.IsFinite(seconds))
        {
            return false;
        }
        var ms = seconds * 1000d;
        if (ms < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() || ms > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            return false;
        }
        timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        return true;
    }

    private static DnsQuery? RowToDns(IReadOnlyDictionary<string, string> r)
    {
        if (!double.TryParse(r.GetValueOrDefault("ts", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tsSec))
        {
            return null;
        }
        if (!TryFromEpochSeconds(tsSec, out var ts))
        {
            return null;
        }
        return new DnsQuery(
            ts,
            r.GetValueOrDefault("id.orig_h", ""),
            r.GetValueOrDefault("id.resp_h", ""),
            r.GetValueOrDefault("query", ""),
            r.GetValueOrDefault("qtype_name", ""),
            r.GetValueOrDefault("answers", null!));
    }

    public static string ToJson(IEnumerable<TcpFlow> flows) => JsonSerializer.Serialize(flows);
}
