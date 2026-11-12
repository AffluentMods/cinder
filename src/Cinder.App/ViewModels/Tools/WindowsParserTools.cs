// =====================================================================================
// Windows artifact parsers: registry hives, event logs, shortcuts, prefetch, jump lists,
// browser history, and the registry-derived views (USB, networks, Amcache, ShimCache), plus
// mail files. Each tool parses on the thread pool and returns its rows to
// SidecarToolViewModel, which owns the grid.
//
// Two rules every parser here follows:
//   * A wrong input (the SOFTWARE hive handed to USB history) or a failed parse is thrown, so
//     the examiner sees an error banner. It must never look like an artifact with no entries.
//   * Cancellation propagates. No catch-all may swallow OperationCanceledException.
// =====================================================================================

using System.Globalization;
using Cinder.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Cinder.App.ViewModels.Tools;

/// <summary>Helpers shared by the registry-backed tools.</summary>
internal static class HiveReader
{
    /// <summary>Opens and parses a hive, turning "this is not a hive" into a sentence an examiner can act on.</summary>
    public static global::Registry.RegistryHive Open(string path, bool recoverDeleted = false)
    {
        try
        {
            var hive = new global::Registry.RegistryHive(path) { RecoverDeleted = recoverDeleted };
            hive.ParseHive();
            return hive;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The library repeats the full path in its message; the file is already named.
            var reason = ex.Message.Replace(path, "the file", StringComparison.OrdinalIgnoreCase);
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} could not be read as a Windows registry hive: {reason}", ex);
        }
    }

    public static string Value(global::Registry.Abstractions.RegistryKey key, string name) =>
        key.Values.FirstOrDefault(v => string.Equals(v.ValueName, name, StringComparison.OrdinalIgnoreCase))?.ValueData ?? "";

    public static byte[]? RawValue(global::Registry.Abstractions.RegistryKey key, string name) =>
        key.Values.FirstOrDefault(v => string.Equals(v.ValueName, name, StringComparison.OrdinalIgnoreCase))?.ValueDataRaw;

    public static string LastWrite(global::Registry.Abstractions.RegistryKey key) =>
        key.LastWriteTime?.ToString("u", CultureInfo.InvariantCulture) ?? "";

    /// <summary>The ControlSet00N keys of a SYSTEM hive, in name order. Empty for any other hive.</summary>
    public static List<global::Registry.Abstractions.RegistryKey> ControlSets(global::Registry.RegistryHive hive)
    {
        var sets = new List<global::Registry.Abstractions.RegistryKey>();
        foreach (var name in hive.Root.SubKeys
                     .Select(k => k.KeyName)
                     .Where(n => n.StartsWith("ControlSet", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (hive.GetKey(name) is { } key)
            {
                sets.Add(key);
            }
        }
        return sets;
    }
}

/// <summary>
/// A private copy of a SQLite database, with its write-ahead log when there is one, in a
/// temporary folder. Parsers read the copy so a file locked by a running browser still opens,
/// so nothing is ever written next to the evidence, and so rows that are still only in the
/// <c>-wal</c> file (the most recent activity) are not missed.
/// </summary>
internal sealed class StagedDatabase : IDisposable
{
    private readonly string _folder;

    public string Path { get; }

    public StagedDatabase(string sourcePath)
    {
        _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cinder-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        Path = System.IO.Path.Combine(_folder, "copy.sqlite");
        try
        {
            File.Copy(sourcePath, Path, overwrite: true);
            foreach (var suffix in new[] { "-wal" })
            {
                if (File.Exists(sourcePath + suffix))
                {
                    File.Copy(sourcePath + suffix, Path + suffix, overwrite: true);
                }
            }
            foreach (var f in Directory.EnumerateFiles(_folder))
            {
                // Copies inherit read-only from write-protected evidence; clear it so SQLite can
                // replay the log into the copy and so the copy can be deleted afterwards.
                File.SetAttributes(f, FileAttributes.Normal);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Connection string for the copy. Not pooled, so the file is released on dispose.</summary>
    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = Path,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false,
    }.ToString();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                foreach (var f in Directory.EnumerateFiles(_folder))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch
        {
            // A temp copy that cannot be removed is not worth failing a parse over.
        }
    }
}

// ===================== Registry =====================

public sealed partial class RegistryTool
{
    /// <summary>Rows the grid will materialize before it stops.</summary>
    private const int RowBudget = 25_000;

    /// <summary>Key nesting depth walked. Deeper keys exist but are not enumerated.</summary>
    private const int DepthBudget = 8;

    protected override async Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct)
    {
        // Registry parsing is CPU-bound on big hives (50–500 MB SOFTWARE), so it runs on the pool.
        var (rows, truncated) = await Task.Run(() => Parse(evidencePath, ct), ct);
        return new ParseResult(rows, truncated);
    }

    private static (List<object> Rows, bool Truncated) Parse(string path, CancellationToken ct)
    {
        var hive = HiveReader.Open(path);
        var rows = new List<object>(capacity: 4096);
        var truncated = false;
        Walk(hive.Root, rows, ct, DepthBudget, RowBudget, ref truncated);
        return (rows, truncated);
    }

    private static void Walk(global::Registry.Abstractions.RegistryKey? key,
                             List<object> rows, CancellationToken ct,
                             int depthBudget, int rowBudget, ref bool truncated)
    {
        if (key is null)
        {
            return;
        }
        if (depthBudget < 0)
        {
            // Keys below the depth limit exist but were not walked — still a partial view.
            truncated = true;
            return;
        }
        if (rows.Count >= rowBudget)
        {
            truncated = true;
            return;
        }
        ct.ThrowIfCancellationRequested();

        // Emit a row per value at this key.
        foreach (var v in key.Values)
        {
            if (rows.Count >= rowBudget)
            {
                truncated = true;
                return;
            }
            rows.Add(new
            {
                Key = key.KeyPath,
                Name = string.IsNullOrEmpty(v.ValueName) ? "(default)" : v.ValueName,
                Type = v.ValueType,
                Value = TruncateValue(v.ValueData),
                KeyLastWrite = HiveReader.LastWrite(key),
            });
        }

        foreach (var child in key.SubKeys)
        {
            Walk(child, rows, ct, depthBudget - 1, rowBudget, ref truncated);
        }
    }

    private static string TruncateValue(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= 256 ? s : s[..256] + "…";
    }
}

// ===================== Event Log (.evtx) =====================

public sealed partial class EventLogTool
{
    private const int RowBudget = 100_000;

    protected override async Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        return ParseResult.WithBudget(rows, RowBudget);
    }

    private static List<object> Parse(string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var log = new evtx.EventLog(fs);
        var rows = new List<object>(capacity: 8192);
        foreach (var rec in log.GetEventRecords())
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= RowBudget) break;
            rows.Add(new
            {
                Time = rec.TimeCreated.ToString("u", CultureInfo.InvariantCulture),
                Channel = rec.Channel ?? "",
                Provider = rec.Provider ?? "",
                EventId = rec.EventId,
                Level = rec.Level ?? "",
                Computer = rec.Computer ?? "",
                User = rec.UserName ?? rec.UserId ?? "",
                Description = rec.MapDescription ?? "",
                RecordId = rec.EventRecordId,
            });
        }
        return rows;
    }
}

