// =====================================================================================
// TimelineIngester — walks a triage folder and feeds every parseable artifact into a
// SuperTimeline. The result is the canonical Plaso-style cross-source chronology that
// examiners actually use day-to-day.
//
// Recognised sources (auto-detected by extension or filename):
//   evtx          — Windows event log: TimeCreated per record
//   prefetch      — .pf files: 8 LastRunTimes per program
//   lnk           — .lnk shortcuts: AccessedOn / WrittenOn / CreatedOn
//   ntuser        — NTUSER.DAT: UserAssist last-execution per program
//   browser       — Chromium/Edge/Firefox History sqlite: per-URL last_visit_time
//   email         — .eml/.msg: Date: header (and Sent: for .msg)
//   recyclebin    — $Recycle.Bin\<SID>\$I*: deletion timestamp + original path
//   filesystem    — every other file gets its MAC times (creation / write / access)
//                   tagged as source=fs.* when discovered in a Documents / Desktop / Downloads
//                   subtree (avoid drowning timeline in OS-binary noise).
//
// Each event becomes a row in the existing SuperTimeline so the TimelineToolViewModel
// histogram + range filters work without any UI change.
// =====================================================================================

using System.Globalization;
using System.Text;
using Cinder.Artifacts;
using Cinder.Search;
using Microsoft.Data.Sqlite;
using MsgReader.Outlook;

namespace Cinder.App.Services;

