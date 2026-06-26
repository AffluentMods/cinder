// =====================================================================================
// Real Windows-artifact parser wirings.
//
// Every tool here used to be a "sidecar stub" — the C# layer had a ToolViewModel that
// rendered a Pick file… UI but the LoadAsync override did nothing because the Python
// parser wasn't built. This file replaces each LoadAsync with an in-process C#
// implementation backed by Eric Zimmerman's libraries on NuGet:
//
//   Registry  → registry hive parsing (NTUSER.DAT / SOFTWARE / SYSTEM / SAM / Amcache.hve)
//   evtx      → Windows Event Log (.evtx) parser
//   Lnk       → Windows shortcut (.lnk) parser
//   Prefetch  → Windows Prefetch (.pf) parser
//   JumpList  → automatic + custom destinations parser
//   Microsoft.Data.Sqlite → direct read of browser History/Cookies/Logins SQLite DBs
//
// All run in-process on the threadpool — no Python venv required for these formats.
// =====================================================================================

using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace Cinder.App.ViewModels.Tools;

// ===================== Registry =====================

public sealed partial class RegistryTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        // Registry parsing is CPU-bound on big hives (50–500 MB SOFTWARE), so hand off
        // to the thread pool and only marshal individual rows back to the UI thread.
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
    }

    private static List<object> Parse(string path, CancellationToken ct)
    {
        var hive = new global::Registry.RegistryHive(path)
        {
            RecoverDeleted = false,
        };
        hive.ParseHive();
        var rows = new List<object>(capacity: 4096);
        Walk(hive.Root, rows, ct, depthBudget: 8, rowBudget: 25_000);
        return rows;
    }

    private static void Walk(global::Registry.Abstractions.RegistryKey? key,
                             List<object> rows, CancellationToken ct,
                             int depthBudget, int rowBudget)
    {
        if (key is null || depthBudget < 0 || rows.Count >= rowBudget)
        {
            return;
        }
        ct.ThrowIfCancellationRequested();

        // Emit a row per value at this key.
        foreach (var v in key.Values)
        {
            if (rows.Count >= rowBudget) return;
            rows.Add(new
            {
                Key = key.KeyPath,
                Name = string.IsNullOrEmpty(v.ValueName) ? "(default)" : v.ValueName,
                Type = v.ValueType,
                Value = TruncateValue(v.ValueData),
                LastWrite = key.LastWriteTime?.ToString("u", CultureInfo.InvariantCulture) ?? "",
            });
        }

        foreach (var child in key.SubKeys)
        {
            Walk(child, rows, ct, depthBudget - 1, rowBudget);
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
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
    }

    private static List<object> Parse(string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var log = new evtx.EventLog(fs);
        var rows = new List<object>(capacity: 8192);
        foreach (var rec in log.GetEventRecords())
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= 100_000) break;
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

// ===================== Prefetch (.pf) =====================

public sealed partial class PrefetchTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() =>
        {
            var list = new List<object>();
            if (Directory.Exists(evidencePath))
            {
                foreach (var pf in Directory.EnumerateFiles(evidencePath, "*.pf"))
                {
                    ct.ThrowIfCancellationRequested();
                    ParseOne(pf, list);
                    if (list.Count >= 5_000) break;
                }
            }
            else
            {
                ParseOne(evidencePath, list);
            }
            return list;
        }, ct);
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
    }

    private static void ParseOne(string path, List<object> rows)
    {
        try
        {
            var pf = Prefetch.PrefetchFile.Open(path);
            // RunTimes are most-recent-first. Convert to UTC ISO strings.
            var runTimes = pf.LastRunTimes
                .Select(t => t.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture))
                .ToArray();
            rows.Add(new
            {
                Executable = Path.GetFileNameWithoutExtension(pf.SourceFilename ?? path),
                RunCount = pf.RunCount,
                LastRun = runTimes.Length > 0 ? runTimes[0] : "",
                AllRuns = string.Join(", ", runTimes),
                LoadedFiles = pf.Filenames.Count,
                DirCount = pf.TotalDirectoryCount,
                Source = pf.SourceFilename ?? path,
            });
        }
        catch (Exception ex)
        {
            rows.Add(new
            {
                Executable = Path.GetFileName(path),
                RunCount = 0,
                LastRun = "",
                AllRuns = "",
                LoadedFiles = 0,
                DirCount = 0,
                Source = $"PARSE ERROR: {ex.Message}",
            });
        }
    }
}