/// <summary>
/// The shape shared by the tools that read either one file or every matching file in a folder
/// (Prefetch, LNK, Jump lists). A single file that cannot be parsed is an error. In a folder,
/// one bad file must not hide the good ones, so it becomes a row with a note and is counted.
/// </summary>
internal static class FolderScan
{
    public static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    public static readonly EnumerationOptions TopLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    /// <summary>The status note for a folder scan: null when every file parsed.</summary>
    public static string? Note(int files, int failed) => failed == 0
        ? null
        : $"Read {files - failed:N0} of {files:N0} files. {failed:N0} could not be parsed; each has a row with the reason in its Note column.";

    /// <summary>A path as shown in the grid: relative to the folder the examiner picked.</summary>
    public static string Display(string root, string file) => Path.GetRelativePath(root, file);
}

// ===================== Prefetch (.pf) =====================

public sealed partial class PrefetchTool
{
    private const int RowBudget = 5_000;

    public override bool AcceptsFolder => true;

    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
        Task.Run(() => Parse(evidencePath, ct), ct);

    private static ParseResult Parse(string path, CancellationToken ct)
    {
        var rows = new List<object>();
        if (!Directory.Exists(path))
        {
            rows.Add(ParseOne(path, Path.GetFileName(path)));
            return new ParseResult(rows);
        }

        int files = 0, failed = 0;
        foreach (var pf in Directory.EnumerateFiles(path, "*.pf", FolderScan.TopLevel))
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= RowBudget) break;
            files++;
            try
            {
                rows.Add(ParseOne(pf, FolderScan.Display(path, pf)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                rows.Add(Row(FolderScan.Display(path, pf), "", 0, "", "", 0, 0, $"Could not parse: {ex.Message}"));
            }
        }
        if (files == 0)
        {
            throw new InvalidOperationException("This folder has no .pf files. Pick the Prefetch folder (Windows\\Prefetch) or a single .pf file.");
        }
        return ParseResult.WithBudget(rows, RowBudget, FolderScan.Note(files, failed));
    }

    private static object ParseOne(string path, string display)
    {
        var pf = Prefetch.PrefetchFile.Open(path);
        // Run times are most recent first.
        var runTimes = pf.LastRunTimes
            .Select(t => t.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture))
            .ToArray();
        return Row(
            display,
            pf.Header?.ExecutableFilename ?? Path.GetFileNameWithoutExtension(path),
            Convert.ToInt32(pf.RunCount, CultureInfo.InvariantCulture),
            runTimes.Length > 0 ? runTimes[0] : "",
            string.Join(", ", runTimes),
            pf.Filenames?.Count ?? 0,
            Convert.ToInt32(pf.TotalDirectoryCount, CultureInfo.InvariantCulture),
            "");
    }

    private static object Row(string file, string executable, int runCount, string lastRun, string allRuns,
                              int filesLoaded, int directories, string note) => new
                              {
                                  File = file,
                                  Executable = executable,
                                  RunCount = runCount,
                                  LastRun = lastRun,
                                  AllRuns = allRuns,
                                  FilesLoaded = filesLoaded,
                                  Directories = directories,
                                  Note = note,
                              };
}

// ===================== LNK shortcuts (.lnk) =====================

public sealed partial class LnkTool
{
    private const int RowBudget = 10_000;

    public override bool AcceptsFolder => true;

    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
        Task.Run(() => Parse(evidencePath, ct), ct);

    private static ParseResult Parse(string path, CancellationToken ct)
    {
        var rows = new List<object>();
        if (!Directory.Exists(path))
        {
            rows.Add(ParseOne(path, Path.GetFileName(path)));
            return new ParseResult(rows);
        }

        int files = 0, failed = 0;
        foreach (var lnk in Directory.EnumerateFiles(path, "*.lnk", FolderScan.Recursive))
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= RowBudget) break;
            files++;
            try
            {
                rows.Add(ParseOne(lnk, FolderScan.Display(path, lnk)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                rows.Add(Row(FolderScan.Display(path, lnk), "", "", "", "", "", "", "", "", "", $"Could not parse: {ex.Message}"));
            }
        }
        if (files == 0)
        {
            throw new InvalidOperationException("This folder has no .lnk files in it or in its subfolders.");
        }
        return ParseResult.WithBudget(rows, RowBudget, FolderScan.Note(files, failed));
    }

