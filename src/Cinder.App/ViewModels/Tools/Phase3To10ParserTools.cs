// =====================================================================================
// Parsers for disk images, shellbags, SRUM, packet captures, Linux artifacts, mobile backups
// and the Recycle Bin. Each parses on the thread pool and returns its rows to
// SidecarToolViewModel, which owns the grid.
//
// The same two rules as the Windows parsers:
//   * A wrong input or a failed parse is thrown, so the examiner sees an error banner. It
//     must never look like an artifact with nothing in it.
//   * Cancellation propagates. No catch-all may swallow OperationCanceledException.
// =====================================================================================

using System.Globalization;
using System.Text;
using Cinder.App.Services;
using Cinder.Filesystems;
using Cinder.Imaging;
using Cinder.Imaging.Ewf;
using Cinder.Search;
using DiscUtils;
using DiscUtils.Streams;
using Microsoft.Data.Sqlite;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Cinder.App.ViewModels.Tools;

/// <summary>Row-budget bookkeeping shared by parsers that read several sources into one grid.</summary>
internal static class RowBudgets
{
    /// <summary>
    /// Adds up to <paramref name="budget"/> rows from <paramref name="items"/> to
    /// <paramref name="into"/> and returns true when the source held more than that. Each source
    /// gets its own budget, so one large table cannot starve the ones read after it, and
    /// "exactly the budget" is not mistaken for "cut short". An item mapped to null is skipped.
    /// </summary>
    public static bool Take<T>(IEnumerable<T> items, int budget, ICollection<object> into, Func<T, object?> toRow)
    {
        var added = 0;
        foreach (var item in items)
        {
            if (added >= budget)
            {
                return true;
            }
            if (toRow(item) is { } row)
            {
                into.Add(row);
                added++;
            }
        }
        return false;
    }
}

// ============================================================ FILESYSTEM ============

public sealed partial class FilesystemTool
{
    protected override async Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct)
    {
        var settings = new SettingsStore().Load();
        var (rows, truncated) = await Task.Run(() => Parse(evidencePath, settings, ct), ct);
        return new ParseResult(rows, truncated);
    }

    /// <summary>
    /// Opens the image (raw, E01 chain, or VHD/VHDX container) and hands it to
    /// <see cref="DiscUtilsWalker"/>, which owns detection, enumeration, deleted-entry recovery
    /// and optional hashing. This method only maps <see cref="FileEntry"/> to grid rows.
    /// </summary>
    private static (List<object> Rows, bool Truncated) Parse(string path, CinderSettings settings, CancellationToken ct)
    {
        var rows = new List<object>();
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // Hash-set lookup is opt-in: it reads every file on the volume.
        HashSetService? hashSets = null;
        WalkOptions options;
        if (settings.HashFilesOnEnumerate)
        {
            if (!string.IsNullOrEmpty(settings.HashSetDatabase) && File.Exists(settings.HashSetDatabase))
            {
                hashSets = new HashSetService(settings.HashSetDatabase);
            }
            var hs = hashSets;
            options = new WalkOptions(
                HashFiles: true,
                HashSizeLimitBytes: Math.Max(1, settings.HashSizeLimitMb) * 1024L * 1024L,
                Lookup: hs is null ? null : (sha1, md5) =>
                {
                    var m = hs.Lookup("sha1", sha1) ?? hs.Lookup("md5", md5);
                    return m is null ? null : new HashVerdict(m.Verdict.ToString(), m.SetName, m.Label);
                });
        }
        else
        {
            options = new WalkOptions();
        }

        try
        {
            WalkResult result;
            if (ext == ".e01" || EvidenceOpener.IsEwf(path))
            {
                // `using`: EwfReader holds one open FileStream per segment in the chain.
                using var ewf = EwfReader.Open(path);
                using var ewfStream = ewf.OpenStream();

                // The hashes below are what the container records about itself — an assertion
                // by whatever wrote the image, not a verification of it. Labelled so nobody
                // reads the row as a checked result; Image verify re-hashes and compares.
                rows.Add(MetadataRow("[EWF metadata]", "E01", ewf.MediaSize, ewf.AcquisitionDate ?? "",
                    $"EWF media_size={ewf.MediaSize:N0} bytes · sectors={ewf.NumberOfSectors:N0} · " +
                    $"segments={ewf.SegmentCount} · recorded (UNVERIFIED) MD5={ewf.RecordedMd5 ?? "none"} · " +
                    $"SHA1={ewf.RecordedSha1 ?? "none"} — run Image verify to check them"));
                result = DiscUtilsWalker.Walk(ewfStream, options, ct);
            }
            else if (ext is ".vhd" or ".vhdx")
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using VirtualDisk disk = ext == ".vhd"
                    ? new DiscUtils.Vhd.Disk(stream, Ownership.None)
                    : new DiscUtils.Vhdx.Disk(stream, Ownership.None);
                result = DiscUtilsWalker.WalkDisk(disk, options, ct);
            }
            else
            {
                using var stream = EvidenceOpener.Open(path);
                result = DiscUtilsWalker.Walk(stream, options, ct);
            }

            if (result.Entries.Count == 0)
            {
                throw new InvalidOperationException(
                    "No NTFS, FAT, ext or ISO 9660 filesystem was found in this file. " +
                    "Filesystem reads disk and partition images; other filesystems are not supported.");
            }

            foreach (var e in result.Entries)
            {
                rows.Add(ToRow(e, options.HashFiles));
            }

            if (result.DeletedRecoveryError is { } err)
            {
                rows.Add(MetadataRow("[deleted]", "(unavailable)", 0, "", $"Deleted-entry recovery failed: {err}"));
            }
            if (result.DeletedTruncated)
            {
                rows.Add(MetadataRow("[deleted]", "…", 0, "", "Deleted-entry listing cut short — the volume has more not-in-use MFT records."));
            }
            if (options.HashFiles)
            {
                rows.Add(MetadataRow("[hashing]", "summary", result.HashedBytes, "",
                    $"Hashed {result.HashedFiles:N0} files ({result.HashedBytes:N0} bytes), skipped {result.HashSkipped:N0}" +
                    (hashSets is null ? " · no hash-set database configured, so verdicts are absent" : $" · verdicts from {Path.GetFileName(settings.HashSetDatabase)}")));
            }

            return (rows, result.Truncated || result.DeletedTruncated);
        }
        finally
        {
            hashSets?.Dispose();
        }
    }

    private static object ToRow(FileEntry e, bool hashed)
    {
        var x = e.Extras;
        string Get(string k) => x is not null && x.TryGetValue(k, out var v) ? v : "";

        var note = e.IsDeleted ? Get("note") : Get("attributes");
        if (hashed)
        {
            var skipped = Get("hash_skipped");
            if (skipped.Length > 0) note = $"hash skipped: {skipped} · {note}";
            var set = Get("hash_set");
            if (set.Length > 0) note = $"{Get("verdict")} in {set}{(Get("hash_label").Length > 0 ? " (" + Get("hash_label") + ")" : "")} · {note}";
        }

        // One row shape for every row. The DataGrid derives its columns from the first item,
        // and the EWF metadata row comes first — a second shape would hide Verdict / Sha1.
        var verdict = e.IsDirectory || e.IsDeleted || !hashed
            ? ""
            : Get("verdict").Length > 0 ? Get("verdict") : (Get("sha1").Length > 0 ? "Unknown" : "");

        if (e.TimesAreLocalWallClock)
        {
            // FAT stores the device's clock with no zone. Printing it with "Z" would claim UTC.
            const string basis = "times are the device's local clock; time zone not recorded";
            note = note.Length > 0 ? $"{basis} · {note}" : basis;
        }
        return Row(e.Inode, e.Path, e.Name, e.Size, e.IsDirectory, e.IsDeleted, verdict, Get("sha1"),
                   Fmt(e.ModifiedUtc, e.TimesAreLocalWallClock), Fmt(e.CreatedUtc, e.TimesAreLocalWallClock),
                   Fmt(e.AccessedUtc, e.TimesAreLocalWallClock), e.Owner ?? "", note);
    }

    private static object MetadataRow(string path, string name, long size, string modified, string note)
        => Row(0L, path, name, size, true, false, "", "", modified, "", "", "", note);

    private static object Row(long inode, string path, string name, long size, bool isDir, bool isDeleted,
                              string verdict, string sha1, string modified, string created, string accessed,
                              string owner, string note) => new
                              {
                                  Inode = inode,
                                  Path = path,
                                  Name = name,
                                  Size = size,
                                  IsDirectory = isDir,
                                  IsDeleted = isDeleted,
                                  Verdict = verdict,
                                  Sha1 = sha1,
                                  Modified = modified,
                                  Created = created,
                                  Accessed = accessed,
                                  Owner = owner,
                                  Note = note,
                              };

    private static string Fmt(DateTimeOffset? t, bool localWallClock = false) =>
        t is not { } v ? ""
        : localWallClock ? v.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " (local)"
        : v.ToString("u", CultureInfo.InvariantCulture);
}