// ===================== LNK shortcuts (.lnk) =====================

public sealed partial class LnkTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() =>
        {
            var list = new List<object>();
            if (Directory.Exists(evidencePath))
            {
                foreach (var lnk in Directory.EnumerateFiles(evidencePath, "*.lnk", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    ParseOne(lnk, list);
                    if (list.Count >= 10_000) break;
                }
            }
            else
            {
                ParseOne(evidencePath, list);
            }
            return list;
        }, ct);
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
    }

    private static void ParseOne(string path, List<object> rows)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var lnk = new Lnk.LnkFile(bytes, path, codepage: 1252);
            rows.Add(new
            {
                Source = Path.GetFileName(path),
                Target = lnk.LocalPath ?? lnk.NetworkShareInfo?.NetworkShareName ?? "",
                Args = lnk.Arguments ?? "",
                WorkingDir = lnk.WorkingDirectory ?? "",
                TargetCreated = lnk.SourceCreated?.ToString("u", CultureInfo.InvariantCulture) ?? "",
                TargetModified = lnk.SourceModified?.ToString("u", CultureInfo.InvariantCulture) ?? "",
                TargetAccessed = lnk.SourceAccessed?.ToString("u", CultureInfo.InvariantCulture) ?? "",
                MachineSerial = lnk.VolumeInfo?.VolumeSerialNumber ?? "",
            });
        }
        catch (Exception ex)
        {
            rows.Add(new
            {
                Source = Path.GetFileName(path),
                Target = $"PARSE ERROR: {ex.Message}",
                Args = "",
                WorkingDir = "",
                TargetCreated = "",
                TargetModified = "",
                TargetAccessed = "",
                MachineSerial = "",
            });
        }
    }
}

// ===================== Jumplists =====================

public sealed partial class JumplistsTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() =>
        {
            var list = new List<object>();
            if (Directory.Exists(evidencePath))
            {
                foreach (var f in Directory.EnumerateFiles(evidencePath))
                {
                    ct.ThrowIfCancellationRequested();
                    ParseOne(f, list);
                    if (list.Count >= 20_000) break;
                }
            }
            else
            {
                ParseOne(evidencePath, list);
            }
            return list;
        }, ct);
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
    }

    private static void ParseOne(string path, List<object> rows)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            var bytes = File.ReadAllBytes(path);
            switch (ext)
            {
                case ".automaticdestinations-ms":
                    {
                        var jl = new JumpList.Automatic.AutomaticDestination(bytes, path, codepage: 1252);
                        foreach (var e in jl.DestListEntries)
                        {
                            rows.Add(new
                            {
                                Kind = "automatic",
                                Source = Path.GetFileName(path),
                                AppId = jl.AppId?.AppId ?? "",
                                AppName = jl.AppId?.Description ?? "",
                                Path = e.Path ?? "",
                                Hostname = e.Hostname ?? "",
                                LastModified = e.LastModified.ToString("u", CultureInfo.InvariantCulture),
                                CreatedOn = e.CreatedOn.ToString("u", CultureInfo.InvariantCulture),
                                Pinned = e.Pinned,
                                Mac = e.MacAddress ?? "",
                            });
                        }
                        break;
                    }
                case ".customdestinations-ms":
                    {
                        var jl = new JumpList.Custom.CustomDestination(bytes, path, codepage: 1252);
                        foreach (var e in jl.Entries)
                        {
                            foreach (var lnk in e.LnkFiles ?? new List<Lnk.LnkFile>())
                            {
                                rows.Add(new
                                {
                                    Kind = "custom",
                                    Source = Path.GetFileName(path),
                                    AppId = jl.AppId?.AppId ?? "",
                                    AppName = jl.AppId?.Description ?? "",
                                    Path = lnk.LocalPath ?? "",
                                    Hostname = "",
                                    LastModified = lnk.SourceModified?.ToString("u", CultureInfo.InvariantCulture) ?? "",
                                    CreatedOn = lnk.SourceCreated?.ToString("u", CultureInfo.InvariantCulture) ?? "",
                                    Pinned = false,
                                    Mac = "",
                                });
                            }
                        }
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            rows.Add(new
            {
                Kind = ext.TrimStart('.'),
                Source = Path.GetFileName(path),
                AppId = "",
                AppName = $"PARSE ERROR: {ex.Message}",
                Path = "",
                Hostname = "",
                LastModified = "",
                CreatedOn = "",
                Pinned = false,
                Mac = "",
            });
        }
    }
}