    private static object ParseOne(string path, string display)
    {
        var bytes = File.ReadAllBytes(path);
        var lnk = new Lnk.LnkFile(bytes, path, codepage: 1252);
        // The tracker block is where the machine name and MAC address live. Not every shortcut has one.
        var tracker = lnk.ExtraBlocks?.OfType<Lnk.ExtraData.TrackerDataBaseBlock>().FirstOrDefault();
        return Row(
            display,
            lnk.LocalPath ?? lnk.NetworkShareInfo?.NetworkShareName ?? "",
            lnk.Arguments ?? "",
            lnk.WorkingDirectory ?? "",
            // The target's own timestamps as recorded in the shortcut header, not the .lnk file's.
            HeaderTime(lnk.Header?.TargetCreationDate),
            HeaderTime(lnk.Header?.TargetModificationDate),
            HeaderTime(lnk.Header?.TargetLastAccessedDate),
            lnk.VolumeInfo?.VolumeSerialNumber ?? "",
            tracker?.MachineId ?? "",
            tracker?.MacAddress ?? "",
            "");
    }

    /// <summary>A shortcut header time as UTC text. An unset FILETIME (1601) is blank, not a date.</summary>
    internal static string HeaderTime(DateTimeOffset? t) =>
        t is { Year: > 1601 } v ? v.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture) : "";

    private static object Row(string source, string target, string args, string workingDir,
                              string created, string modified, string accessed,
                              string volumeSerial, string machineId, string mac, string note) => new
                              {
                                  Source = source,
                                  Target = target,
                                  Args = args,
                                  WorkingDir = workingDir,
                                  TargetCreated = created,
                                  TargetModified = modified,
                                  TargetAccessed = accessed,
                                  VolumeSerial = volumeSerial,
                                  MachineId = machineId,
                                  MacAddress = mac,
                                  Note = note,
                              };
}

// ===================== Jump lists =====================

public sealed partial class JumplistsTool
{
    private const int RowBudget = 20_000;

    public override bool AcceptsFolder => true;

    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
        Task.Run(() => Parse(evidencePath, ct), ct);

    private static bool IsJumpList(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".automaticDestinations-ms", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".customDestinations-ms", StringComparison.OrdinalIgnoreCase);
    }

    private static ParseResult Parse(string path, CancellationToken ct)
    {
        var rows = new List<object>();
        if (!Directory.Exists(path))
        {
            if (!IsJumpList(path))
            {
                throw new InvalidOperationException(
                    "Jump lists reads .automaticDestinations-ms and .customDestinations-ms files. This file is neither.");
            }
            ParseOne(path, Path.GetFileName(path), rows);
            return ParseResult.WithBudget(rows, RowBudget);
        }

        int files = 0, failed = 0;
        foreach (var f in Directory.EnumerateFiles(path, "*", FolderScan.Recursive).Where(IsJumpList))
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= RowBudget) break;
            files++;
            try
            {
                ParseOne(f, FolderScan.Display(path, f), rows);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                rows.Add(Row(Path.GetExtension(f).TrimStart('.'), FolderScan.Display(path, f), "", "", "", "", "", "", false, "", $"Could not parse: {ex.Message}"));
            }
        }
        if (files == 0)
        {
            throw new InvalidOperationException(
                "This folder has no jump list files. Pick a Recent\\AutomaticDestinations or Recent\\CustomDestinations folder.");
        }
        return ParseResult.WithBudget(rows, RowBudget, FolderScan.Note(files, failed));
    }

    private static void ParseOne(string path, string display, List<object> rows)
    {
        var bytes = File.ReadAllBytes(path);
        if (Path.GetExtension(path).Equals(".automaticDestinations-ms", StringComparison.OrdinalIgnoreCase))
        {
            var jl = new JumpList.Automatic.AutomaticDestination(bytes, path, codepage: 1252);
            foreach (var e in jl.DestListEntries)
            {
                rows.Add(Row("automatic", display, jl.AppId?.AppId ?? "", jl.AppId?.Description ?? "",
                    e.Path ?? "", e.Hostname ?? "",
                    e.LastModified.ToString("u", CultureInfo.InvariantCulture),
                    e.CreatedOn.ToString("u", CultureInfo.InvariantCulture),
                    e.Pinned, e.MacAddress ?? "", ""));
            }
        }
        else
        {
            var jl = new JumpList.Custom.CustomDestination(bytes, path, codepage: 1252);
            foreach (var e in jl.Entries)
            {
                foreach (var lnk in e.LnkFiles ?? [])
                {
                    rows.Add(Row("custom", display, jl.AppId?.AppId ?? "", jl.AppId?.Description ?? "",
                        lnk.LocalPath ?? "", "",
                        LnkTool.HeaderTime(lnk.Header?.TargetModificationDate),
                        LnkTool.HeaderTime(lnk.Header?.TargetCreationDate),
                        false, "", ""));
                }
            }
        }
    }

    private static object Row(string kind, string source, string appId, string appName, string path, string hostname,
                              string lastModified, string createdOn, bool pinned, string mac, string note) => new
                              {
                                  Kind = kind,
                                  Source = source,
                                  AppId = appId,
                                  AppName = appName,
                                  Path = path,
                                  Hostname = hostname,
                                  LastModified = lastModified,
                                  CreatedOn = createdOn,
                                  Pinned = pinned,
                                  MacAddress = mac,
                                  Note = note,
                              };
}

