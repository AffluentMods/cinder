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
        => ImportTsv<TcpFlow>(path, ct, RowToFlow);

    public IAsyncEnumerable<DnsQuery> ImportDnsLog(string path, CancellationToken ct = default)
        => ImportTsv<DnsQuery>(path, ct, RowToDns);

    private static async IAsyncEnumerable<T> ImportTsv<T>(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, Func<IReadOnlyDictionary<string, string>, T?> map)
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
        var ts = DateTimeOffset.FromUnixTimeMilliseconds((long)(tsSec * 1000));
        return new TcpFlow(
            r.GetValueOrDefault("id.orig_h", ""),
            int.TryParse(r.GetValueOrDefault("id.orig_p", "0"), out var sp) ? sp : 0,
            r.GetValueOrDefault("id.resp_h", ""),
            int.TryParse(r.GetValueOrDefault("id.resp_p", "0"), out var dp) ? dp : 0,
            ts, ts,
            long.TryParse(r.GetValueOrDefault("orig_pkts", "0"), out var po) ? po : 0,
            long.TryParse(r.GetValueOrDefault("resp_pkts", "0"), out var pi) ? pi : 0,
            long.TryParse(r.GetValueOrDefault("orig_bytes", "0"), out var bo) ? bo : 0,
            long.TryParse(r.GetValueOrDefault("resp_bytes", "0"), out var bi) ? bi : 0,
            null, null, null);
    }

    private static DnsQuery? RowToDns(IReadOnlyDictionary<string, string> r)
    {
        if (!double.TryParse(r.GetValueOrDefault("ts", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tsSec))
        {
            return null;
        }
        return new DnsQuery(
            DateTimeOffset.FromUnixTimeMilliseconds((long)(tsSec * 1000)),
            r.GetValueOrDefault("id.orig_h", ""),
            r.GetValueOrDefault("id.resp_h", ""),
            r.GetValueOrDefault("query", ""),
            r.GetValueOrDefault("qtype_name", ""),
            r.GetValueOrDefault("answers", null!));
    }

    public static string ToJson(IEnumerable<TcpFlow> flows) => JsonSerializer.Serialize(flows);
}