// ============================================================ SHELLBAGS ============

public sealed partial class ShellbagsTool
{
    private const int RowBudget = 25_000;

    protected override async Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        return ParseResult.WithBudget(rows, RowBudget);
    }

    private static List<object> Parse(string path, CancellationToken ct)
    {
        var hive = HiveReader.Open(path);
        var rows = new List<object>();
        // BagMRU lives at slightly different paths depending on hive flavour:
        //   NTUSER.DAT       Software\Microsoft\Windows\Shell\BagMRU
        //                    Software\Microsoft\Windows\ShellNoRoam\BagMRU
        //   UsrClass.dat     Local Settings\Software\Microsoft\Windows\Shell\BagMRU
        //                    Local Settings\Software\Microsoft\Windows\ShellNoRoam\BagMRU
        var roots = new[]
        {
            @"Software\Microsoft\Windows\Shell\BagMRU",
            @"Local Settings\Software\Microsoft\Windows\Shell\BagMRU",
            @"Software\Microsoft\Windows\ShellNoRoam\BagMRU",
            @"Local Settings\Software\Microsoft\Windows\ShellNoRoam\BagMRU",
        };
        var found = false;
        foreach (var rootPath in roots)
        {
            ct.ThrowIfCancellationRequested();
            var key = hive.GetKey(rootPath);
            if (key is null) continue;
            found = true;
            WalkBagMru(key, parentPath: "", rows, ct);
        }
        if (!found)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} has no BagMRU key. Shellbags reads a user's NTUSER.DAT or UsrClass.dat, " +
                "not a system hive.");
        }
        return rows;
    }

    private static void WalkBagMru(global::Registry.Abstractions.RegistryKey key,
                                    string parentPath, List<object> rows, CancellationToken ct)
    {
        if (rows.Count >= RowBudget) return;

        // Reconstruct this key's path component from its binary shell-item value. BagMRU
        // entries are numbered ("0", "1", "2"...). Each numbered value at the PARENT is a
        // shell-item blob describing that visited folder. We resolve our own component by
        // looking at our key name (which equals the numbered value we represent) against the
        // parent's same-named value. Root-level keys have no shell-item; just use their key
        // name.
        var componentName = DecodeOwnShellItem(key) ?? key.KeyName;
        var thisPath = string.IsNullOrEmpty(parentPath)
            ? componentName
            : (parentPath.EndsWith('\\') ? parentPath + componentName : parentPath + "\\" + componentName);

        var nodeSlot = key.Values.FirstOrDefault(v => v.ValueName == "NodeSlot");
        rows.Add(new
        {
            Path = thisPath,
            NodeSlot = nodeSlot?.ValueData ?? "",
            Subfolders = key.SubKeys.Count,
            KeyLastWrite = HiveReader.LastWrite(key),
            HiveKey = key.KeyPath,
        });
        foreach (var sub in key.SubKeys)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= RowBudget) return;
            WalkBagMru(sub, thisPath, rows, ct);
        }
    }

    /// <summary>
    /// Resolves a BagMRU subkey's path component to a human-readable string by parsing the
    /// corresponding numbered value on its parent. Returns null if there's no parent or the
    /// shell-item type isn't one we recognise.
    /// </summary>
    private static string? DecodeOwnShellItem(global::Registry.Abstractions.RegistryKey key)
    {
        var parent = key.Parent;
        if (parent is null) return null;
        var val = parent.Values.FirstOrDefault(v => v.ValueName == key.KeyName);
        if (val?.ValueDataRaw is not { Length: > 2 } bytes) return null;
        return DecodeShellItemBytes(bytes);
    }

    /// <summary>
    /// Picks the right Lnk.ShellItems decoder based on the shell-item type byte at offset 2.
    /// Falls back to the printable subset of the buffer for unrecognised types.
    /// </summary>
    internal static string? DecodeShellItemBytes(byte[] bytes)
    {
        try
        {
            // bytes[0..2] = total size (LE); bytes[2] = type code.
            var typeCode = bytes[2];
            string? value = typeCode switch
            {
                0x1F => new Lnk.ShellItems.ShellBag0X1F(bytes).Value,
                0x23 => new Lnk.ShellItems.ShellBag0X23(bytes, codepage: 1252).Value,
                0x2E => new Lnk.ShellItems.ShellBag0X2E(bytes).Value,
                0x2F => new Lnk.ShellItems.ShellBag0X2F(bytes, codepage: 1252).Value,
                0x31 or 0x32 or 0xB1 => new Lnk.ShellItems.ShellBag0X31(bytes, codepage: 1252).Value,
                0x35 or 0x71 => new Lnk.ShellItems.ShellBag0X71(bytes).Value,
                0x40 or 0x41 or 0x42 or 0x46 or 0x47 or 0xC3 => new Lnk.ShellItems.ShellBag0X40(bytes, codepage: 1252).Value,
                0x4C => new Lnk.ShellItems.ShellBag0X4C(bytes).Value,
                0x61 => new Lnk.ShellItems.ShellBag0X61(bytes, codepage: 1252).Value,
                0x74 => new Lnk.ShellItems.ShellBag0X74(bytes, codepage: 1252).Value,
                _ => null,
            };
            if (!string.IsNullOrEmpty(value)) return value;
        }
        catch { /* parser threw on a malformed blob — fall through */ }

        // Fallback: pull out the longest run of printable Latin / UTF-16 from the blob so the
        // user sees *something* useful instead of an empty cell.
        return ExtractPrintable(bytes);
    }

    private static string? ExtractPrintable(byte[] bytes)
    {
        if (bytes.Length < 6) return null;
        var sb = new StringBuilder();
        // Try UTF-16LE first (most shell-items embed names this way).
        for (int i = 4; i + 1 < bytes.Length; i += 2)
        {
            var ch = (char)(bytes[i] | (bytes[i + 1] << 8));
            if (ch == 0) break;
            if (ch is >= (char)0x20 and <= (char)0x7E or >= (char)0xA1 and <= (char)0xFF)
            {
                sb.Append(ch);
            }
        }
        if (sb.Length >= 3) return sb.ToString();
        // Fall back to ASCII.
        sb.Clear();
        foreach (var b in bytes)
        {
            if (b is >= 0x20 and <= 0x7E) sb.Append((char)b);
            else if (sb.Length >= 4) break;
            else sb.Clear();
        }
        return sb.Length >= 4 ? sb.ToString() : null;
    }
}

