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
//   email         — .eml/.msg/.mbox: Date: header (Received/Sent for .msg), one event per message
//   recyclebin    — $Recycle.Bin\<SID>\$I*: deletion timestamp + original path
//   usn / logfile — $UsnJrnl$J change records; $LogFile name events at $FILE_NAME creation
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
                        IngestEvtx(timeline, file, stats, ct);
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
                        IngestBrowserHistory(timeline, file, stats, ct);
                        progress?.Report($"browser: {file}");
                    }
                    else if (ext == ".eml" || ext == ".msg" || ext == ".mbox")
                    {
                        IngestEmail(timeline, file, stats, ct);
                        if (ext == ".mbox")
                        {
                            progress?.Report($"mbox: {file}");
                        }
                    }
                    else if (lower.StartsWith("$i", StringComparison.Ordinal) &&
                             file.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
                    {
                        IngestRecycleBin(timeline, file, stats);
                    }
                    else if (lower == "$j" || lower.EndsWith("$usnjrnl$j", StringComparison.Ordinal) || lower == "$usnjrnl:$j")
                    {
                        // KAPE and most triage tools export the change journal as $Extend\$UsnJrnl$J.
                        IngestUsnJournal(timeline, file, stats, ct);
                        progress?.Report($"usn: {file}");
                    }
                    else if (lower == "$logfile")
                    {
                        IngestLogFile(timeline, file, stats, ct);
                        progress?.Report($"logfile: {file}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    stats.Fail(file, ex);
                }
            }
            timeline.Sort();
        }, ct);
        return stats;
    }

    // ----- Per-source parsers --------------------------------------------------

    private static void IngestEvtx(SuperTimeline timeline, string path, IngestStats stats, CancellationToken ct)
    {
        using var fs = File.OpenRead(path);
        var log = new evtx.EventLog(fs);
        var src = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        var n = 0;
        foreach (var rec in log.GetEventRecords())
        {
            if ((++n & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();   // a Security.evtx holds millions of records
            }
            var when = rec.TimeCreated;
            var summary = string.IsNullOrEmpty(rec.MapDescription)
                ? $"[{rec.Channel}] {rec.Provider} EventID={rec.EventId}"
                : $"[{rec.Channel}] {rec.Provider} EventID={rec.EventId} — {rec.MapDescription}";
            timeline.Add(new Synth($"evtx.{src}", rec.UserName, when, summary));
            stats.Evtx++;
        }
    }

    /// <summary>
    /// Every change-journal record becomes an event. Reason flags are kept in the summary so
    /// "FILE_CREATE" and "FILE_DELETE" are filterable text, and the MFT reference ties the
    /// event to a record even after the file is gone.
    /// </summary>
    private static void IngestUsnJournal(SuperTimeline timeline, string path, IngestStats stats, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            var n = 0;
            // The token goes into the parser: a multi-gigabyte $J must stop when asked, not
            // when its last record has been read.
            foreach (var r in Cinder.Filesystems.UsnJournal.Parse(fs, ct))
            {
                if (r.Timestamp == DateTimeOffset.MinValue) continue;
                timeline.Add(new Synth("ntfs.usn", null, r.Timestamp,
                    $"{r.ReasonText}: {r.FileName} (MFT {r.MftIndex}-{r.MftSequence}, parent {r.ParentMftIndex})"));
                stats.Usn++;
                if (++n >= 2_000_000) break;   // a rolled-over journal on a busy volume can be larger than memory
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { stats.Fail(path, ex); }
    }

    /// <summary>
    /// $LogFile records carry no time of their own; the only clocks in them are the $FILE_NAME
    /// timestamps inside file-record and index-entry payloads. A record that initialises a
    /// file record or adds a directory entry therefore yields one event at that name's
    /// creation time — "this name existed, with this MFT record, and was created then" — which
    /// is the one $LogFile fact that outlives both the MFT record and the USN journal. Stale
    /// (circular-slack) records are included and labelled, since they are why the log is read.
    /// </summary>
    private static void IngestLogFile(SuperTimeline timeline, string path, IngestStats stats, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            var n = 0;
            foreach (var r in Cinder.Filesystems.NtfsLogFile.Parse(fs, ct: ct))
            {
                if (r.FileName is null || r.Created is null) continue;
                if (r.RedoOp is not (0x02 or 0x0C or 0x0E) && r.UndoOp is not (0x0C or 0x0E)) continue;
                var verb = r.RedoOp switch
                {
                    0x02 => "file record initialised",
                    0x0C or 0x0E => "directory entry added",
                    _ => "directory entry removed",
                };
                var mft = r.MftRecordNumber is { } m ? $" (MFT {m}, parent {r.ParentMftRecord})" : "";
                var stale = r.IsStale ? " [stale slack]" : "";
                timeline.Add(new Synth("ntfs.logfile", null, r.Created.Value,
                    $"{verb}: {r.FileName}{mft}; $FILE_NAME created {r.Created:u}, LSN {r.Lsn}{stale}"));
                stats.LogFile++;
                if (++n >= 2_000_000) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { stats.Fail(path, ex); }
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
        catch (Exception ex) when (ex is not OperationCanceledException) { stats.Fail(path, ex); }
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
        catch (Exception ex) when (ex is not OperationCanceledException) { stats.Fail(path, ex); }
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
                    if (UserAssistLastRun(v.ValueDataRaw) is not { } when) continue;
                    var name = Rot13(v.ValueName ?? "");
                    timeline.Add(new Synth("registry.userassist", null, when,
                        $"UserAssist: {name}"));
                    stats.UserAssist++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { stats.Fail(path, ex); }
    }

    /// <summary>
    /// Reads URL visits from a Chromium-family <c>History</c> or a Firefox <c>places.sqlite</c>.
    ///
    /// <para>The database is copied to a private staging directory first, together with its
    /// write-ahead log: a browser that was running (or was killed) leaves its most recent
    /// visits in <c>-wal</c>, and reading the main file alone silently drops exactly the
    /// activity closest to the incident. SQLite never opens the evidence file itself. The
    /// staging directory holds a copy of evidence, so it is removed before this returns — which
    /// is why the connection is unpooled: a pooled connection keeps the file open after
    /// Dispose and the delete fails.</para>
    /// </summary>
    internal static void IngestBrowserHistory(
        SuperTimeline timeline, string path, IngestStats stats, CancellationToken ct, string? stagingRoot = null)
    {
        var stagingDir = Path.Combine(stagingRoot ?? Path.GetTempPath(), $"cinder-history-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingDir);
            var staged = Path.Combine(stagingDir, "history.sqlite");
            File.Copy(path, staged, overwrite: true);
            if (File.Exists(path + "-wal"))
            {
                try
                {
                    File.Copy(path + "-wal", staged + "-wal", overwrite: true);
                    // The shared-memory index travels with the log when it can be read. It is
                    // derived from the log (SQLite rebuilds it on open), so a live browser
                    // holding it locked is no reason to give up on the log itself.
                    if (File.Exists(path + "-shm"))
                    {
                        try
                        {
                            File.Copy(path + "-shm", staged + "-shm", overwrite: true);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            File.Delete(staged + "-shm");
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    File.Delete(staged + "-wal");
                    stats.Skip($"{Path.GetFileName(path)}: its write-ahead log could not be copied ({ex.Message}); the most recent visits may be missing");
                }
            }

            // Read-write on the copy (never on the evidence) so SQLite can replay the WAL.
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = staged,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString();
            using var c = new SqliteConnection(connectionString);
            c.Open();
            string? sql;
            // Chromium-family: urls(last_visit_time microseconds since 1601-01-01) + visits
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='urls'";
                sql = cmd.ExecuteScalar() as string;
            }
            using var query = c.CreateCommand();
            query.CommandText = sql is not null
                ? "SELECT url, title, last_visit_time FROM urls WHERE last_visit_time>0 ORDER BY last_visit_time DESC LIMIT 50000"
                // Firefox: moz_places (last_visit_date microseconds Unix epoch)
                : "SELECT url, title, last_visit_date FROM moz_places WHERE last_visit_date IS NOT NULL ORDER BY last_visit_date DESC LIMIT 50000";
            using var r = query.ExecuteReader();
            var n = 0;
            while (r.Read())
            {
                if ((++n & 0x3FF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }
                var url = r.IsDBNull(0) ? "" : r.GetString(0);
                var title = r.IsDBNull(1) ? "" : r.GetString(1);
                var raw = r.GetInt64(2);
                // One row with a nonsense timestamp must cost one row, not the rest of the file.
                var when = sql is not null ? FromWebKitMicroseconds(raw) : FromUnixMicroseconds(raw);
                if (when is null)
                {
                    stats.Skip($"{Path.GetFileName(path)}: visit to {url} has an out-of-range timestamp ({raw})");
                    continue;
                }
                timeline.Add(new Synth("browser.history", null, when.Value,
                    string.IsNullOrEmpty(title) ? url : $"{title} — {url}"));
                stats.Browser++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { stats.Fail(path, ex); }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDir))
                {
                    Directory.Delete(stagingDir, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A copy of evidence left behind is worth a line in the summary, not silence.
                stats.Fail(stagingDir, new IOException("staged browser database could not be removed: " + ex.Message));
            }
        }
    }

    /// <summary>How much of an .eml is read to find its headers. Bodies and attachments are not loaded.</summary>
    private const int MaxHeaderChars = 1 << 18;

    /// <summary>No single mailbox contributes more than this many events.</summary>
    private const int MaxMessagesPerMailbox = 2_000_000;

    private static void IngestEmail(SuperTimeline timeline, string path, IngestStats stats, CancellationToken ct)
    {
        try
        {
            if (path.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
            {
                // A triage folder can hold a hundred thousand .eml files; rent the block
                // rather than allocate one per message.
                Dictionary<string, string> headers;
                var block = System.Buffers.ArrayPool<char>.Shared.Rent(MaxHeaderChars);
                try
                {
                    int read;
                    using (var reader = new StreamReader(path))
                    {
                        read = reader.ReadBlock(block, 0, MaxHeaderChars);
                    }
                    headers = ReadHeaders(new StringReader(new string(block, 0, read)));
                }
                finally
                {
                    System.Buffers.ArrayPool<char>.Shared.Return(block);
                }
                AddMessage(timeline, stats, path,
                    headers.GetValueOrDefault("Date"), headers.GetValueOrDefault("From"), headers.GetValueOrDefault("Subject"));
            }
            else if (path.EndsWith(".mbox", StringComparison.OrdinalIgnoreCase))
            {
                // One event per message, streamed: an mbox is routinely gigabytes.
                using var reader = new StreamReader(path);
                var n = 0;
                foreach (var headers in Cinder.App.ViewModels.Tools.MboxHeaderReader.ReadMessages(reader, ct))
                {
                    AddMessage(timeline, stats, path,
                        headers.GetValueOrDefault("Date"), headers.GetValueOrDefault("From"), headers.GetValueOrDefault("Subject"));
                    if (++n >= MaxMessagesPerMailbox)
                    {
                        stats.Skip($"{Path.GetFileName(path)}: stopped after {MaxMessagesPerMailbox:N0} messages");
                        break;
                    }
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
                else
                {
                    stats.Skip($"{Path.GetFileName(path)}: message has no sent or received date");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { stats.Fail(path, ex); }
    }

    /// <summary>Adds one message to the timeline, or counts it as skipped when it cannot be placed in time.</summary>
    private static void AddMessage(SuperTimeline timeline, IngestStats stats, string path, string? date, string? from, string? subject)
    {
        if (ParseMailDate(date) is not { } when)
        {
            stats.Skip(string.IsNullOrWhiteSpace(date)
                ? $"{Path.GetFileName(path)}: message has no Date header"
                : $"{Path.GetFileName(path)}: unreadable Date header \"{date}\"");
            return;
        }
        timeline.Add(new Synth("email", from, when,
            $"Email: {(string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject)} (from {from ?? "?"})"));
        stats.Email++;
    }

    /// <summary>
    /// Reads one RFC 5322 header block (up to the first blank line). A line starting with a
    /// space or tab continues the header before it; the first occurrence of a header wins.
    /// </summary>
    internal static Dictionary<string, string> ReadHeaders(TextReader reader)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? last = null;
        string? line;
        while ((line = reader.ReadLine()) is not null && line.Length > 0)
        {
            if (line[0] is ' ' or '\t')
            {
                if (last is not null)
                {
                    headers[last] = (headers[last] + " " + line.Trim()).Trim();
                }
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                last = null;
                continue;
            }
            var name = line[..colon].Trim();
            if (headers.TryAdd(name, line[(colon + 1)..].Trim()))
            {
                last = name;
            }
            else
            {
                last = null;   // a repeated header: keep the first, and its continuation lines with it
            }
        }
        return headers;
    }

    /// <summary>Zone names RFC 5322 still allows in a Date header (obs-zone) that .NET does not know.</summary>
    private static readonly Dictionary<string, string> ObsoleteZones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UT"] = "+0000", ["UTC"] = "+0000", ["Z"] = "+0000",
        ["EST"] = "-0500", ["EDT"] = "-0400",
        ["CST"] = "-0600", ["CDT"] = "-0500",
        ["MST"] = "-0700", ["MDT"] = "-0600",
        ["PST"] = "-0800", ["PDT"] = "-0700",
    };

    /// <summary>
    /// Parses a mail <c>Date:</c> value. Handles what real headers contain and
    /// <see cref="DateTimeOffset.TryParse(string, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/>
    /// alone does not: a trailing comment — <c>Tue, 3 Jun 2025 10:00:00 +0000 (UTC)</c> is what
    /// most mail servers write — and the obsolete named zones.
    /// </summary>
    internal static DateTimeOffset? ParseMailDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Comments are parenthesised and may nest; they carry no information the offset lacks.
        var sb = new StringBuilder(value.Length);
        var depth = 0;
        foreach (var c in value)
        {
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0)
            {
                sb.Append(c);
            }
        }
        var text = string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            return null;
        }

        const DateTimeStyles Styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, Styles, out var parsed))
        {
            return parsed;
        }
        var lastSpace = text.LastIndexOf(' ');
        if (lastSpace > 0 && ObsoleteZones.TryGetValue(text[(lastSpace + 1)..], out var offset) &&
            DateTimeOffset.TryParse(text[..lastSpace] + " " + offset, CultureInfo.InvariantCulture, Styles, out parsed))
        {
            return parsed;
        }
        return null;
    }

    private static void IngestRecycleBin(SuperTimeline timeline, string path, IngestStats stats)
    {
        try
        {
            // $I files are a few hundred bytes. Anything large with that name is not one, and
            // is not read into memory to find that out.
            if (new FileInfo(path).Length > 64 * 1024)
            {
                stats.Skip($"{Path.GetFileName(path)}: too large to be a Recycle Bin index record");
                return;
            }
            var data = File.ReadAllBytes(path);
            if (data.Length < 24) return;
            var version = BitConverter.ToInt64(data, 0);
            if (FromFileTime(BitConverter.ToInt64(data, 16)) is not { } when)
            {
                stats.Skip($"{Path.GetFileName(path)}: deletion time is out of range");
                return;
            }
            string origPath;
            if (version == 2 && data.Length >= 28)
            {
                var nameChars = BitConverter.ToInt32(data, 24);
                var byteCount = (int)Math.Clamp((long)nameChars * 2, 0, data.Length - 28);
                origPath = Encoding.Unicode.GetString(data, 28, byteCount).TrimEnd('\0');
            }
            else
            {
                var end = Math.Min(data.Length, 24 + 520);
                origPath = Encoding.Unicode.GetString(data, 24, end - 24).TrimEnd('\0');
            }
            // Owning user SID from the parent folder name (S-1-5-21-...)
            var sid = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            timeline.Add(new Synth("recyclebin", sid.StartsWith("S-1-", StringComparison.Ordinal) ? sid : null,
                when, $"Deleted: {origPath}"));
            stats.RecycleBin++;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { stats.Fail(path, ex); }
    }

    // ----- Timestamps ----------------------------------------------------------

    /// <summary>The largest FILETIME <see cref="DateTime"/> can represent (9999-12-31).</summary>
    private const long MaxFileTime = 2_650_467_743_999_999_999L;

    /// <summary>A Windows FILETIME (100 ns ticks since 1601-01-01 UTC), or null when it is zero ("never") or out of range.</summary>
    internal static DateTimeOffset? FromFileTime(long fileTime) =>
        fileTime is <= 0 or > MaxFileTime
            ? null
            : new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero);

    /// <summary>Chromium / WebKit time: microseconds since 1601-01-01 UTC.</summary>
    internal static DateTimeOffset? FromWebKitMicroseconds(long microseconds) =>
        microseconds is <= 0 or > MaxFileTime / 10 ? null : FromFileTime(microseconds * 10);

    /// <summary>Firefox PRTime: microseconds since the Unix epoch.</summary>
    internal static DateTimeOffset? FromUnixMicroseconds(long microseconds)
    {
        var milliseconds = microseconds / 1000;
        return milliseconds is < -62_135_596_800_000 or > 253_402_300_799_999
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    /// <summary>
    /// Last-run time from a UserAssist <c>Count</c> value (Windows 7 and later: 72 bytes, with
    /// the FILETIME at offset 60). Null when the entry has never been run or the data is short.
    ///
    /// <para>The only bounds are "not zero" and "representable". An earlier upper bound of
    /// 9,999,999,999,999,999 sat a factor of ten below every FILETIME since 1632 — today's are
    /// about 1.3 × 10^17 — so it rejected every real entry and this source produced nothing.</para>
    /// </summary>
    internal static DateTimeOffset? UserAssistLastRun(byte[]? data) =>
        data is { Length: >= 68 } ? FromFileTime(BitConverter.ToInt64(data, 60)) : null;

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
    public int Usn;
    public int LogFile;
    public int Errors;
    public string? LastError;

    /// <summary>
    /// Records that were read but could not be put on the timeline — a message with no usable
    /// date, a visit with an impossible timestamp, a mailbox cut off at the cap. They are not
    /// errors (the file parsed), but the examiner must be told the timeline is short by this many.
    /// </summary>
    public int Skipped;
    public string? LastSkipReason;

    public int Total =>
        Evtx + Prefetch + Lnk + UserAssist + Browser + Email + RecycleBin + Usn + LogFile;

    internal void Skip(string reason)
    {
        Skipped++;
        LastSkipReason = reason;
    }

    internal void Fail(string path, Exception ex)
    {
        Errors++;
        LastError = $"{Path.GetFileName(path)}: {ex.Message}";
    }

    public override string ToString()
    {
        var text = $"evtx={Evtx} pf={Prefetch} lnk={Lnk} userassist={UserAssist} browser={Browser} email={Email} recycle={RecycleBin} usn={Usn} logfile={LogFile} errors={Errors} skipped={Skipped}";
        if (Skipped > 0 && LastSkipReason is not null)
        {
            text += $" (last skipped: {LastSkipReason})";
        }
        if (Errors > 0 && LastError is not null)
        {
            text += $" (last error: {LastError})";
        }
        return text;
    }
}