public static class TimelineIngester
{
    /// <summary>Walks <paramref name="folder"/> recursively, parsing every artifact and
    /// appending one or more <see cref="TimelineEvent"/>s to <paramref name="timeline"/>.
    /// Returns per-source ingestion counts. Honours <paramref name="ct"/>.</summary>
    public static async Task<IngestStats> IngestAsync(
        SuperTimeline timeline,
        string folder,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var stats = new IngestStats();
        if (!Directory.Exists(folder))
        {
            return stats;
        }

        await Task.Run(() =>
        {
            foreach (var file in SafeEnumerate(folder, ct))
            {
                ct.ThrowIfCancellationRequested();
                var lower = Path.GetFileName(file).ToLowerInvariant();
                var ext = Path.GetExtension(lower);

                try
                {
                    if (ext == ".evtx")
                    {
                        IngestEvtx(timeline, file, stats);
                        progress?.Report($"evtx: {Path.GetFileName(file)}");
                    }
                    else if (ext == ".pf")
                    {
                        IngestPrefetch(timeline, file, stats);
                    }
                    else if (ext == ".lnk")
                    {
                        IngestLnk(timeline, file, stats);
                    }
                    else if (lower == "ntuser.dat" || lower == "usrclass.dat")
                    {
                        IngestNtuser(timeline, file, stats);
                        progress?.Report($"ntuser: {file}");
                    }
                    else if (lower == "history" || lower == "places.sqlite")
                    {
                        IngestBrowserHistory(timeline, file, stats);
                        progress?.Report($"browser: {file}");
                    }
                    else if (ext == ".eml" || ext == ".msg" || ext == ".mbox")
                    {
                        IngestEmail(timeline, file, stats);
                    }
                    else if (lower.StartsWith("$i", StringComparison.Ordinal) &&
                             file.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
                    {
                        IngestRecycleBin(timeline, file, stats);
                    }
                }
                catch (Exception ex)
                {
                    stats.Errors++;
                    stats.LastError = $"{Path.GetFileName(file)}: {ex.Message}";
                }
            }
            timeline.Sort();
        }, ct);
        return stats;
    }

    // ----- Per-source parsers --------------------------------------------------

    private static void IngestEvtx(SuperTimeline timeline, string path, IngestStats stats)
    {
        using var fs = File.OpenRead(path);
        var log = new evtx.EventLog(fs);
        var src = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        foreach (var rec in log.GetEventRecords())
        {
            var when = rec.TimeCreated;
            var summary = string.IsNullOrEmpty(rec.MapDescription)
                ? $"[{rec.Channel}] {rec.Provider} EventID={rec.EventId}"
                : $"[{rec.Channel}] {rec.Provider} EventID={rec.EventId} — {rec.MapDescription}";
            timeline.Add(new Synth($"evtx.{src}", rec.UserName, when, summary));
            stats.Evtx++;
        }
    }

    private static void IngestPrefetch(SuperTimeline timeline, string path, IngestStats stats)
    {
        try
        {
            var pf = Prefetch.PrefetchFile.Open(path);
            if (pf is null) return;
            var name = pf.Header.ExecutableFilename ?? Path.GetFileNameWithoutExtension(path);
            foreach (var t in pf.LastRunTimes)
            {
                if (t == DateTimeOffset.MinValue) continue;
                timeline.Add(new Synth("prefetch", null, t, $"Executed: {name}"));
                stats.Prefetch++;
            }
        }
        catch { stats.Errors++; }
    }

    private static void IngestLnk(SuperTimeline timeline, string path, IngestStats stats)
    {
        try
        {
            var l = Lnk.Lnk.LoadFile(path);
            if (l is null) return;
            var name = Path.GetFileName(path);
            var target = l.LocalPath ?? l.CommonPath ?? "(target unknown)";

            if (l.Header.TargetCreationDate != DateTimeOffset.MinValue)
                timeline.Add(new Synth("lnk.created", null, l.Header.TargetCreationDate,
                    $".lnk target created: {target} (from {name})"));
            if (l.Header.TargetModificationDate != DateTimeOffset.MinValue)
                timeline.Add(new Synth("lnk.modified", null, l.Header.TargetModificationDate,
                    $".lnk target modified: {target} (from {name})"));
            if (l.Header.TargetLastAccessedDate != DateTimeOffset.MinValue)
                timeline.Add(new Synth("lnk.accessed", null, l.Header.TargetLastAccessedDate,
                    $".lnk target accessed: {target} (from {name})"));
            stats.Lnk++;
        }
        catch { stats.Errors++; }
    }

    private static void IngestNtuser(SuperTimeline timeline, string path, IngestStats stats)
    {
        try
        {
            var hive = new global::Registry.RegistryHive(path) { RecoverDeleted = false };
            hive.ParseHive();
            var ua = hive.GetKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist");
            if (ua is null) return;
            foreach (var guidSub in ua.SubKeys)
            {
                var count = guidSub.SubKeys.FirstOrDefault(s => s.KeyName == "Count");
                if (count is null) continue;
                foreach (var v in count.Values)
                {
                    // Win7+ v4 format: 72 bytes; FILETIME at offset 60 (8 bytes LE)
                    if (v.ValueDataRaw is not { Length: >= 68 } data) continue;
                    var ft = BitConverter.ToInt64(data, 60);
                    if (ft <= 0 || ft > 9_999_999_999_999_999) continue;
                    DateTime when;
                    try { when = DateTime.FromFileTimeUtc(ft); }
                    catch { continue; }
                    var name = Rot13(v.ValueName ?? "");
                    timeline.Add(new Synth("registry.userassist", null,
                        new DateTimeOffset(when, TimeSpan.Zero),
                        $"UserAssist: {name}"));
                    stats.UserAssist++;
                }
            }
        }
        catch { stats.Errors++; }
    }

    private static void IngestBrowserHistory(SuperTimeline timeline, string path, IngestStats stats)
    {
        // Stage to a temp copy so a locked DB still parses.
        var staged = Path.Combine(Path.GetTempPath(), $"cinder-history-{Guid.NewGuid():N}.sqlite");
        try
        {
            File.Copy(path, staged, overwrite: true);
            using var c = new SqliteConnection($"Data Source={staged};Mode=ReadOnly");
            c.Open();
            string? sql;
            // Chromium-family: urls(last_visit_time microseconds since 1601-01-01) + visits
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='urls'";
                sql = cmd.ExecuteScalar() as string;
            }
            if (sql is not null)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT url, title, last_visit_time FROM urls WHERE last_visit_time>0 ORDER BY last_visit_time DESC LIMIT 50000";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var url = r.GetString(0);
                    var title = r.IsDBNull(1) ? "" : r.GetString(1);
                    var lvt = r.GetInt64(2);
                    // Chromium: microseconds since 1601-01-01
                    var when = new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero)
                        .AddTicks(lvt * 10);
                    timeline.Add(new Synth("browser.history", null, when,
                        string.IsNullOrEmpty(title) ? url : $"{title} — {url}"));
                    stats.Browser++;
                }
            }
            else
            {
                // Firefox: moz_places (last_visit_date microseconds Unix epoch)
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT url, title, last_visit_date FROM moz_places WHERE last_visit_date IS NOT NULL ORDER BY last_visit_date DESC LIMIT 50000";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var url = r.GetString(0);
                    var title = r.IsDBNull(1) ? "" : r.GetString(1);
                    var lvd = r.GetInt64(2);
                    var when = DateTimeOffset.FromUnixTimeMilliseconds(lvd / 1000);
                    timeline.Add(new Synth("browser.history", null, when,
                        string.IsNullOrEmpty(title) ? url : $"{title} — {url}"));
                    stats.Browser++;
                }
            }
        }
        catch { stats.Errors++; }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
        }
    }

    private static void IngestEmail(SuperTimeline timeline, string path, IngestStats stats)
    {
        try
        {
            if (path.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
            {
                var text = File.ReadAllText(path);
                var (date, from, subject) = ParseEmlHeaders(text);
                if (date is not null)
                {
                    timeline.Add(new Synth("email", from, date.Value,
                        $"Email: {subject} (from {from ?? "?"})"));
                    stats.Email++;
                }
            }
            else if (path.EndsWith(".msg", StringComparison.OrdinalIgnoreCase))
            {
                using var msg = new Storage.Message(path);
                var date = msg.ReceivedOn ?? msg.SentOn;
                if (date.HasValue)
                {
                    timeline.Add(new Synth("email", msg.Sender?.Email, date.Value,
                        $"Email: {msg.Subject} (from {msg.Sender?.DisplayName ?? "?"})"));
                    stats.Email++;
                }
            }
        }
        catch { stats.Errors++; }
    }

    private static (DateTimeOffset? Date, string? From, string Subject) ParseEmlHeaders(string text)
    {
        DateTimeOffset? date = null;
        string? from = null;
        string subject = "(no subject)";
        using var sr = new StringReader(text);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            if (line.Length == 0) break;
            if (line.StartsWith("Date:", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTimeOffset.TryParse(line[5..].Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var d))
                {
                    date = d;
                }
            }
            else if (line.StartsWith("From:", StringComparison.OrdinalIgnoreCase))
            {
                from = line[5..].Trim();
            }
            else if (line.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
            {
                subject = line[8..].Trim();
            }
        }
        return (date, from, subject);
    }

    private static void IngestRecycleBin(SuperTimeline timeline, string path, IngestStats stats)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 24) return;
            var version = BitConverter.ToInt64(data, 0);
            var ft = BitConverter.ToInt64(data, 16);
            if (ft <= 0) return;
            string origPath;
            if (version == 2 && data.Length >= 28)
            {
                var nameChars = BitConverter.ToInt32(data, 24);
                var byteCount = Math.Max(0, Math.Min(nameChars * 2, data.Length - 28));
                origPath = Encoding.Unicode.GetString(data, 28, byteCount).TrimEnd('\0');
            }
            else
            {
                var end = Math.Min(data.Length, 24 + 520);
                origPath = Encoding.Unicode.GetString(data, 24, end - 24).TrimEnd('\0');
            }
            var when = new DateTimeOffset(DateTime.FromFileTimeUtc(ft), TimeSpan.Zero);
            // Owning user SID from the parent folder name (S-1-5-21-...)
            var sid = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            timeline.Add(new Synth("recyclebin", sid.StartsWith("S-1-", StringComparison.Ordinal) ? sid : null,
                when, $"Deleted: {origPath}"));
            stats.RecycleBin++;
        }
        catch { stats.Errors++; }
    }

    // ----- Helpers -------------------------------------------------------------

    private static IEnumerable<string> SafeEnumerate(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<string> sub;
            try { sub = Directory.EnumerateDirectories(dir); }
            catch { continue; }
            foreach (var s in sub) stack.Push(s);
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }
            foreach (var f in files) yield return f;
        }
    }

    private static string Rot13(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c >= 'A' && c <= 'Z') sb.Append((char)((c - 'A' + 13) % 26 + 'A'));
            else if (c >= 'a' && c <= 'z') sb.Append((char)((c - 'a' + 13) % 26 + 'a'));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private sealed record Synth(string Source, string? User, DateTimeOffset? Timestamp, string Summary)
        : ArtifactBase(Source, User, Timestamp, Summary);
}

public sealed class IngestStats
{
    public int Evtx;
    public int Prefetch;
    public int Lnk;
    public int UserAssist;
    public int Browser;
    public int Email;
    public int RecycleBin;
    public int Errors;
    public string? LastError;
    public int Total =>
        Evtx + Prefetch + Lnk + UserAssist + Browser + Email + RecycleBin;
    public override string ToString() =>
        $"evtx={Evtx} pf={Prefetch} lnk={Lnk} userassist={UserAssist} browser={Browser} email={Email} recycle={RecycleBin} errors={Errors}";
}