// ===================== Browser history =====================

public sealed partial class BrowserHistoryTool
{
    /// <summary>Most rows shown across every database found.</summary>
    internal const int RowBudget = 100_000;

    /// <summary>Most rows read from one history database.</summary>
    internal const int PerDatabaseLimit = 50_000;

    public override bool AcceptsFolder => true;

    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
        Task.Run(() => Parse(evidencePath, ct), ct);

    /// <summary>
    /// Reads every history database under <paramref name="path"/> (or the one file given).
    /// The result is flagged truncated when any single database holds more URLs than
    /// <paramref name="perDatabaseLimit"/> or the total reaches <paramref name="budget"/>: either
    /// way the grid is a prefix, and must say so.
    /// </summary>
    internal static ParseResult Parse(string path, CancellationToken ct,
                                      int perDatabaseLimit = PerDatabaseLimit, int budget = RowBudget)
    {
        // Accept either a directory (browser profile, or the folder above several profiles) or a
        // History / places.sqlite file directly.
        var databases = new List<(string Path, string Display, string Browser)>();
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFiles(path, "History", FolderScan.Recursive))
            {
                databases.Add((f, FolderScan.Display(path, f), GuessBrowser(f)));
            }
            foreach (var f in Directory.EnumerateFiles(path, "places.sqlite", FolderScan.Recursive))
            {
                databases.Add((f, FolderScan.Display(path, f), "Firefox"));
            }
            if (databases.Count == 0)
            {
                throw new InvalidOperationException(
                    "No browser history database was found in this folder or its subfolders. " +
                    "Chrome, Edge, Brave and Opera keep it in a file named History; Firefox uses places.sqlite.");
            }
        }
        else
        {
            var isFirefox = Path.GetFileName(path).Equals("places.sqlite", StringComparison.OrdinalIgnoreCase);
            // Keep the profile folder in the name: "Default\History" says more than "History".
            var parent = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "");
            var display = string.IsNullOrEmpty(parent) ? Path.GetFileName(path) : Path.Combine(parent, Path.GetFileName(path));
            databases.Add((path, display, isFirefox ? "Firefox" : GuessBrowser(path)));
        }

        var rows = new List<object>();
        var truncated = false;
        var failures = new List<string>();
        foreach (var (db, display, browser) in databases)
        {
            ct.ThrowIfCancellationRequested();
            var room = budget - rows.Count;
            if (room <= 0)
            {
                truncated = true;
                break;
            }
            try
            {
                using var staged = new StagedDatabase(db);
                var limit = Math.Min(perDatabaseLimit, room);
                truncated |= browser == "Firefox"
                    ? ReadFirefox(staged, display, rows, limit, ct)
                    : ReadChromium(staged, browser, display, rows, limit, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{display}: {ex.Message}");
            }
        }

        if (rows.Count == 0 && failures.Count > 0)
        {
            throw new InvalidOperationException(
                failures.Count == 1
                    ? $"Could not read {failures[0]}"
                    : $"None of the {failures.Count} history databases could be read. First: {failures[0]}");
        }
        var note = failures.Count == 0
            ? null
            : $"{failures.Count:N0} of {databases.Count:N0} history databases could not be read ({failures[0]}).";
        return new ParseResult(rows, truncated, null, note);
    }

    /// <summary>
    /// Names the browser from where its profile lives. Matches on either slash direction, so a
    /// profile copied out of a Linux or macOS image is recognised too.
    /// </summary>
    internal static string GuessBrowser(string path)
    {
        var p = path.Replace('\\', '/').ToLowerInvariant();
        if (p.Contains("/bravesoftware/") || p.Contains("/brave-browser")) return "Brave";
        if (p.Contains("/opera software/") || p.Contains("/opera stable") || p.Contains("/opera gx") || p.Contains("/com.operasoftware.opera") || p.Contains("/opera/")) return "Opera";
        if (p.Contains("/vivaldi/")) return "Vivaldi";
        if (p.Contains("/microsoft/edge") || p.Contains("/microsoft edge") || p.Contains("/microsoft-edge")) return "Edge";
        if (p.Contains("/google/chrome") || p.Contains("/google-chrome")) return "Chrome";
        if (p.Contains("/mozilla/firefox") || p.Contains("/.mozilla/") || p.Contains("/firefox/")) return "Firefox";
        if (p.Contains("/chromium/")) return "Chromium";
        return "Chromium-based (browser not identified)";
    }

    /// <summary>Returns true when the database holds more rows than <paramref name="limit"/>.</summary>
    private static bool ReadChromium(StagedDatabase db, string browser, string source, List<object> rows, int limit, CancellationToken ct)
    {
        using var conn = new SqliteConnection(db.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Chromium stores last_visit_time as microseconds since 1601-01-01 UTC. One extra row
        // is requested so "exactly the limit" and "more than the limit" can be told apart.
        cmd.CommandText = """
            SELECT url, title, visit_count, last_visit_time
            FROM urls
            ORDER BY last_visit_time DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit + 1);
        using var r = cmd.ExecuteReader();
        var read = 0;
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (read++ >= limit)
            {
                return true;
            }
            rows.Add(Row(browser, source,
                ChromiumTimeToString(r.IsDBNull(3) ? 0L : r.GetInt64(3)),
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? 0L : r.GetInt64(2)));
        }
        return false;
    }

    private static bool ReadFirefox(StagedDatabase db, string source, List<object> rows, int limit, CancellationToken ct)
    {
        using var conn = new SqliteConnection(db.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Firefox moz_places: last_visit_date is microseconds since the Unix epoch.
        cmd.CommandText = """
            SELECT url, title, visit_count, last_visit_date
            FROM moz_places
            ORDER BY last_visit_date DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit + 1);
        using var r = cmd.ExecuteReader();
        var read = 0;
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (read++ >= limit)
            {
                return true;
            }
            var lvd = r.IsDBNull(3) ? 0L : r.GetInt64(3);
            rows.Add(Row("Firefox", source,
                lvd > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(lvd / 1000).UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
                    : "",
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? 0L : r.GetInt64(2)));
        }
        return false;
    }

    private static object Row(string browser, string source, string lastVisited, string url, string title, long visitCount) => new
    {
        Browser = browser,
        Source = source,
        LastVisited = lastVisited,
        Url = url,
        Title = title,
        VisitCount = visitCount,
    };

    /// <summary>Chromium time = µs since 1601-01-01 UTC.</summary>
    internal static string ChromiumTimeToString(long ts)
    {
        if (ts <= 0) return "";
        try
        {
            return DateTime.FromFileTimeUtc(checked(ts * 10)).ToString("u", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            return "";
        }
    }
}