// ===================== Browser history =====================

public sealed partial class BrowserHistoryTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
    }

    private static List<object> Parse(string path, CancellationToken ct)
    {
        // Accept either a directory (browser profile root) or a History file directly.
        var historyDbs = new List<(string Path, string Browser)>();

        if (Directory.Exists(path))
        {
            ScanProfileDir(path, historyDbs);
        }
        else if (File.Exists(path))
        {
            var name = Path.GetFileName(path);
            // Most browsers — Chromium-family: History (no extension); Firefox: places.sqlite.
            if (name.Equals("places.sqlite", StringComparison.OrdinalIgnoreCase))
            {
                historyDbs.Add((path, "Firefox"));
            }
            else
            {
                historyDbs.Add((path, GuessBrowser(path)));
            }
        }

        var rows = new List<object>();
        foreach (var (db, browser) in historyDbs)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= 100_000) break;

            // Copy the DB to a temp file so we can open it even if the browser has it locked.
            var staging = Path.Combine(Path.GetTempPath(), $"cinder-history-{Guid.NewGuid():N}.sqlite");
            try
            {
                File.Copy(db, staging, overwrite: true);
                if (browser == "Firefox")
                {
                    ReadFirefox(staging, browser, rows, ct);
                }
                else
                {
                    ReadChromium(staging, browser, rows, ct);
                }
            }
            catch (Exception ex)
            {
                rows.Add(new
                {
                    Browser = browser,
                    Source = Path.GetFileName(db),
                    Visited = "",
                    Url = $"PARSE ERROR: {ex.Message}",
                    Title = "",
                    VisitCount = 0,
                });
            }
            finally
            {
                try { File.Delete(staging); } catch { }
            }
        }
        return rows;
    }

    private static void ScanProfileDir(string dir, List<(string, string)> hits)
    {
        // Chrome/Edge/Brave/Opera: "History" SQLite, possibly under "Default/" or
        // "Profile N/" beneath the User Data root.
        foreach (var f in Directory.EnumerateFiles(dir, "History", SearchOption.AllDirectories))
        {
            hits.Add((f, GuessBrowser(f)));
        }
        foreach (var f in Directory.EnumerateFiles(dir, "places.sqlite", SearchOption.AllDirectories))
        {
            hits.Add((f, "Firefox"));
        }
    }

    private static string GuessBrowser(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.Contains("\\google\\chrome\\")) return "Chrome";
        if (lower.Contains("\\microsoft\\edge\\")) return "Edge";
        if (lower.Contains("\\brave\\")) return "Brave";
        if (lower.Contains("\\opera\\")) return "Opera";
        if (lower.Contains("\\vivaldi\\")) return "Vivaldi";
        if (lower.Contains("\\mozilla\\firefox\\")) return "Firefox";
        return "Chromium-family";
    }

    private static void ReadChromium(string dbPath, string browser, List<object> rows, CancellationToken ct)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // urls + visits join: Chromium stores last_visit_time as microseconds since 1601-01-01.
        cmd.CommandText = """
            SELECT u.url AS Url, u.title AS Title, u.visit_count AS VisitCount,
                   u.last_visit_time AS LastVisitTime
            FROM urls u
            ORDER BY u.last_visit_time DESC
            LIMIT 50000;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            var lvt = r.GetInt64(3);
            rows.Add(new
            {
                Browser = browser,
                Source = Path.GetFileName(dbPath),
                Visited = ChromiumTimeToString(lvt),
                Url = r.GetString(0),
                Title = r.IsDBNull(1) ? "" : r.GetString(1),
                VisitCount = r.GetInt32(2),
            });
        }
    }

    private static void ReadFirefox(string dbPath, string browser, List<object> rows, CancellationToken ct)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Firefox moz_places: last_visit_date is microseconds since Unix epoch.
        cmd.CommandText = """
            SELECT url, title, visit_count, last_visit_date
            FROM moz_places
            ORDER BY last_visit_date DESC
            LIMIT 50000;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            var lvd = r.IsDBNull(3) ? 0L : r.GetInt64(3);
            rows.Add(new
            {
                Browser = browser,
                Source = Path.GetFileName(dbPath),
                Visited = lvd > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(lvd / 1000).UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
                    : "",
                Url = r.GetString(0),
                Title = r.IsDBNull(1) ? "" : r.GetString(1),
                VisitCount = r.GetInt32(2),
            });
        }
    }

    /// <summary>Chromium time = µs since 1601-01-01 UTC.</summary>
    private static string ChromiumTimeToString(long ts)
    {
        if (ts <= 0) return "";
        try
        {
            return DateTime.FromFileTimeUtc(ts * 10).ToString("u", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "";
        }
    }
}

