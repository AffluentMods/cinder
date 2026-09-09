using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cinder.Core.Analysis;
using Cinder.Search;

namespace Cinder.App.Services;

/// <summary>One indicator hit, shaped for the IOC tool's grid.</summary>
public sealed record IocMatchRow(string Indicator, string Type, string Where, string Location, string At, string Context);

public sealed class IocScanStats
{
    public int Files { get; set; }
    public int FilesHashed { get; set; }
    public int FilesScanned { get; set; }
    public int TimelineEvents { get; set; }
    public int Errors { get; set; }
    public override string ToString() =>
        $"{Files:N0} files · {FilesHashed:N0} hashed · {FilesScanned:N0} content-scanned · {TimelineEvents:N0} timeline events · {Errors} errors";
}

/// <summary>
/// Runs an IOC list against a folder of evidence four ways at once:
/// <list type="number">
/// <item><b>Hashes</b> — MD5 / SHA-1 / SHA-256 of every file up to the size cap, compared to
/// hash indicators.</item>
/// <item><b>Paths</b> — every text/domain/email/URL indicator matched against full paths.</item>
/// <item><b>Content</b> — the same indicators compiled into a YARA-lite ruleset (ASCII and
/// UTF-16LE forms) and run over file bytes through the Aho-Corasick scanner.</item>
/// <item><b>Timeline</b> — the folder ingested through <see cref="TimelineIngester"/> and each
/// event's summary and user searched, which is where an IP in an event log or a domain in
/// browser history turns up.</item>
/// </list>
/// Everything is bounded: file count, per-file size, rows returned. Errors are counted, never
/// thrown, because one unreadable file must not stop a scan of ten thousand.
/// </summary>
public static class IocScanner
{
    private const long MaxHashBytes = 256L * 1024 * 1024;
    private const long MaxContentBytes = 512L * 1024 * 1024;
    private const int MaxFiles = 200_000;

    public static async Task<(List<IocMatchRow> Rows, IocScanStats Stats)> ScanAsync(
        IReadOnlyList<Indicator> indicators,
        string folder,
        IProgress<string>? progress,
        int maxRows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(indicators);
        var rows = new List<IocMatchRow>();
        var stats = new IocScanStats();
        if (indicators.Count == 0 || !Directory.Exists(folder))
        {
            return (rows, stats);
        }

        var hashIocs = indicators.Where(i => i.IsHash)
            .ToDictionary(i => i.Value.ToLowerInvariant(), i => i, StringComparer.OrdinalIgnoreCase);
        var textIocs = indicators.Where(i => !i.IsHash).ToList();

        // ---- files: paths, hashes, content ----
        var ruleset = textIocs.Count > 0 ? BuildRuleset(textIocs) : null;
        var files = SafeEnumerate(folder, ct).Take(MaxFiles).ToList();
        stats.Files = files.Count;

        var n = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= maxRows) break;
            if ((++n & 63) == 0) progress?.Report($"files {n:N0}/{files.Count:N0} · {rows.Count:N0} hits");

            foreach (var ioc in textIocs)
            {
                if (file.Contains(ioc.Value, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(new IocMatchRow(ioc.Value, ioc.Type.ToString(), "path", file, "", Path.GetFileName(file)));
                }
            }

            long size;
            try { size = new FileInfo(file).Length; }
            catch { stats.Errors++; continue; }

            if (hashIocs.Count > 0 && size <= MaxHashBytes)
            {
                try
                {
                    var (md5, sha1, sha256) = await HashAsync(file, ct);
                    stats.FilesHashed++;
                    foreach (var h in new[] { md5, sha1, sha256 })
                    {
                        if (hashIocs.TryGetValue(h, out var ioc))
                        {
                            rows.Add(new IocMatchRow(ioc.Value, ioc.Type.ToString(), "hash", file, "", $"{ioc.Type} matches file digest"));
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { stats.Errors++; }
            }

            if (ruleset is not null && size > 0 && size <= MaxContentBytes)
            {
                try
                {
                    await foreach (var m in ruleset.ScanAsync(file, ct))
                    {
                        if (rows.Count >= maxRows) break;
                        var ioc = textIocs[int.Parse(m.RuleName["ioc_".Length..], CultureInfo.InvariantCulture)];
                        rows.Add(new IocMatchRow(ioc.Value, ioc.Type.ToString(), "content", file,
                            "0x" + m.Offset.ToString("X", CultureInfo.InvariantCulture),
                            m.Identifier == "$w" ? "UTF-16LE" : "ASCII"));
                    }
                    stats.FilesScanned++;
                }
                catch (OperationCanceledException) { throw; }
                catch { stats.Errors++; }
            }
        }

        // ---- timeline ----
        if (textIocs.Count > 0 && rows.Count < maxRows)
        {
            progress?.Report("ingesting timeline…");
            var timeline = new SuperTimeline();
            try
            {
                await TimelineIngester.IngestAsync(timeline, folder, null, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { stats.Errors++; }

            stats.TimelineEvents = timeline.Count;
            foreach (var e in timeline.Range(DateTimeOffset.MinValue, DateTimeOffset.MaxValue))
            {
                if (rows.Count >= maxRows) break;
                foreach (var ioc in textIocs)
                {
                    if (e.Summary.Contains(ioc.Value, StringComparison.OrdinalIgnoreCase) ||
                        (e.User?.Contains(ioc.Value, StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        rows.Add(new IocMatchRow(ioc.Value, ioc.Type.ToString(), "timeline", e.Source,
                            e.Timestamp.ToString("u", CultureInfo.InvariantCulture), e.Summary));
                    }
                }
            }
        }

        return (rows, stats);
    }

    /// <summary>One rule per indicator, ASCII and UTF-16LE forms, so both a log line and a registry value hit.</summary>
    private static YaraLiteRuleset BuildRuleset(IReadOnlyList<Indicator> textIocs)
    {
        var rules = new List<YaraLiteRule>(textIocs.Count);
        for (int i = 0; i < textIocs.Count; i++)
        {
            var v = textIocs[i].Value;
            rules.Add(new YaraLiteRule
            {
                Name = "ioc_" + i.ToString(CultureInfo.InvariantCulture),
                Strings =
                [
                    new YaraLiteString { Identifier = "$a", Pattern = Encoding.ASCII.GetBytes(v), NoCase = true },
                    new YaraLiteString { Identifier = "$w", Pattern = Encoding.Unicode.GetBytes(v) },
                ],
                Condition = "any of them",
            });
        }
        return YaraLiteRuleset.Compile(rules);
    }

    private static async Task<(string Md5, string Sha1, string Sha256)> HashAsync(string path, CancellationToken ct)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var buffer = new byte[1 << 16];
        while (true)
        {
            var n = await fs.ReadAsync(buffer, ct);
            if (n == 0) break;
            md5.AppendData(buffer, 0, n);
            sha1.AppendData(buffer, 0, n);
            sha256.AppendData(buffer, 0, n);
        }
        return (Convert.ToHexStringLower(md5.GetHashAndReset()),
                Convert.ToHexStringLower(sha1.GetHashAndReset()),
                Convert.ToHexStringLower(sha256.GetHashAndReset()));
    }

    private static IEnumerable<string> SafeEnumerate(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs) stack.Push(d);
        }
    }
}