// ===================== USB history (SYSTEM hive) =====================

public sealed partial class UsbHistoryTool
{
    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
        Task.Run(() => Parse(evidencePath, ct), ct);

    private static ParseResult Parse(string path, CancellationToken ct)
    {
        var hive = HiveReader.Open(path);
        var sets = HiveReader.ControlSets(hive);
        if (sets.Count == 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} has no ControlSet keys, so it is not a SYSTEM hive. " +
                "USB history reads Windows\\System32\\config\\SYSTEM.");
        }

        var rows = new List<object>();
        var found = false;
        foreach (var set in sets)
        {
            ct.ThrowIfCancellationRequested();
            // USBSTOR is Enum\USBSTOR\<device class>\<instance, usually the serial number>.
            var root = hive.GetKey($@"{set.KeyName}\Enum\USBSTOR");
            if (root is null) continue;
            found = true;
            foreach (var classKey in root.SubKeys)
            {
                foreach (var instance in classKey.SubKeys)
                {
                    rows.Add(new
                    {
                        ControlSet = set.KeyName,
                        DeviceClass = classKey.KeyName,
                        Instance = instance.KeyName,
                        FriendlyName = HiveReader.Value(instance, "FriendlyName"),
                        Mfg = HiveReader.Value(instance, "Mfg"),
                        DeviceDesc = HiveReader.Value(instance, "DeviceDesc"),
                        // The key's last-write time. Windows rewrites these keys, so it is not a
                        // first-connected or last-connected time and is not labelled as one.
                        KeyLastWrite = HiveReader.LastWrite(instance),
                    });
                }
            }
        }

        var note = !found
            ? "This SYSTEM hive has no Enum\\USBSTOR key: no USB storage device is recorded in it."
            : rows.Count == 0 ? "The USBSTOR key is present but lists no devices." : null;
        return new ParseResult(rows, false, null, note);
    }
}

// ===================== Network history (SOFTWARE hive) =====================

public sealed partial class WifiHistoryTool
{
    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
        Task.Run(() => Parse(evidencePath, ct), ct);