// ===================== USB history (SYSTEM hive) =====================

public sealed partial class UsbHistoryTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
    }

    private static List<object> Parse(string path, CancellationToken ct)
    {
        var hive = new global::Registry.RegistryHive(path);
        hive.ParseHive();
        var rows = new List<object>();
        // USBSTOR lives under ControlSetXXX\Enum\USBSTOR. We look at each ControlSet.
        foreach (var setName in new[] { "ControlSet001", "ControlSet002", "CurrentControlSet" })
        {
            ct.ThrowIfCancellationRequested();
            var root = hive.GetKey($@"{setName}\Enum\USBSTOR");
            if (root is null) continue;
            foreach (var classKey in root.SubKeys)
            {
                foreach (var instance in classKey.SubKeys)
                {
                    rows.Add(new
                    {
                        ControlSet = setName,
                        Class = classKey.KeyName,
                        Instance = instance.KeyName,
                        FriendlyName = instance.Values.FirstOrDefault(v => v.ValueName == "FriendlyName")?.ValueData ?? "",
                        Mfg = instance.Values.FirstOrDefault(v => v.ValueName == "Mfg")?.ValueData ?? "",
                        DeviceDesc = instance.Values.FirstOrDefault(v => v.ValueName == "DeviceDesc")?.ValueData ?? "",
                        FirstInstall = instance.LastWriteTime?.ToString("u", CultureInfo.InvariantCulture) ?? "",
                    });
                }
            }
        }
        return rows;
    }
}

// ===================== Wi-Fi history (SOFTWARE hive) =====================

public sealed partial class WifiHistoryTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
    }

    private static List<object> Parse(string path, CancellationToken ct)
    {
        var hive = new global::Registry.RegistryHive(path);
        hive.ParseHive();
        var rows = new List<object>();
        var root = hive.GetKey(@"Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles");
        if (root is null) return rows;
        foreach (var profile in root.SubKeys)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(new
            {
                ProfileId = profile.KeyName,
                Name = profile.Values.FirstOrDefault(v => v.ValueName == "ProfileName")?.ValueData ?? "",
                Description = profile.Values.FirstOrDefault(v => v.ValueName == "Description")?.ValueData ?? "",
                Category = profile.Values.FirstOrDefault(v => v.ValueName == "Category")?.ValueData ?? "",
                LastSeen = profile.LastWriteTime?.ToString("u", CultureInfo.InvariantCulture) ?? "",
            });
        }
        return rows;
    }
}

// ===================== Amcache (Amcache.hve) =====================