// ============================================================ SRUM ==================

public sealed partial class SrumTool
{
    /// <summary>Rows read from each SRUM table. Each table has its own budget.</summary>
    internal const int PerTableBudget = 25_000;

    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
        Task.Run(() => Parse(evidencePath, ct), ct);

    // Well-known SRUM extension table GUIDs.
    private const string NetworkDataTable = "{973F5D5C-1D90-4944-BE8E-24B94231A174}";
    private const string AppResourceTable = "{D10CA2FE-6FCF-4F6D-848E-B2E99266FA89}";
    private const string EnergyEstTable = "{FEE4E14F-02A9-4550-B5CE-5FA2DA202E37}";

    private static ParseResult Parse(string path, CancellationToken ct)
    {
        var rows = new List<object>();
        var truncated = false;
        var problems = new List<string>();
        // Work on a copy in a fresh folder so the database engine never writes beside the evidence.
        var stagingDir = Path.Combine(Path.GetTempPath(), "cinder-srum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);
        var stagedDb = Path.Combine(stagingDir, Path.GetFileName(path));
        Microsoft.Database.Isam.IsamInstance? inst = null;
        try
        {
            File.Copy(path, stagedDb, overwrite: true);
            File.SetAttributes(stagedDb, FileAttributes.Normal);
            inst = new Microsoft.Database.Isam.IsamInstance(
                checkpointFileDirectoryPath: stagingDir,
                logfileDirectoryPath: stagingDir,
                temporaryDatabaseFileDirectoryPath: stagingDir,
                baseName: "edb",
                eventSource: "Cinder",
                readOnly: true,
                pageSize: 0);
            using var session = inst.CreateSession();
            session.AttachDatabase(stagedDb);
            using var db = session.OpenDatabase(stagedDb);

            // The ID → name map first (SruDbIdMapTable: IdType, IdIndex, IdBlob). IdBlob is a
            // UTF-16 string for application ids and a binary SID for user ids.
            var idMap = ReadSruDbIdMap(db, ct);

            truncated |= ReadTable(db, AppResourceTable, "application resource usage", rows, problems, ct, cur => new
            {
                Time = TryGetFiletime(cur, "TimeStamp"),
                Table = "app",
                User = ResolveId(cur, idMap, "UserId"),
                App = ResolveId(cur, idMap, "AppId"),
                Value = $"cpu_fg={TryGetLong(cur, "CpuForeground") ?? 0} ms · cpu_bg={TryGetLong(cur, "CpuBackground") ?? 0} ms · face_time={TryGetLong(cur, "FaceTime") ?? 0} ms",
            });
            truncated |= ReadTable(db, NetworkDataTable, "network data usage", rows, problems, ct, cur => new
            {
                Time = TryGetFiletime(cur, "TimeStamp"),
                Table = "net",
                User = ResolveId(cur, idMap, "UserId"),
                App = ResolveId(cur, idMap, "AppId"),
                Value = string.Create(CultureInfo.InvariantCulture, $"sent={TryGetLong(cur, "BytesSent") ?? 0:N0} B · recv={TryGetLong(cur, "BytesRecvd") ?? 0:N0} B"),
            });
            truncated |= ReadTable(db, EnergyEstTable, "energy estimate", rows, problems, ct, cur => new
            {
                Time = TryGetFiletime(cur, "TimeStamp"),
                Table = "energy",
                User = ResolveId(cur, idMap, "UserId"),
                App = ResolveId(cur, idMap, "AppId"),
                Value = $"designed_capacity={TryGetLong(cur, "DesignedCapacity") ?? 0}",
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var hint = ex.Message.Contains("dirty", StringComparison.OrdinalIgnoreCase)
                ? " The database was copied from a running system without a clean shutdown. Repair a copy with esentutl /p, then open the repaired copy."
                : !OperatingSystem.IsWindows() ? " SRUM uses the Windows database engine and can only be read on Windows." : "";
            throw new InvalidOperationException($"{Path.GetFileName(path)} could not be opened as a SRUM database: {ex.Message}.{hint}", ex);
        }
        finally
        {
            try { inst?.Dispose(); } catch { }
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
        }

        var note = problems.Count > 0
            ? $"Some tables could not be read completely: {string.Join("; ", problems)}"
            : rows.Count == 0 ? "This SRUM database has no rows in the application, network or energy tables." : null;
        return new ParseResult(rows, truncated, null, note);
    }

    /// <summary>
    /// Reads one table into <paramref name="rows"/> under its own budget. Returns true when the
    /// table holds more rows than the budget. A table that fails part-way is reported in
    /// <paramref name="problems"/> rather than passed off as complete.
    /// </summary>
    private static bool ReadTable(Microsoft.Database.Isam.IsamDatabase db, string table, string label,
        List<object> rows, List<string> problems, CancellationToken ct, Func<Microsoft.Database.Isam.Cursor, object> toRow)
    {
        if (!db.Exists(table))
        {
            return false;
        }
        try
        {
            using var cur = db.OpenCursor(table);
            cur.MoveBeforeFirst();
            return RowBudgets.Take(Records(cur, ct), PerTableBudget, rows, c =>
            {
                try
                {
                    return toRow(c);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return null; // one malformed record must not end the table
                }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problems.Add($"{label}: {ex.Message}");
            return false;
        }
    }

    private static IEnumerable<Microsoft.Database.Isam.Cursor> Records(Microsoft.Database.Isam.Cursor cur, CancellationToken ct)
    {
        while (cur.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            yield return cur;
        }
    }

    /// <summary>SruDbIdMapTable: maps small integer IDs to their string AppId or binary SID.</summary>
    private static Dictionary<int, string> ReadSruDbIdMap(Microsoft.Database.Isam.IsamDatabase db, CancellationToken ct)
    {
        var map = new Dictionary<int, string>();
        if (!db.Exists("SruDbIdMapTable"))
        {
            return map;
        }
        using var cur = db.OpenCursor("SruDbIdMapTable");
        cur.MoveBeforeFirst();
        while (cur.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var idType = TryGetInt(cur, "IdType");
                var idIndex = TryGetInt(cur, "IdIndex");
                var blob = cur.Record["IdBlob"] as byte[];
                if (idIndex is null || blob is null) continue;
                // Type 3 is a user SID; everything else is a UTF-16 application id.
                map[idIndex.Value] = idType is 3 ? TrySidString(blob) : Encoding.Unicode.GetString(blob).TrimEnd('\0');
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // malformed row — skip
            }
        }
        return map;
    }

    private static int? TryGetInt(Microsoft.Database.Isam.Cursor cur, string col)
    {
        try
        {
            return cur.Record[col] switch
            {
                int i => i,
                short s => s,
                byte b => b,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static long? TryGetLong(Microsoft.Database.Isam.Cursor cur, string col)
    {
        try
        {
            return cur.Record[col] switch
            {
                long l => l,
                int i => i,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string TryGetFiletime(Microsoft.Database.Isam.Cursor cur, string col)
    {
        try
        {
            var v = cur.Record[col];
            if (v is DateTime dt) return dt.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);
            if (v is long ft && ft > 0)
            {
                return DateTime.FromFileTimeUtc(ft).ToString("u", CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // unreadable or out-of-range value
        }
        return "";
    }

    private static string ResolveId(Microsoft.Database.Isam.Cursor cur,
        Dictionary<int, string> idMap, string column)
    {
        var idx = TryGetInt(cur, column);
        if (idx is null) return "";
        return idMap.GetValueOrDefault(idx.Value, $"id#{idx.Value}");
    }

    /// <summary>Format a binary SID as the canonical S-1-… string. Returns "" on malformed input.</summary>
    internal static string TrySidString(byte[] sid)
    {
        // SID layout: revision (1 byte) | subAuthCount (1) | authority (6 BE) | subAuths (4 each LE)
        if (sid.Length < 8) return "";
        int rev = sid[0];
        int count = sid[1];
        if (sid.Length < 8 + 4 * count) return "";
        long authority = ((long)sid[2] << 40) | ((long)sid[3] << 32) | ((long)sid[4] << 24) |
                         ((long)sid[5] << 16) | ((long)sid[6] << 8) | sid[7];
        var sb = new StringBuilder().Append("S-").Append(rev).Append('-').Append(authority);
        for (int i = 0; i < count; i++)
        {
            sb.Append('-').Append(BitConverter.ToUInt32(sid, 8 + 4 * i));
        }
        return sb.ToString();
    }
}

// ============================================================ NETWORK (PCAP) ========

public sealed partial class NetworkTool
{
    private const int RowBudget = 50_000;

    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
        Task.Run(() => Parse(evidencePath, ct), ct);

    private static ParseResult Parse(string path, CancellationToken ct)
    {
        var rows = new List<object>();
        string? note = null;
        try
        {
            using var reader = new CaptureFileReaderDevice(path);
            reader.Open(new DeviceConfiguration());
            int packetIndex = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (rows.Count >= RowBudget) break;

                var status = reader.GetNextPacket(out var capture);
                if (status != GetPacketStatus.PacketRead)
                {
                    // Anything that isn't a successful read ends the walk: an error status
                    // repeats indefinitely rather than advancing. But an error is not the end
                    // of the capture, and the examiner is told which one happened.
                    if (status != GetPacketStatus.NoRemainingPackets)
                    {
                        note = $"The capture could not be read past packet {packetIndex:N0} ({status}). It may be cut short or damaged; later packets are not shown.";
                    }
                    break;
                }
                packetIndex++;

                try
                {
                    var raw = capture.GetPacket();
                    var parsed = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
                    var ip = parsed.Extract<IPPacket>();
                    var tcp = parsed.Extract<TcpPacket>();
                    var udp = parsed.Extract<UdpPacket>();
                    var icmp = parsed.Extract<IcmpV4Packet>();
                    var proto = tcp is not null ? "TCP"
                              : udp is not null ? "UDP"
                              : icmp is not null ? "ICMP"
                              : ip?.Protocol.ToString() ?? "?";
                    rows.Add(new
                    {
                        Packet = packetIndex,
                        Time = raw.Timeval.Date.ToString("u", CultureInfo.InvariantCulture),
                        Protocol = proto,
                        Source = ip?.SourceAddress?.ToString() ?? "",
                        SourcePort = (int)(tcp?.SourcePort ?? udp?.SourcePort ?? 0),
                        Destination = ip?.DestinationAddress?.ToString() ?? "",
                        DestinationPort = (int)(tcp?.DestinationPort ?? udp?.DestinationPort ?? 0),
                        Bytes = raw.Data.Length,
                        TcpFlags = tcp is not null
                            ? $"{(tcp.Synchronize ? "S" : "")}{(tcp.Acknowledgment ? "A" : "")}{(tcp.Finished ? "F" : "")}{(tcp.Reset ? "R" : "")}{(tcp.Push ? "P" : "")}"
                            : "",
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A packet that cannot be decoded is skipped; the gap shows in the Packet column.
                }
            }
            reader.Close();
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        {
            throw new InvalidOperationException(
                "Reading packet captures needs the packet-capture library, which is not installed on this computer: " +
                "Npcap on Windows (npcap.com), libpcap on Linux and macOS. Install it, then open the capture again.", ex);
        }
        return ParseResult.WithBudget(rows, RowBudget, note);
    }
}

// ============================================================ LINUX ARTIFACTS =======

/// <summary>One login-accounting record from a Linux <c>wtmp</c> / <c>btmp</c> / <c>utmp</c> file.</summary>
internal readonly record struct UtmpRecord(string Type, int Pid, string Line, string User, string Host, DateTimeOffset TimeUtc);

public sealed partial class LinuxArtifactsTool
{
    private const int RowBudget = 50_000;

    /// <summary>The timestamp carried a UTC offset and has been converted to UTC.</summary>
    internal const string BasisUtc = "UTC";

    /// <summary>A full date and time with no zone: the log host's local clock.</summary>
    internal const string BasisLocal = "log's local time (zone not recorded)";

    /// <summary>Classic syslog: month, day and time only. The year is not in the log and is not guessed.</summary>
    internal const string BasisNoYear = "no year in log; log's local time";

    public override bool AcceptsFolder => true;

    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
        Task.Run(() => Parse(evidencePath, ct), ct);

    private static ParseResult Parse(string root, CancellationToken ct)
    {
        var rows = new List<object>();
        if (File.Exists(root))
        {
            // A single file: a failure to read it is the result, so it is thrown.
            ParseFile(root, Path.GetFileName(root), rows, ct);
            return ParseResult.WithBudget(rows, RowBudget);
        }

        // A mounted root or triage folder: walk the well-known artifact locations.
        var candidates = new List<string>
        {
            Path.Combine(root, "var", "log", "auth.log"),
            Path.Combine(root, "var", "log", "secure"),
            Path.Combine(root, "var", "log", "syslog"),
            Path.Combine(root, "var", "log", "messages"),
            Path.Combine(root, "var", "log", "wtmp"),
            Path.Combine(root, "var", "log", "btmp"),
            Path.Combine(root, "etc", "crontab"),
            Path.Combine(root, "etc", "passwd"),
            Path.Combine(root, "etc", "shadow"),
            Path.Combine(root, "etc", "ssh", "ssh_known_hosts"),
        };
        // Per-user shell history, root's included.
        var homes = new List<string>();
        var homeRoot = Path.Combine(root, "home");
        if (Directory.Exists(homeRoot))
        {
            homes.AddRange(Directory.EnumerateDirectories(homeRoot, "*", FolderScan.TopLevel));
        }
        homes.Add(Path.Combine(root, "root"));
        foreach (var home in homes)
        {
            foreach (var name in new[] { ".bash_history", ".zsh_history", ".fish_history", ".python_history" })
            {
                candidates.Add(Path.Combine(home, name));
            }
        }

        int files = 0, failed = 0;
        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= RowBudget) break;
            if (!File.Exists(file)) continue;
            files++;
            var display = FolderScan.Display(root, file);
            try
            {
                ParseFile(file, display, rows, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                rows.Add(Row(display, "error", "", "", "", $"Could not read: {ex.Message}"));
            }
        }
        if (files == 0)
        {
            throw new InvalidOperationException(
                "None of the Linux files this tool reads were found in this folder. Pick the root of a mounted " +
                "Linux filesystem or a triage folder that contains var/log, etc and home.");
        }
        var note = failed == 0 ? null : $"Read {files - failed:N0} of {files:N0} files. {failed:N0} could not be read; each has an \"error\" row with the reason.";
        return ParseResult.WithBudget(rows, RowBudget, note);
    }

    private static void ParseFile(string path, string display, List<object> rows, CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        var baseName = name.Split('.')[0].Length == 0 ? name : name.Split('.')[0];

        // Login accounting files are fixed-size binary records, not text.
        if (baseName is "wtmp" or "btmp" or "utmp")
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var category = baseName == "btmp" ? "failed login" : "login";
            foreach (var r in ReadUtmp(fs))
            {
                ct.ThrowIfCancellationRequested();
                if (rows.Count >= RowBudget) return;
                rows.Add(Row(display, category,
                    r.TimeUtc.ToString("u", CultureInfo.InvariantCulture), BasisUtc, r.User,
                    $"{r.Type} · terminal {(r.Line.Length > 0 ? r.Line : "-")} · from {(r.Host.Length > 0 ? r.Host : "-")} · pid {r.Pid}"));
            }
            return;
        }

        var cat = name switch
        {
            "auth.log" or "secure" => "auth",
            "syslog" or "messages" => "syslog",
            "crontab" => "cron",
            "passwd" => "passwd",
            "shadow" => "shadow",
            "ssh_known_hosts" => "ssh",
            ".bash_history" or ".zsh_history" or ".fish_history" or ".python_history" => "shell",
            _ => "other",
        };
        // Only log files carry a timestamp at the start of each line.
        var timestamped = cat is "auth" or "syslog" or "other";
        var who = cat == "shell" ? Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "") ?? "" : "";

        using var sr = new StreamReader(path);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= RowBudget) return;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var (time, basis) = timestamped ? ParseLogTimestamp(line) : ("", "");
            rows.Add(Row(display, cat, time, basis, who, line.Length > 512 ? line[..512] + "…" : line));
        }
    }

    private static object Row(string source, string category, string time, string timeBasis, string who, string what) => new
    {
        Source = source,
        Category = category,
        Time = time,
        TimeBasis = timeBasis,
        Who = who,
        What = what,
    };

    private static readonly string[] Months =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>
    /// Reads the timestamp at the start of a log line and says what kind of time it is.
    ///
    /// <para>An ISO 8601 stamp with an offset (<c>2024-05-01T12:34:56.123+02:00</c>) is converted
    /// to UTC. An ISO stamp without an offset is kept as written and labelled as the host's local
    /// time. A classic syslog stamp (<c>Nov  2 10:00:00</c>) has no year and no zone: it is shown
    /// as <c>11-02 10:00:00</c> and labelled as such. The year is never invented and the time is
    /// never shifted by this workstation's time zone.</para>
    /// </summary>
    internal static (string Time, string Basis) ParseLogTimestamp(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return ("", "");
        }

        // ISO 8601 at the start: take the token up to the first space.
        var space = line.IndexOf(' ');
        var token = space < 0 ? line : line[..space];
        if (token.Length >= 19 && char.IsAsciiDigit(token[0]) && token[4] == '-' && token[7] == '-' && token[10] == 'T')
        {
            if (HasUtcOffset(token))
            {
                if (DateTimeOffset.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
                {
                    return (withOffset.UtcDateTime.ToString("u", CultureInfo.InvariantCulture), BasisUtc);
                }
            }
            else if (DateTime.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                return (local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), BasisLocal);
            }
        }

        // Classic syslog: "Mmm dd HH:MM:SS" with the day space-padded ("Nov  2").
        if (line.Length >= 15 && line[3] == ' ' && line[6] == ' ')
        {
            var month = Array.FindIndex(Months, m => line.AsSpan(0, 3).Equals(m, StringComparison.Ordinal)) + 1;
            if (month > 0
                && int.TryParse(line.AsSpan(4, 2).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var day)
                && day is >= 1 and <= 31
                && TimeOnly.TryParseExact(line.AsSpan(7, 8), "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                return (string.Create(CultureInfo.InvariantCulture, $"{month:00}-{day:00} ") + time.ToString("HH:mm:ss", CultureInfo.InvariantCulture), BasisNoYear);
            }
        }
        return ("", "");
    }

    private static bool HasUtcOffset(string token)
    {
        if (token[^1] is 'Z' or 'z')
        {
            return true;
        }
        // The date part has hyphens of its own, so only a sign after the "T" is an offset.
        return token.AsSpan(11).IndexOfAny('+', '-') >= 0;
    }

    /// <summary>
    /// Reads Linux utmp-format records: 384 bytes each on x86-64 and most 64-bit systems
    /// (type, pid, terminal, id, user, host, exit status, session, seconds, microseconds,
    /// address). Empty slots are skipped. A file whose size is not a whole number of records is
    /// from a different architecture and is refused rather than misread.
    /// </summary>
    internal static List<UtmpRecord> ReadUtmp(Stream stream)
    {
        const int RecordSize = 384;
        var records = new List<UtmpRecord>();
        if (stream.Length % RecordSize != 0)
        {
            throw new InvalidOperationException(
                $"This login record file is {stream.Length:N0} bytes, which is not a whole number of 384-byte records. " +
                "It is from a system with a different record layout, which Cinder does not read.");
        }
        var buffer = new byte[RecordSize];
        while (stream.ReadAtLeast(buffer, RecordSize, throwOnEndOfStream: false) == RecordSize)
        {
            var type = BitConverter.ToInt16(buffer, 0);
            var seconds = BitConverter.ToInt32(buffer, 340);
            if (type == 0 && seconds == 0)
            {
                continue; // empty slot
            }
            records.Add(new UtmpRecord(
                type switch
                {
                    1 => "run level",
                    2 => "boot",
                    3 => "clock changed (new time)",
                    4 => "clock changed (old time)",
                    5 => "init process",
                    6 => "login prompt",
                    7 => "session start",
                    8 => "session end",
                    _ => $"type {type}",
                },
                BitConverter.ToInt32(buffer, 4),
                Text(buffer, 8, 32),
                Text(buffer, 44, 32),
                Text(buffer, 76, 256),
                DateTimeOffset.FromUnixTimeSeconds((uint)seconds)));
        }
        return records;

        static string Text(byte[] b, int offset, int length)
        {
            var end = Array.IndexOf(b, (byte)0, offset, length);
            return Encoding.UTF8.GetString(b, offset, (end < 0 ? offset + length : end) - offset);
        }
    }
}

// ============================================================ MOBILE BACKUPS ========

public sealed partial class MobileTool
{
    private const int RowBudget = 50_000;

    public override bool AcceptsFolder => true;

    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
        Task.Run(() => Parse(evidencePath, ct), ct);

    private static ParseResult Parse(string root, CancellationToken ct)
    {
        // Android adb backup: a single .ab file, detected by extension or by its first bytes.
        if (File.Exists(root))
        {
            if (Path.GetExtension(root).Equals(".ab", StringComparison.OrdinalIgnoreCase) || LooksLikeAdbBackup(root))
            {
                return ParseAdbBackup(root, ct);
            }
            throw new InvalidOperationException(
                "This file is not an Android backup (.ab). For an iPhone or iPad backup, pick the backup folder " +
                "that contains Manifest.db instead of a file inside it.");
        }

        var manifestPath = Path.Combine(root, "Manifest.db");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                "This folder has no Manifest.db, so it is not an iPhone or iPad backup folder. " +
                "Pick the folder named with the device identifier, inside the MobileSync Backup folder.");
        }

        var rows = new List<object>();
        bool truncated;
        try
        {
            // A private copy, so a running Finder or iTunes does not hold the file against us.
            using var staged = new StagedDatabase(manifestPath);
            using var conn = new SqliteConnection(staged.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Files: fileID (SHA-1 of domain-relativePath, also the stored file's name), domain,
            // relativePath, flags (1 file, 2 folder, 4 link).
            cmd.CommandText = """
                SELECT domain, relativePath, fileID, flags
                FROM Files
                ORDER BY domain, relativePath
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", RowBudget + 1);
            using var r = cmd.ExecuteReader();
            truncated = RowBudgets.Take(Records(r, ct), RowBudget, rows, rec => Row(
                rec.IsDBNull(0) ? "" : rec.GetString(0),
                (rec.IsDBNull(3) ? 0 : rec.GetInt64(3)) switch { 1 => "file", 2 => "folder", 4 => "link", var f => $"flags {f}" },
                rec.IsDBNull(1) ? "" : rec.GetString(1),
                "",
                null,
                rec.IsDBNull(2) ? "" : rec.GetString(2)));
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException(
                $"Manifest.db could not be read: {ex.Message}. If the backup is encrypted, Manifest.db is encrypted too, " +
                "and Cinder cannot open encrypted iPhone backups yet.", ex);
        }
        return new ParseResult(rows, truncated);
    }

    private static IEnumerable<SqliteDataReader> Records(SqliteDataReader reader, CancellationToken ct)
    {
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            yield return reader;
        }
    }

    private static object Row(string domain, string kind, string path, string modified, long? sizeBytes, string backupFileId) => new
    {
        Domain = domain,
        Kind = kind,
        Path = path,
        Modified = modified,
        SizeBytes = sizeBytes,
        BackupFileId = backupFileId,
    };

    /// <summary>
    /// Magic check for Android adb-backup files. The format starts with the literal ASCII
    /// header "ANDROID BACKUP" on its own line.
    /// </summary>
    private static bool LooksLikeAdbBackup(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[14];
            return fs.ReadAtLeast(head, 14, throwOnEndOfStream: false) == 14 && head.SequenceEqual("ANDROID BACKUP"u8);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lists an Android adb backup (.ab). The file's first 4 lines are a text header:
    ///   ANDROID BACKUP\n
    ///   &lt;version&gt;\n     (1, 2, 3, 4, or 5)
    ///   &lt;compression&gt;\n (0 = none, 1 = deflate)
    ///   &lt;encryption&gt;\n  ("none" or "AES-256")
    /// Followed by the payload — a TAR archive that, if compression=1, is a zlib stream.
    /// </summary>
    private static ParseResult ParseAdbBackup(string path, CancellationToken ct)
    {
        var rows = new List<object>();
        using var fs = File.OpenRead(path);
        // Read header (up to 256 bytes — generous; 4 short lines).
        var headerBytes = new byte[256];
        var n = fs.ReadAtLeast(headerBytes, headerBytes.Length, throwOnEndOfStream: false);
        var lines = Encoding.ASCII.GetString(headerBytes, 0, n).Split('\n');
        if (lines.Length < 5 || !lines[0].StartsWith("ANDROID BACKUP", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This file does not start with a complete Android backup header, so it cannot be read as an .ab backup.");
        }
        var compression = lines[2].Trim();
        var encryption = lines[3].Trim();
        if (!string.Equals(encryption, "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"This Android backup is encrypted ({encryption}). Cinder cannot open encrypted backups yet.");
        }
        // The payload starts after the 4th newline.
        int payloadOffset = 0, newlines = 0;
        for (int i = 0; i < n; i++)
        {
            if (headerBytes[i] == (byte)'\n' && ++newlines == 4)
            {
                payloadOffset = i + 1;
                break;
            }
        }
        fs.Seek(payloadOffset, SeekOrigin.Begin);

        Stream tarStream = fs;
        if (compression == "1")
        {
            tarStream = new System.IO.Compression.ZLibStream(fs, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
        }
        try
        {
            using var tarReader = SharpCompress.Readers.Tar.TarReader.OpenReader(tarStream, new SharpCompress.Readers.ReaderOptions());
            while (tarReader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (rows.Count >= RowBudget) break;
                var entry = tarReader.Entry;
                if (entry.IsDirectory) continue;
                // Android tar layout: apps/<package>/…, shared/0/<paths>
                var key = entry.Key ?? "(unnamed)";
                rows.Add(Row(
                    AdbDomain(key),
                    "file",
                    key,
                    entry.LastModifiedTime?.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture) ?? "",
                    entry.Size,
                    ""));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"The backup's contents could not be listed after {rows.Count:N0} entries: {ex.Message}. The file may be cut short or damaged.", ex);
        }
        return ParseResult.WithBudget(rows, RowBudget);
    }

    /// <summary>The app package (or "shared") an entry of an adb backup belongs to, from its path.</summary>
    internal static string AdbDomain(string entryPath)
    {
        const string Apps = "apps/";
        if (entryPath.StartsWith(Apps, StringComparison.Ordinal))
        {
            var slash = entryPath.IndexOf('/', Apps.Length);
            return slash > Apps.Length ? entryPath[Apps.Length..slash] : entryPath[Apps.Length..];
        }
        return entryPath.StartsWith("shared/", StringComparison.Ordinal) ? "shared" : "";
    }
}

// ============================================================ RECYCLE BIN =========
// Windows $I metadata files. Format:
//   v1 (Vista / 7): version(8) | original size(8) | deletion FILETIME(8) | UTF-16 path[260 chars]
//   v2 (Win10+):    version(8) | original size(8) | deletion FILETIME(8) | path length in chars(4) | UTF-16 path
// The companion $R file (or directory) carries the deleted content.

public sealed partial class RecycleBinTool
{
    public override bool AcceptsFolder => true;

    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) =>
        Task.Run(() => Parse(evidencePath, ct), ct);

    private static ParseResult Parse(string path, CancellationToken ct)
    {
        var rows = new List<object>();
        if (File.Exists(path))
        {
            if (!Path.GetFileName(path).StartsWith("$I", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Recycle Bin reads the $I files that record each deletion. Pick a $I file, a $Recycle.Bin folder, or a user's folder inside it.");
            }
            rows.Add(ReadEntry(path, owner: ""));
            return new ParseResult(rows);
        }

        // $Recycle.Bin holds one folder per user, named by SID. Anything else is treated as a
        // folder that holds $I files directly.
        var bins = Directory.EnumerateDirectories(path, "S-1-*", FolderScan.TopLevel)
            .Select(d => (Owner: Path.GetFileName(d), Folder: d))
            .ToList();
        if (bins.Count == 0)
        {
            bins.Add((Owner: Path.GetFileName(path).StartsWith("S-1-", StringComparison.Ordinal) ? Path.GetFileName(path) : "", Folder: path));
        }

        int files = 0, failed = 0;
        foreach (var (owner, folder) in bins)
        {
            foreach (var f in Directory.EnumerateFiles(folder, "$I*", FolderScan.TopLevel))
            {
                ct.ThrowIfCancellationRequested();
                files++;
                try
                {
                    rows.Add(ReadEntry(f, owner));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    rows.Add(Row(owner, "", null, "", "", Path.GetFileName(f), $"Could not read: {ex.Message}"));
                }
            }
        }
        var note = files == 0
            ? "No $I files in this folder: nothing is recorded as deleted here."
            : FolderScan.Note(files, failed);
        return new ParseResult(rows, false, null, note);
    }

    private static object ReadEntry(string iFile, string owner)
    {
        var (originalPath, originalSize, deleted) = DecodeIFile(File.ReadAllBytes(iFile));

        // The deleted content sits beside the $I file under the same name with $R.
        var rPath = Path.Combine(Path.GetDirectoryName(iFile) ?? "", "$R" + Path.GetFileName(iFile)[2..]);
        var content = File.Exists(rPath) ? string.Create(CultureInfo.InvariantCulture, $"file, {new FileInfo(rPath).Length:N0} bytes")
            : Directory.Exists(rPath) ? "folder"
            : "missing (the deleted content is not in this folder)";

        return Row(owner, originalPath, originalSize,
            deleted?.ToString("u", CultureInfo.InvariantCulture) ?? "", content, Path.GetFileName(iFile), "");
    }

    /// <summary>
    /// Decodes a $I file: where the item was deleted from, how big it was, and when. Throws
    /// when the file is too short to be a $I record.
    /// </summary>
    internal static (string OriginalPath, long OriginalSize, DateTimeOffset? DeletedUtc) DecodeIFile(byte[] data)
    {
        if (data.Length < 24)
        {
            throw new InvalidOperationException($"The $I file is only {data.Length} bytes; a deletion record is at least 24.");
        }
        var version = BitConverter.ToInt64(data, 0);
        var size = BitConverter.ToInt64(data, 8);
        var fileTime = BitConverter.ToInt64(data, 16);

        string path;
        if (version == 2)
        {
            if (data.Length < 28)
            {
                throw new InvalidOperationException("The $I file ends before its path length field.");
            }
            long byteCount = Math.Max(0, BitConverter.ToInt32(data, 24)) * 2L;
            byteCount = Math.Min(byteCount, data.Length - 28);
            path = Encoding.Unicode.GetString(data, 28, (int)byteCount);
        }
        else
        {
            // Version 1: a fixed 520-byte UTF-16 field starting at offset 24.
            path = Encoding.Unicode.GetString(data, 24, Math.Min(data.Length, 24 + 520) - 24);
        }
        var nul = path.IndexOf('\0');
        if (nul >= 0)
        {
            path = path[..nul];
        }

        DateTimeOffset? deleted = null;
        if (fileTime > 0)
        {
            try
            {
                deleted = DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                // Not a valid FILETIME: leave the time blank rather than fail the row.
            }
        }
        return (path, size, deleted);
    }

    private static object Row(string owner, string originalPath, long? originalSize, string deleted,
                              string content, string recordFile, string note) => new
                              {
                                  Owner = owner,
                                  OriginalPath = originalPath,
                                  OriginalSize = originalSize,
                                  Deleted = deleted,
                                  Content = content,
                                  RecordFile = recordFile,
                                  Note = note,
                              };
}