    private static ParseResult Parse(string path, CancellationToken ct)
    {
        var hive = HiveReader.Open(path);
        if (hive.GetKey(@"Microsoft\Windows NT\CurrentVersion") is null)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} has no Microsoft\\Windows NT\\CurrentVersion key, so it is not a SOFTWARE hive. " +
                "Wi-Fi history reads Windows\\System32\\config\\SOFTWARE.");
        }

        var rows = new List<object>();
        var root = hive.GetKey(@"Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles");
        if (root is null)
        {
            return new ParseResult(rows, false, null,
                "This SOFTWARE hive has no NetworkList\\Profiles key: no network profile is recorded in it.");
        }
        foreach (var profile in root.SubKeys)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(new
            {
                ProfileId = profile.KeyName,
                Name = HiveReader.Value(profile, "ProfileName"),
                Description = HiveReader.Value(profile, "Description"),
                Medium = DescribeNameType(HiveReader.Value(profile, "NameType")),
                Category = DescribeCategory(HiveReader.Value(profile, "Category")),
                // Windows stores these two as SYSTEMTIME in the computer's local time zone.
                FirstConnectedLocal = DecodeSystemTime(HiveReader.RawValue(profile, "DateCreated")),
                LastConnectedLocal = DecodeSystemTime(HiveReader.RawValue(profile, "DateLastConnected")),
                KeyLastWrite = HiveReader.LastWrite(profile),
            });
        }
        return new ParseResult(rows, false, null, rows.Count == 0 ? "The NetworkList\\Profiles key is present but empty." : null);
    }

    /// <summary>
    /// Decodes a 16-byte SYSTEMTIME (year, month, day-of-week, day, hour, minute, second, ms).
    /// It carries no time zone: the value is the wall-clock time on the computer, so the text
    /// has no "Z". Returns an empty string for anything that is not a plausible date.
    /// </summary>
    internal static string DecodeSystemTime(byte[]? raw)
    {
        if (raw is not { Length: >= 16 })
        {
            return "";
        }
        int year = BitConverter.ToUInt16(raw, 0), month = BitConverter.ToUInt16(raw, 2), day = BitConverter.ToUInt16(raw, 6);
        int hour = BitConverter.ToUInt16(raw, 8), minute = BitConverter.ToUInt16(raw, 10), second = BitConverter.ToUInt16(raw, 12);
        if (year is < 1980 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
        {
            return "";
        }
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    internal static string DescribeCategory(string value) => value.Trim() switch
    {
        "0" => "Public",
        "1" => "Private",
        "2" => "Domain",
        "" => "",
        var other => other,
    };

    internal static string DescribeNameType(string value) => value.Trim() switch
    {
        "71" => "Wireless",
        "6" => "Wired",
        "23" => "Mobile broadband",
        "" => "",
        var other => $"Type {other}",
    };
}

// ===================== Amcache (Amcache.hve) =====================

public sealed partial class AmcacheTool
{
    private const int RowBudget = 25_000;

    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
        Task.Run(() => Parse(evidencePath, ct), ct);

    private static ParseResult Parse(string path, CancellationToken ct)
    {
        var hive = HiveReader.Open(path);
        if (hive.GetKey("Root") is null)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} has no Root key, so it is not an Amcache hive. " +
                "Amcache reads Windows\\AppCompat\\Programs\\Amcache.hve.");
        }

        var rows = new List<object>();
        // Windows 10 and 11: one key per file under Root\InventoryApplicationFile.
        if (hive.GetKey(@"Root\InventoryApplicationFile") is { } inventory)
        {
            foreach (var entry in inventory.SubKeys)
            {
                ct.ThrowIfCancellationRequested();
                if (rows.Count >= RowBudget) break;
                var name = HiveReader.Value(entry, "Name");
                rows.Add(Row(entry.KeyName,
                    name.Length > 0 ? name : HiveReader.Value(entry, "ProductName"),
                    HiveReader.Value(entry, "LowerCaseLongPath"),
                    Sha1FromFileId(HiveReader.Value(entry, "FileId")),
                    HiveReader.Value(entry, "Publisher"),
                    HiveReader.Value(entry, "ProgramId"),
                    HiveReader.Value(entry, "Version"),
                    HiveReader.Value(entry, "Size"),
                    HiveReader.LastWrite(entry)));
            }
            return ParseResult.WithBudget(rows, RowBudget);
        }

        // Windows 7 and 8: Root\File\<volume GUID>\<file reference>, values named by number.
        if (hive.GetKey(@"Root\File") is { } legacy)
        {
            foreach (var volume in legacy.SubKeys)
            {
                foreach (var entry in volume.SubKeys)
                {
                    ct.ThrowIfCancellationRequested();
                    if (rows.Count >= RowBudget) break;
                    var full = HiveReader.Value(entry, "15");
                    rows.Add(Row($@"{volume.KeyName}\{entry.KeyName}",
                        full.Length > 0 ? Path.GetFileName(full.Replace('\\', Path.DirectorySeparatorChar)) : HiveReader.Value(entry, "0"),
                        full,
                        Sha1FromFileId(HiveReader.Value(entry, "101")),
                        HiveReader.Value(entry, "1"),
                        HiveReader.Value(entry, "100"),
                        HiveReader.Value(entry, "5"),
                        HiveReader.Value(entry, "6"),
                        HiveReader.LastWrite(entry)));
                }
            }
            return ParseResult.WithBudget(rows, RowBudget);
        }

        return new ParseResult(rows, false, null,
            "This Amcache hive has neither an InventoryApplicationFile nor a File key: it records no files.");
    }

    /// <summary>
    /// Amcache stores a file's SHA-1 as "0000" followed by the 40 hex digits. Only that
    /// four-character prefix is removed. Trimming every leading zero would also eat zeros that
    /// belong to the hash, and one hash in sixteen starts with one.
    /// </summary>
    internal static string Sha1FromFileId(string? fileId)
    {
        var id = (fileId ?? "").Trim();
        return id.Length == 44 && id.StartsWith("0000", StringComparison.Ordinal) ? id[4..] : id;
    }

    private static object Row(string key, string name, string path, string sha1, string publisher, string programId,
                              string version, string size, string keyLastWrite) => new
                              {
                                  Key = key,
                                  Name = name,
                                  Path = path,
                                  Sha1 = sha1,
                                  Publisher = publisher,
                                  ProgramId = programId,
                                  Version = version,
                                  Size = size,
                                  KeyLastWrite = keyLastWrite,
                              };
}

// ===================== ShimCache (SYSTEM hive) =====================

/// <summary>One decoded AppCompatCache entry: the path, and the file's last-modified time when the entry carries one.</summary>
internal readonly record struct ShimEntry(string Path, DateTimeOffset? FileModifiedUtc);

public sealed partial class ShimcacheTool
{
    private const int RowBudget = 25_000;

    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
        Task.Run(() => Parse(evidencePath, ct), ct);