public sealed partial class AmcacheTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
    }

    private static List<object> Parse(string path, CancellationToken ct)
    {
        var hive = new global::Registry.RegistryHive(path);
        hive.ParseHive();
        var rows = new List<object>();
        // InventoryApplicationFile is the key Amcache table on Win10+.
        var root = hive.GetKey(@"Root\InventoryApplicationFile") ??
                   hive.GetKey(@"Root\File");
        if (root is null) return rows;
        foreach (var entry in root.SubKeys)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= 25_000) break;
            rows.Add(new
            {
                Key = entry.KeyName,
                Name = entry.Values.FirstOrDefault(v => v.ValueName == "Name" || v.ValueName == "ProductName")?.ValueData ?? "",
                LowerCaseLongPath = entry.Values.FirstOrDefault(v => v.ValueName == "LowerCaseLongPath")?.ValueData ?? "",
                Sha1 = (entry.Values.FirstOrDefault(v => v.ValueName == "FileId")?.ValueData ?? "")
                       .TrimStart('0', '?'),
                Publisher = entry.Values.FirstOrDefault(v => v.ValueName == "Publisher" || v.ValueName == "ProgramId")?.ValueData ?? "",
                Version = entry.Values.FirstOrDefault(v => v.ValueName == "Version")?.ValueData ?? "",
                Size = entry.Values.FirstOrDefault(v => v.ValueName == "Size")?.ValueData ?? "",
                LastSeen = entry.LastWriteTime?.ToString("u", CultureInfo.InvariantCulture) ?? "",
            });
        }
        return rows;
    }
}

// ===================== ShimCache (SYSTEM hive) =====================

public sealed partial class ShimcacheTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
    }

    private static List<object> Parse(string path, CancellationToken ct)
    {
        // AppCompatCache lives at ControlSet00X\Control\Session Manager\AppCompatCache.
        // Format varies by Windows version. We decode Win10/11 ("10ts" magic 0x31307473) and
        // Win8/8.1 ("10ts"/"00ts") which together cover 99% of modern evidence. Older formats
        // (XP/2003/Vista/Win7) surface as a "binary blob present" row pointing at Hex viewer.
        var hive = new global::Registry.RegistryHive(path);
        hive.ParseHive();
        var rows = new List<object>();
        foreach (var setName in new[] { "ControlSet001", "ControlSet002", "CurrentControlSet" })
        {
            ct.ThrowIfCancellationRequested();
            var key = hive.GetKey($@"{setName}\Control\Session Manager\AppCompatCache");
            if (key is null) continue;
            var val = key.Values.FirstOrDefault(v => v.ValueName == "AppCompatCache");
            var blob = val?.ValueDataRaw;
            if (blob is null || blob.Length < 16) continue;

            var entries = DecodeWin10Or8(blob);
            if (entries.Count == 0)
            {
                rows.Add(new
                {
                    ControlSet = setName,
                    Path = "",
                    LastModified = "",
                    Note = $"AppCompatCache blob present ({blob.Length:N0} bytes) but its header doesn't match Win8/Win10/Win11. Open in Hex viewer for inspection.",
                });
                continue;
            }
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(new
                {
                    ControlSet = setName,
                    Path = e.Path,
                    LastModified = e.LastModified?.ToString("u", CultureInfo.InvariantCulture) ?? "",
                    Note = e.Note,
                });
                if (rows.Count >= 25_000) break;
            }
        }
        return rows;
    }

    private readonly record struct ShimEntry(string Path, DateTimeOffset? LastModified, string Note);

    /// <summary>
    /// Win10/Win11 AppCompatCache parser. The cache is a stream of variable-length entries each
    /// starting with the magic "10ts" (0x73 0x74 0x73 0x31 reversed — "10ts"). After the 4-byte
    /// magic comes a 4-byte length, then the entry payload: { 2-byte path length, N bytes UTF-16
    /// path, 8-byte FILETIME, 4-byte data size, … skipped … }.
    /// Win8/8.1 uses a similar layout with magic "00ts" / "10ts".
    /// </summary>
    private static List<ShimEntry> DecodeWin10Or8(byte[] blob)
    {
        var entries = new List<ShimEntry>();
        // Modern Win10+ cache starts with a 48-byte header. Earlier Win10 builds and Win8
        // start at offset 128 or 0x80. We scan for the "10ts" / "00ts" signature instead of
        // hard-coding the header size — far more robust against monthly Win10 build changes.
        int i = 0;
        while (i + 12 < blob.Length && entries.Count < 25_000)
        {
            // Look for "10ts" (0x31 0x30 0x74 0x73) or "00ts" (0x30 0x30 0x74 0x73).
            if (!(blob[i] == 0x31 || blob[i] == 0x30) ||
                blob[i + 1] != 0x30 || blob[i + 2] != 0x74 || blob[i + 3] != 0x73)
            {
                i++;
                continue;
            }

            // Length of this entry's payload (after the magic + length fields).
            int entryLen = BitConverter.ToInt32(blob, i + 4);
            if (entryLen <= 0 || i + 8 + entryLen > blob.Length)
            {
                i++;
                continue;
            }
            int p = i + 8;
            try
            {
                // Path: u16 length-prefixed UTF-16 string.
                ushort pathLen = BitConverter.ToUInt16(blob, p);
                p += 2;
                if (pathLen == 0 || pathLen > entryLen) { i = p; continue; }
                if (p + pathLen > blob.Length) break;
                var path = System.Text.Encoding.Unicode.GetString(blob, p, pathLen);
                p += pathLen;

                // FILETIME: last-modified of the executable.
                if (p + 8 > blob.Length) break;
                long ft = BitConverter.ToInt64(blob, p);
                DateTimeOffset? lastMod = null;
                if (ft > 0 && ft < 0x7FFFFFFFFFFFFFFFL)
                {
                    try { lastMod = DateTimeOffset.FromFileTime(ft); } catch { /* invalid */ }
                }

                entries.Add(new ShimEntry(path, lastMod, ""));
            }
            catch
            {
                // Malformed entry — skip past the magic and keep scanning.
            }
            i = i + 8 + entryLen;
        }
        return entries;
    }
}