    private static ParseResult Parse(string path, CancellationToken ct)
    {
        var hive = HiveReader.Open(path);
        var sets = HiveReader.ControlSets(hive);
        if (sets.Count == 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} has no ControlSet keys, so it is not a SYSTEM hive. " +
                "ShimCache reads Windows\\System32\\config\\SYSTEM.");
        }

        var rows = new List<object>();
        int found = 0, undecoded = 0;
        foreach (var set in sets)
        {
            ct.ThrowIfCancellationRequested();
            var key = hive.GetKey($@"{set.KeyName}\Control\Session Manager\AppCompatCache");
            if (key is null || HiveReader.RawValue(key, "AppCompatCache") is not { } blob)
            {
                continue;
            }
            found++;
            var entries = DecodeAppCompatCache(blob, RowBudget);
            if (entries.Count == 0)
            {
                undecoded++;
                continue;
            }
            for (var i = 0; i < entries.Count && rows.Count < RowBudget; i++)
            {
                rows.Add(new
                {
                    ControlSet = set.KeyName,
                    // Entries are stored most recently inserted first.
                    Position = i,
                    Path = entries[i].Path,
                    FileModified = entries[i].FileModifiedUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "",
                });
            }
        }

        if (found > 0 && rows.Count == 0)
        {
            throw new InvalidOperationException(
                "The ShimCache value is present but could not be decoded. Cinder reads the Windows 8, 8.1, 10 and 11 layouts; " +
                "older Windows versions store it differently. The raw value is at ControlSet001\\Control\\Session Manager\\AppCompatCache.");
        }
        var note = found == 0
            ? "This SYSTEM hive has no AppCompatCache value."
            : undecoded > 0 ? $"{undecoded} control set(s) hold a ShimCache value in a layout Cinder does not decode; they are not shown." : null;
        return ParseResult.WithBudget(rows, RowBudget, note);
    }

    /// <summary>
    /// Decodes the AppCompatCache value written by Windows 8, 8.1, 10 and 11.
    ///
    /// <para>Windows 10 and 11: the first DWORD is the header length (0x30 or 0x34) and entries
    /// follow it. Each entry is: signature <c>10ts</c> (4), unknown (4), entry data size (4),
    /// path length in bytes (2), UTF-16 path, FILETIME last modified (8), data size (4), data.</para>
    ///
    /// <para>Windows 8 and 8.1: a 0x80-byte header, signature <c>00ts</c> (8.0) or <c>10ts</c>
    /// (8.1), then the same first four fields, followed by a package name (2-byte length plus
    /// bytes), insert flags (4), shim flags (4), and then the FILETIME.</para>
    ///
    /// <para>The entry data size counts from the end of the 12-byte prefix, so the next entry
    /// starts at <c>entry + 12 + size</c>. Returns an empty list for a value in any other layout
    /// rather than guessing.</para>
    /// </summary>
    internal static List<ShimEntry> DecodeAppCompatCache(byte[] blob, int max = RowBudget)
    {
        var entries = new List<ShimEntry>();
        if (blob is null || blob.Length < 0x34 + 14)
        {
            return entries;
        }

        var headerSize = BitConverter.ToInt32(blob, 0);
        int start;
        bool windows8;
        if (headerSize is 0x30 or 0x34 && IsEntrySignature(blob, headerSize))
        {
            start = headerSize;
            windows8 = false;
        }
        else if (IsEntrySignature(blob, 0x80))
        {
            start = 0x80;
            windows8 = true;
        }
        else
        {
            return entries;
        }

        long i = start;
        while (i + 14 <= blob.Length && entries.Count < max && IsEntrySignature(blob, (int)i))
        {
            var dataSize = BitConverter.ToInt32(blob, (int)i + 8);
            var next = i + 12 + dataSize;
            if (dataSize < 2 || next > blob.Length)
            {
                break;
            }

            var p = (int)i + 12;
            int pathLength = BitConverter.ToUInt16(blob, p);
            p += 2;
            if (p + pathLength > next)
            {
                break;
            }
            var path = System.Text.Encoding.Unicode.GetString(blob, p, pathLength);
            p += pathLength;

            if (windows8 && p + 2 <= next)
            {
                int packageLength = BitConverter.ToUInt16(blob, p);
                p += 2 + packageLength + 8;
            }

            DateTimeOffset? modified = null;
            if (p + 8 <= next)
            {
                modified = FileTimeOrNull(BitConverter.ToInt64(blob, p));
            }
            entries.Add(new ShimEntry(path, modified));
            i = next;
        }
        return entries;
    }

    private static bool IsEntrySignature(byte[] blob, int offset) =>
        offset >= 0 && offset + 4 <= blob.Length
        && (blob[offset] == (byte)'1' || blob[offset] == (byte)'0')
        && blob[offset + 1] == (byte)'0' && blob[offset + 2] == (byte)'t' && blob[offset + 3] == (byte)'s';

    private static DateTimeOffset? FileTimeOrNull(long fileTime)
    {
        if (fileTime <= 0)
        {
            return null;
        }
        try
        {
            return DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

// ===================== Email (.msg, .eml, .mbox, .pst, .ost) =====================

public sealed partial class EmailTool
{
    private const int RowBudget = 50_000;

    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
        Task.Run(() => Parse(evidencePath, ct), ct);

    private static ParseResult Parse(string path, CancellationToken ct)
    {
        var rows = new List<object>();
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".msg":
                rows.Add(ParseMsg(path));
                break;
            case ".eml":
                rows.Add(ParseEml(path));
                break;
            case ".mbox":
                using (var reader = new StreamReader(path))
                {
                    rows.AddRange(ReadMbox(reader, Path.GetFileName(path), RowBudget, ct));
                }
                break;
            case ".pst":
            case ".ost":
                ParsePstOst(path, rows, ct);
                break;
            default:
                throw new InvalidOperationException(
                    $"Email reads .msg, .eml, .mbox, .pst and .ost files. {Path.GetFileName(path)} is none of these.");
        }
        return ParseResult.WithBudget(rows, RowBudget);
    }

    private static object ParseMsg(string path)
    {
        using var msg = new MsgReader.Outlook.Storage.Message(path);
        return Row(Path.GetFileName(path),
            msg.GetEmailSender(false, false) ?? "",
            msg.GetEmailRecipients(MsgReader.Outlook.RecipientType.To, false, false) ?? "",
            msg.GetEmailRecipients(MsgReader.Outlook.RecipientType.Cc, false, false) ?? "",
            msg.Subject ?? "",
            msg.SentOn?.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture) ?? "",
            new FileInfo(path).Length,
            msg.Attachments?.Count);
    }

    private static object ParseEml(string path)
    {
        using var fs = File.OpenRead(path);
        var msg = MsgReader.Mime.Message.Load(fs);
        // DateSent is DateTime.MinValue when the header is absent or unparseable.
        var sent = msg.Headers.DateSent == default
            ? ""
            : msg.Headers.DateSent.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);
        return Row(Path.GetFileName(path),
            msg.Headers.From?.Address ?? "",
            string.Join(", ", (msg.Headers.To ?? []).Select(t => t.Address)),
            string.Join(", ", (msg.Headers.Cc ?? []).Select(t => t.Address)),
            msg.Headers.Subject ?? "",
            sent,
            new FileInfo(path).Length,
            null);
    }

    /// <summary>
    /// One row per message in an MBOX, from its headers. "Sent" is the Date header exactly as
    /// the message carries it, time zone included, so nothing is reinterpreted.
    /// </summary>
    internal static List<object> ReadMbox(TextReader reader, string source, int budget, CancellationToken ct)
    {
        var rows = new List<object>();
        foreach (var headers in MboxHeaderReader.ReadMessages(reader, ct))
        {
            if (rows.Count >= budget) break;
            rows.Add(Row(source,
                MboxHeaderReader.Get(headers, "From"),
                MboxHeaderReader.Get(headers, "To"),
                MboxHeaderReader.Get(headers, "Cc"),
                MboxHeaderReader.Get(headers, "Subject"),
                MboxHeaderReader.Get(headers, "Date"),
                null,
                null));
        }
        return rows;
    }

    private static object Row(string source, string from, string to, string cc, string subject, string sent,
                              long? sizeBytes, int? attachments) => new
                              {
                                  Source = source,
                                  From = from,
                                  To = to,
                                  Cc = cc,
                                  Subject = subject,
                                  Sent = sent,
                                  SizeBytes = sizeBytes,
                                  Attachments = attachments,
                              };

    private const string PstScript = """
import json, sys
try:
    import pypff
except ImportError:
    print(json.dumps({"_missing": True}), flush=True)
    sys.exit(0)

def walk(folder, prefix=""):
    try:
        sub = folder.sub_folders
    except Exception:
        sub = []
    for child in sub:
        name = child.name or "(unnamed)"
        yield from walk(child, prefix + "/" + name)
    try:
        for m in folder.sub_messages:
            try:
                rec = {
                    "folder": prefix or "/",
                    "subject": (m.subject or "")[:200],
                    "sender_name": (m.sender_name or "")[:120],
                    "delivery_time": str(m.delivery_time or ""),
                    "attachments": m.number_of_attachments,
                }
            except Exception as ex:
                rec = {"folder": prefix or "/", "subject": f"<unreadable item: {ex}>", "delivery_time": "", "attachments": 0}
            print(json.dumps(rec), flush=True)
    except Exception:
        pass

pff = pypff.file()
pff.open(sys.argv[1])
root = pff.get_root_folder()
for f in walk(root, ""):
    pass
pff.close()
""";

    /// <summary>
    /// PST / OST through the Python <c>libpff-python</c> binding: a short script walks every
    /// folder and prints one JSON object per item. Its exit code and stderr are checked, so a
    /// corrupt or unsupported file is an error with the reader's own message, not an empty grid.
    /// </summary>
    private static void ParsePstOst(string path, List<object> rows, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ResolvePython(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(PstScript);
        psi.ArgumentList.Add(path);

        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start Python. Install Python 3.12 or later, then try again.");
        // Drained concurrently: a child that fills an unread stderr pipe blocks forever.
        var stderrTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        var reachedBudget = false;
        // Stopping must work even while the script is silent (opening a large PST): killing the
        // process ends the stream, which unblocks ReadLine below.
        using var stop = ct.Register(() => KillQuietly(p));
        try
        {
            string? line;
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                System.Text.Json.JsonDocument doc;
                try
                {
                    doc = System.Text.Json.JsonDocument.Parse(line);
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("_missing", out _))
                    {
                        throw new InvalidOperationException(
                            "Reading PST and OST files needs the optional libpff-python package. " +
                            "Install it with:  python -m pip install libpff-python  — then run this again.");
                    }
                    rows.Add(Row(
                        root.TryGetProperty("folder", out var f) ? f.GetString() ?? "" : "",
                        root.TryGetProperty("sender_name", out var sn) ? sn.GetString() ?? "" : "",
                        "",
                        "",
                        root.TryGetProperty("subject", out var sj) ? sj.GetString() ?? "" : "",
                        root.TryGetProperty("delivery_time", out var dt) ? dt.GetString() ?? "" : "",
                        null,
                        root.TryGetProperty("attachments", out var a) && a.TryGetInt32(out var n) ? n : null));
                }
                if (rows.Count >= RowBudget)
                {
                    reachedBudget = true;
                    break;
                }
            }
            ct.ThrowIfCancellationRequested();
            if (reachedBudget)
            {
                KillQuietly(p);
            }
            p.WaitForExit();
        }
        catch
        {
            KillQuietly(p);
            throw;
        }

        var stderr = stderrTask.GetAwaiter().GetResult();
        if (!reachedBudget && p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The PST reader stopped with exit code {p.ExitCode}. {LastLine(stderr)}".TrimEnd());
        }
    }

    private static void KillQuietly(System.Diagnostics.Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already gone.
        }
    }

    /// <summary>The last non-empty line of a traceback: the part that names the actual error.</summary>
    internal static string LastLine(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    private static string ResolvePython()
    {
        var venv = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cinder", "venv");
        var local = OperatingSystem.IsWindows()
            ? Path.Combine(venv, "Scripts", "python.exe")
            : Path.Combine(venv, "bin", "python");
        if (File.Exists(local)) return local;

        // Absolute path, not a bare name — see ExecutableResolver for why that matters here.
        return ExecutableResolver.ResolveRequired(
            OperatingSystem.IsWindows() ? "python.exe" : "python3",
            "Install Python 3.12+ and make sure it is on PATH.");
    }
}