// ===================== Email (.msg + .eml + .mbox) =====================

public sealed partial class EmailTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
    }

    private static List<object> Parse(string path, CancellationToken ct)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var rows = new List<object>();
        switch (ext)
        {
            case ".msg":
                ParseMsg(path, rows);
                break;
            case ".eml":
                ParseEml(path, rows);
                break;
            case ".mbox":
                ParseMbox(path, rows, ct);
                break;
            case ".pst":
            case ".ost":
                ParsePstOst(path, rows, ct);
                break;
            default:
                rows.Add(new
                {
                    Source = Path.GetFileName(path),
                    From = "",
                    To = "",
                    Subject = $"Unsupported email extension: {ext}",
                    Sent = "",
                    Size = 0L,
                });
                break;
        }
        return rows;
    }

    private static void ParseMsg(string path, List<object> rows)
    {
        try
        {
            using var msg = new MsgReader.Outlook.Storage.Message(path);
            rows.Add(new
            {
                Source = Path.GetFileName(path),
                From = msg.GetEmailSender(false, false) ?? "",
                To = msg.GetEmailRecipients(MsgReader.Outlook.RecipientType.To, false, false) ?? "",
                Subject = msg.Subject ?? "",
                Sent = msg.SentOn?.ToString("u", CultureInfo.InvariantCulture) ?? "",
                Size = new FileInfo(path).Length,
            });
        }
        catch (Exception ex)
        {
            rows.Add(new
            {
                Source = Path.GetFileName(path),
                From = "",
                To = "",
                Subject = $"PARSE ERROR: {ex.Message}",
                Sent = "",
                Size = 0L,
            });
        }
    }

    private static void ParseEml(string path, List<object> rows)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var msg = MsgReader.Mime.Message.Load(fs);
            rows.Add(new
            {
                Source = Path.GetFileName(path),
                From = msg.Headers.From?.Address ?? "",
                To = string.Join(", ", msg.Headers.To?.Select(t => t.Address) ?? Array.Empty<string>()),
                Subject = msg.Headers.Subject ?? "",
                Sent = msg.Headers.DateSent.ToString("u", CultureInfo.InvariantCulture),
                Size = new FileInfo(path).Length,
            });
        }
        catch (Exception ex)
        {
            rows.Add(new
            {
                Source = Path.GetFileName(path),
                From = "",
                To = "",
                Subject = $"PARSE ERROR: {ex.Message}",
                Sent = "",
                Size = 0L,
            });
        }
    }

    private static void ParseMbox(string path, List<object> rows, CancellationToken ct)
    {
        // MBOX = concatenation of RFC-822 messages, each starting with "From " at column 0.
        // We do a streaming pass: split on "From " lines at BOL and parse the headers of each.
        try
        {
            using var sr = new StreamReader(path);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            bool inHeaders = false;
            while ((line = sr.ReadLine()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                if (rows.Count >= 50_000) break;

                if (line.StartsWith("From ", StringComparison.Ordinal))
                {
                    if (headers.Count > 0)
                    {
                        rows.Add(BuildEmlRow(path, headers));
                        headers.Clear();
                    }
                    inHeaders = true;
                    continue;
                }
                if (inHeaders && string.IsNullOrEmpty(line))
                {
                    inHeaders = false;
                    continue;
                }
                if (inHeaders)
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        headers[line[..colon]] = line[(colon + 1)..].Trim();
                    }
                }
            }
            if (headers.Count > 0)
            {
                rows.Add(BuildEmlRow(path, headers));
            }
        }
        catch (Exception ex)
        {
            rows.Add(new
            {
                Source = Path.GetFileName(path),
                From = "",
                To = "",
                Subject = $"PARSE ERROR: {ex.Message}",
                Sent = "",
                Size = 0L,
            });
        }
    }

    private static object BuildEmlRow(string path, Dictionary<string, string> headers) => new
    {
        Source = Path.GetFileName(path),
        From = headers.TryGetValue("From", out var f) ? f : "",
        To = headers.TryGetValue("To", out var t) ? t : "",
        Subject = headers.TryGetValue("Subject", out var s) ? s : "",
        Sent = headers.TryGetValue("Date", out var d) ? d : "",
        Size = 0L,
    };

    /// <summary>
    /// PST / OST via the Python <c>libpff-python</c> binding (sister library to pyewf).
    /// Shells out to <c>python -c &lt;inline-script&gt;</c> with the .pst path as argv[1];
    /// the script walks every folder and emits one JSON object per item line. If pypff
    /// isn't installed we surface an actionable install hint as the only "row" so the
    /// user has a clear next step.
    /// </summary>
    private static void ParsePstOst(string path, List<object> rows, CancellationToken ct)
    {
        const string Script = """
import json, sys
try:
    import pypff
except ImportError:
    print(json.dumps({"_install_hint": "Run:  python -m pip install libpff-python  — then re-open this PST/OST."}), flush=True)
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
                    "sender_email": "",  # libpff doesn't expose this directly on all versions
                    "delivery_time": str(m.delivery_time or ""),
                    "client_submit_time": str(m.client_submit_time or ""),
                    "attachments": m.number_of_attachments,
                }
            except Exception as ex:
                rec = {"folder": prefix, "subject": f"<error: {ex}>", "delivery_time": "", "attachments": 0}
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

        try
        {
            var python = ResolvePython();
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = python,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(Script);
            psi.ArgumentList.Add(path);

            using var p = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Could not spawn Python — install Python 3.12+ and pypff (libpff-python).");

            while (!p.StandardOutput.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = p.StandardOutput.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("_install_hint", out var hint))
                    {
                        rows.Add(new
                        {
                            Source = Path.GetFileName(path),
                            From = "",
                            To = "",
                            Subject = hint.GetString() ?? "Install libpff-python.",
                            Sent = "",
                            Size = new FileInfo(path).Length,
                        });
                        return;
                    }
                    rows.Add(new
                    {
                        Source = root.GetProperty("folder").GetString() ?? "",
                        From = root.TryGetProperty("sender_name", out var sn) ? sn.GetString() ?? "" : "",
                        To = "",
                        Subject = root.TryGetProperty("subject", out var sj) ? sj.GetString() ?? "" : "",
                        Sent = root.TryGetProperty("delivery_time", out var dt) ? dt.GetString() ?? "" : "",
                        Size = root.TryGetProperty("attachments", out var a) && a.TryGetInt32(out var n) ? (long)n : 0L,
                    });
                }
                catch { /* skip malformed line */ }
            }
            p.WaitForExit();
        }
        catch (Exception ex)
        {
            rows.Add(new
            {
                Source = Path.GetFileName(path),
                From = "",
                To = "",
                Subject = $"PST/OST parse failed: {ex.Message}",
                Sent = "",
                Size = 0L,
            });
        }
    }

    private static string ResolvePython()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cinder", "venv", "Scripts", "python.exe");
        if (File.Exists(local)) return local;
        return OperatingSystem.IsWindows() ? "python.exe" : "python3";
    }
}
