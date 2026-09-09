// =====================================================================================
// Real in-process parser wirings for Phase 3 (Filesystem / Carver) and beyond.
//
// Each tool here used to be a sidecar stub. We now bind every common case to a real
// in-process implementation via:
//
//   DiscUtils.{Ntfs,Fat,Iso9660,Ext,Vhd,Vhdx} → file-system parsing
//   SharpPcap + PacketDotNet                  → PCAP / PCAPNG parsing
//   Microsoft.Database.Isam                   → ESE database (SRUM SRUDB.dat)
//   Registry (Eric Zimmerman)                 → Shellbags via BagMRU walking
//   Microsoft.Data.Sqlite                     → iOS backup Manifest.db
//   plain text I/O                            → Linux artifact parsers
// =====================================================================================

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Cinder.Imaging.Ewf;
using Cinder.Search;
using Cinder.Imaging;
using Cinder.Filesystems;
using Cinder.App.Services;
using DiscUtils;
using DiscUtils.Ext;
using DiscUtils.Fat;
using DiscUtils.Iso9660;
using DiscUtils.Ntfs;
using DiscUtils.Streams;
using Microsoft.Data.Sqlite;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Cinder.App.ViewModels.Tools;

// ============================================================ FILESYSTEM ============

public sealed partial class FilesystemTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var settings = new SettingsStore().Load();
        var (rows, truncated) = await Task.Run(() => Parse(evidencePath, settings, ct), ct);
        AddRows(rows, budget: int.MaxValue);
        IsTruncated = truncated;
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
                // reads the row as a checked result; Imaging ▸ Verify re-hashes and compares.
                rows.Add(MetadataRow("[EWF metadata]", "E01", ewf.MediaSize, ewf.AcquisitionDate ?? "",
                    $"EWF media_size={ewf.MediaSize:N0} bytes · sectors={ewf.NumberOfSectors:N0} · " +
                    $"segments={ewf.SegmentCount} · recorded (UNVERIFIED) MD5={ewf.RecordedMd5 ?? "none"} · " +
                    $"SHA1={ewf.RecordedSha1 ?? "none"} — run Imaging ▸ Verify image to check them"));
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
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                result = DiscUtilsWalker.Walk(stream, options, ct);
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
                rows.Add(MetadataRow("[deleted]", "…", 0, "", "Deleted-entry listing TRUNCATED — the volume has more not-in-use MFT records."));
            }
            if (options.HashFiles)
            {
                rows.Add(MetadataRow("[hashing]", "summary", result.HashedBytes, "",
                    $"Hashed {result.HashedFiles:N0} files ({result.HashedBytes:N0} bytes), skipped {result.HashSkipped:N0}" +
                    (hashSets is null ? " · no hash-set database configured, so verdicts are absent" : $" · verdicts from {Path.GetFileName(settings.HashSetDatabase)}")));
            }
            if (rows.Count == 0 || (rows.Count == 1 && result.Entries.Count == 0))
            {
                rows.Add(MetadataRow("(no known filesystem detected)", "", new FileInfo(path).Length, "",
                    "DiscUtils could not identify NTFS/FAT/ISO9660/ext on this image."));
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

        return Row(e.Inode, e.Path, e.Name, e.Size, e.IsDirectory, e.IsDeleted, verdict, Get("sha1"),
                   Fmt(e.ModifiedUtc), Fmt(e.CreatedUtc), Fmt(e.AccessedUtc), e.Owner ?? "", note);
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

    private static string Fmt(DateTimeOffset? t) =>
        t is { } v ? v.ToString("u", CultureInfo.InvariantCulture) : "";
}

// ============================================================ SHELLBAGS ============

public sealed partial class ShellbagsTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        AddRows(rows, budget: 25_000);
    }

    private static List<object> Parse(string path, CancellationToken ct)
    {
        var hive = new global::Registry.RegistryHive(path);
        hive.ParseHive();
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
        foreach (var rootPath in roots)
        {
            ct.ThrowIfCancellationRequested();
            var key = hive.GetKey(rootPath);
            if (key is null) continue;
            WalkBagMru(key, parentPath: "", rows, ct);
        }
        return rows;
    }

    private static void WalkBagMru(global::Registry.Abstractions.RegistryKey key,
                                    string parentPath, List<object> rows, CancellationToken ct)
    {
        if (rows.Count >= 25_000) return;

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
            EntryCount = key.SubKeys.Count,
            LastWrite = key.LastWriteTime?.ToString("u", CultureInfo.InvariantCulture) ?? "",
            HiveKey = key.KeyPath,
        });
        foreach (var sub in key.SubKeys)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= 25_000) return;
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
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
    }

    // Well-known SRUM extension table GUIDs.
    private const string NetworkDataTable = "{973F5D5C-1D90-4944-BE8E-24B94231A174}";
    private const string AppResourceTable = "{D10CA2FE-6FCF-4F6D-848E-B2E99266FA89}";
    private const string NetworkConnTable = "{DD6636C4-8929-4683-974E-22C046A43763}";
    private const string EnergyEstTable = "{FEE4E14F-02A9-4550-B5CE-5FA2DA202E37}";

    private static List<object> Parse(string path, CancellationToken ct)
    {
        var rows = new List<object>();
        // Stage SRUDB.dat into a fresh working directory so ESE can replay its logs without
        // polluting the source folder.
        var stagingDir = Path.Combine(Path.GetTempPath(), "cinder-srum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);
        var stagedDb = Path.Combine(stagingDir, Path.GetFileName(path));
        Microsoft.Database.Isam.IsamInstance? inst = null;
        try
        {
            File.Copy(path, stagedDb, overwrite: true);
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

            // Build the ID → name map first (table is SruDbIdMapTable; columns IdType, IdIndex,
            // IdBlob). IdBlob is a UTF-16 string for AppIds and a binary SID for UserIds.
            var idMap = ReadSruDbIdMap(db, ct);

            // Stream rows from each well-known table. Each table has its own column set; we
            // pick the most useful columns and label them.
            ReadAppResourceTable(db, idMap, rows, ct);
            ReadNetworkDataTable(db, idMap, rows, ct);
            ReadEnergyEstTable(db, idMap, rows, ct);

            if (rows.Count == 0)
            {
                rows.Add(new
                {
                    Time = "",
                    Source = "(empty)",
                    User = "",
                    AppOrUser = "",
                    Value = "",
                    Note = "SRUDB.dat opened but no rows in the known SRUM extension tables.",
                });
            }
        }
        catch (Exception ex)
        {
            rows.Add(new
            {
                Time = "",
                Source = "(error)",
                User = "",
                AppOrUser = "",
                Value = "",
                Note = $"Could not open SRUDB.dat: {ex.Message}",
            });
        }
        finally
        {
            try { inst?.Dispose(); } catch { }
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
        }
        return rows;
    }

    /// <summary>SruDbIdMapTable: maps small integer IDs to their string AppId or binary SID.</summary>
    private static Dictionary<int, string> ReadSruDbIdMap(Microsoft.Database.Isam.IsamDatabase db, CancellationToken ct)
    {
        var map = new Dictionary<int, string>();
        try
        {
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
                    if (idIndex is null) continue;
                    string label;
                    if (idType is 3 && blob is not null)
                    {
                        // User SID — render as S-1-…
                        label = TrySidString(blob);
                    }
                    else if (blob is not null)
                    {
                        // App ID — UTF-16, often with a trailing nul.
                        var s = System.Text.Encoding.Unicode.GetString(blob).TrimEnd('\0');
                        label = s;
                    }
                    else continue;
                    map[idIndex.Value] = label;
                }
                catch { /* malformed row — skip */ }
            }
        }
        catch { /* table missing — skip */ }
        return map;
    }

    private static void ReadAppResourceTable(Microsoft.Database.Isam.IsamDatabase db,
        Dictionary<int, string> idMap, List<object> rows, CancellationToken ct)
    {
        if (!db.Exists(AppResourceTable)) return;
        try
        {
            using var cur = db.OpenCursor(AppResourceTable);
            cur.MoveBeforeFirst();
            while (cur.MoveNext() && rows.Count < 25_000)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var ts = TryGetFiletime(cur, "TimeStamp");
                    var appId = ResolveId(cur, idMap, "AppId");
                    var userId = ResolveId(cur, idMap, "UserId");
                    var cpuActive = TryGetLong(cur, "CpuForeground") ?? 0;
                    var cpuBackground = TryGetLong(cur, "CpuBackground") ?? 0;
                    var faceTime = TryGetLong(cur, "FaceTime") ?? 0;
                    rows.Add(new
                    {
                        Time = ts,
                        Source = "app",
                        User = userId,
                        AppOrUser = appId,
                        Value = $"cpu_fg={cpuActive} ms · cpu_bg={cpuBackground} ms · face_time={faceTime} ms",
                        Note = "",
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    private static void ReadNetworkDataTable(Microsoft.Database.Isam.IsamDatabase db,
        Dictionary<int, string> idMap, List<object> rows, CancellationToken ct)
    {
        if (!db.Exists(NetworkDataTable)) return;
        try
        {
            using var cur = db.OpenCursor(NetworkDataTable);
            cur.MoveBeforeFirst();
            while (cur.MoveNext() && rows.Count < 25_000)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var ts = TryGetFiletime(cur, "TimeStamp");
                    var appId = ResolveId(cur, idMap, "AppId");
                    var userId = ResolveId(cur, idMap, "UserId");
                    var sent = TryGetLong(cur, "BytesSent") ?? 0;
                    var recv = TryGetLong(cur, "BytesRecvd") ?? 0;
                    rows.Add(new
                    {
                        Time = ts,
                        Source = "net",
                        User = userId,
                        AppOrUser = appId,
                        Value = $"sent={sent:N0} B · recv={recv:N0} B",
                        Note = "",
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    private static void ReadEnergyEstTable(Microsoft.Database.Isam.IsamDatabase db,
        Dictionary<int, string> idMap, List<object> rows, CancellationToken ct)
    {
        if (!db.Exists(EnergyEstTable)) return;
        try
        {
            using var cur = db.OpenCursor(EnergyEstTable);
            cur.MoveBeforeFirst();
            while (cur.MoveNext() && rows.Count < 25_000)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var ts = TryGetFiletime(cur, "TimeStamp");
                    var appId = ResolveId(cur, idMap, "AppId");
                    var userId = ResolveId(cur, idMap, "UserId");
                    var energy = TryGetLong(cur, "DesignedCapacity") ?? 0;
                    rows.Add(new
                    {
                        Time = ts,
                        Source = "energy",
                        User = userId,
                        AppOrUser = appId,
                        Value = $"energy={energy}",
                        Note = "",
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    private static int? TryGetInt(Microsoft.Database.Isam.Cursor cur, string col)
    {
        try
        {
            var v = cur.Record[col];
            return v switch
            {
                int i => i,
                short s => s,
                byte b => b,
                _ => null,
            };
        }
        catch { return null; }
    }

    private static long? TryGetLong(Microsoft.Database.Isam.Cursor cur, string col)
    {
        try
        {
            var v = cur.Record[col];
            return v switch
            {
                long l => l,
                int i => i,
                _ => null,
            };
        }
        catch { return null; }
    }

    private static string TryGetFiletime(Microsoft.Database.Isam.Cursor cur, string col)
    {
        try
        {
            var v = cur.Record[col];
            if (v is DateTime dt) return dt.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);
            if (v is long ft && ft > 0)
            {
                try { return DateTime.FromFileTimeUtc(ft).ToString("u", CultureInfo.InvariantCulture); }
                catch { return ""; }
            }
        }
        catch { }
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
    private static string TrySidString(byte[] sid)
    {
        try
        {
            // SID layout: revision (1 byte) | subAuthCount (1) | authority (6 BE) | subAuths (4 each LE)
            if (sid.Length < 8) return "";
            int rev = sid[0];
            int count = sid[1];
            if (sid.Length < 8 + 4 * count) return "";
            long authority = ((long)sid[2] << 40) | ((long)sid[3] << 32) | ((long)sid[4] << 24) |
                             ((long)sid[5] << 16) | ((long)sid[6] << 8) | sid[7];
            var sb = new System.Text.StringBuilder().Append("S-").Append(rev).Append('-').Append(authority);
            for (int i = 0; i < count; i++)
            {
                uint sub = BitConverter.ToUInt32(sid, 8 + 4 * i);
                sb.Append('-').Append(sub);
            }
            return sb.ToString();
        }
        catch { return ""; }
    }
}

// ============================================================ NETWORK (PCAP) ========

public sealed partial class NetworkTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        AddRows(rows, budget: 50_000);
    }

    private static List<object> Parse(string path, CancellationToken ct)
    {
        var rows = new List<object>();
        using var reader = new CaptureFileReaderDevice(path);
        reader.Open(new DeviceConfiguration());
        int packetIndex = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= 50_000) break;

            var status = reader.GetNextPacket(out var capture);
            if (status != GetPacketStatus.PacketRead)
            {
                // Anything that isn't a successful read ends the walk. `continue` here would
                // spin forever on a truncated or corrupt capture, because an error status
                // repeats indefinitely rather than advancing to NoRemainingPackets.
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
                    Idx = packetIndex,
                    Time = raw.Timeval.Date.ToString("u", CultureInfo.InvariantCulture),
                    Proto = proto,
                    Src = ip?.SourceAddress?.ToString() ?? "",
                    SrcPort = tcp?.SourcePort ?? udp?.SourcePort ?? 0,
                    Dst = ip?.DestinationAddress?.ToString() ?? "",
                    DstPort = tcp?.DestinationPort ?? udp?.DestinationPort ?? 0,
                    Bytes = raw.Data.Length,
                    Note = tcp is not null
                        ? $"flags={(tcp.Synchronize ? "S" : "")}{(tcp.Acknowledgment ? "A" : "")}{(tcp.Finished ? "F" : "")}{(tcp.Reset ? "R" : "")}{(tcp.Push ? "P" : "")}"
                        : "",
                });
            }
            catch
            {
                // skip malformed packet
            }
        }
        reader.Close();
        return rows;
    }
}

// ============================================================ LINUX ARTIFACTS =======

public sealed partial class LinuxArtifactsTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        AddRows(rows, budget: 50_000);
    }

    private static List<object> Parse(string root, CancellationToken ct)
    {
        var rows = new List<object>();
        if (File.Exists(root))
        {
            // Single file — just parse it.
            ParseFile(root, rows, ct);
            return rows;
        }
        if (!Directory.Exists(root))
        {
            rows.Add(new { Source = "", Category = "error", When = "", Who = "", What = $"Not a file or directory: {root}" });
            return rows;
        }

        // Triage directory layout: walk the well-known artifact locations.
        var artifacts = new (string Path, string Cat)[]
        {
            (Path.Combine(root, "var", "log", "auth.log"), "auth.log"),
            (Path.Combine(root, "var", "log", "secure"),   "auth.log"),
            (Path.Combine(root, "var", "log", "syslog"),   "syslog"),
            (Path.Combine(root, "var", "log", "messages"), "syslog"),
            (Path.Combine(root, "var", "log", "wtmp"),     "wtmp"),
            (Path.Combine(root, "etc", "crontab"),         "crontab"),
            (Path.Combine(root, "etc", "passwd"),          "passwd"),
            (Path.Combine(root, "etc", "shadow"),          "shadow"),
            (Path.Combine(root, "etc", "ssh", "ssh_known_hosts"), "ssh.known_hosts"),
        };
        foreach (var (p, _) in artifacts)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(p))
            {
                ParseFile(p, rows, ct);
            }
        }
        // Per-user shell history.
        var homeRoot = Path.Combine(root, "home");
        if (Directory.Exists(homeRoot))
        {
            foreach (var user in Directory.EnumerateDirectories(homeRoot))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var name in new[] { ".bash_history", ".zsh_history", ".fish_history", ".python_history" })
                {
                    var f = Path.Combine(user, name);
                    if (File.Exists(f)) ParseFile(f, rows, ct);
                }
            }
        }
        if (rows.Count == 0)
        {
            rows.Add(new
            {
                Source = root,
                Category = "info",
                When = "",
                Who = "",
                What = "No well-known Linux artifacts found. Point at a mounted Linux root or a triage folder containing /etc, /home, /var/log.",
            });
        }
        return rows;
    }

    private static void ParseFile(string path, List<object> rows, CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        var category = name switch
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
        var who = path.Contains("/home/") || path.Contains("\\home\\")
            ? Path.GetFileName(Path.GetDirectoryName(path)) ?? ""
            : "";
        try
        {
            using var sr = new StreamReader(path);
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                if (rows.Count >= 50_000) return;
                if (string.IsNullOrWhiteSpace(line)) continue;
                rows.Add(new
                {
                    Source = name,
                    Category = category,
                    When = ParseSyslogTimestamp(line),
                    Who = who,
                    What = line.Length > 512 ? line[..512] + "…" : line,
                });
            }
        }
        catch (Exception ex)
        {
            rows.Add(new
            {
                Source = name,
                Category = "error",
                When = "",
                Who = "",
                What = $"Read failed: {ex.Message}",
            });
        }
    }

    private static string ParseSyslogTimestamp(string line)
    {
        // Modern syslog/journalctl: ISO-8601 at the start.
        if (line.Length >= 19 && DateTimeOffset.TryParse(line.AsSpan(0, Math.Min(35, line.Length)), out var iso))
        {
            return iso.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);
        }
        // Classic syslog: "Mmm dd HH:MM:SS" (no year).
        if (line.Length >= 15 && DateTime.TryParseExact(line[..15],
                "MMM d HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var legacy))
        {
            // Year is the current year unless that lands in the future, in which case last year.
            var stamped = new DateTime(DateTime.UtcNow.Year, legacy.Month, legacy.Day, legacy.Hour, legacy.Minute, legacy.Second, DateTimeKind.Utc);
            if (stamped > DateTime.UtcNow) stamped = stamped.AddYears(-1);
            return stamped.ToString("u", CultureInfo.InvariantCulture);
        }
        return "";
    }
}

// ============================================================ MOBILE (iOS backup) ===

public sealed partial class MobileTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        AddRows(rows, budget: 50_000);
    }

    private static List<object> Parse(string root, CancellationToken ct)
    {
        var rows = new List<object>();
        // Android adb backup — single .ab file. Detect by extension or magic header.
        if (File.Exists(root))
        {
            var ext = Path.GetExtension(root).ToLowerInvariant();
            if (ext == ".ab" || LooksLikeAdbBackup(root))
            {
                ParseAdbBackup(root, rows, ct);
                return rows;
            }
            rows.Add(new { App = "info", Kind = "", Source = "", When = "", What = "For iOS, point Mobile at the backup folder (containing Manifest.db). For Android, pick a .ab adb backup file." });
            return rows;
        }
        if (!Directory.Exists(root))
        {
            rows.Add(new { App = "error", Kind = "", Source = "", When = "", What = $"Not a directory: {root}" });
            return rows;
        }

        var manifestPath = Path.Combine(root, "Manifest.db");
        if (!File.Exists(manifestPath))
        {
            rows.Add(new { App = "info", Kind = "", Source = "", When = "", What = "No Manifest.db here. This isn't an iOS backup, or the backup is encrypted and needs the user's iTunes backup password to decrypt before browsing." });
            return rows;
        }

        // Stage so a running iTunes doesn't fight us for the file.
        var staging = Path.Combine(Path.GetTempPath(), $"cinder-mobile-{Guid.NewGuid():N}.sqlite");
        try
        {
            File.Copy(manifestPath, staging, overwrite: true);
            var cs = new SqliteConnectionStringBuilder { DataSource = staging, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Files table on iOS Manifest.db:
            //   fileID (sha1 of the backed-up path), domain, relativePath, flags, file (binary plist)
            cmd.CommandText = """
                SELECT domain, relativePath, fileID, flags
                FROM Files
                ORDER BY domain, relativePath
                LIMIT 50000;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(new
                {
                    App = r.GetString(0),
                    Kind = (r.IsDBNull(3) ? 0 : r.GetInt64(3)) == 1 ? "file" : "dir",
                    Source = r.IsDBNull(1) ? "" : r.GetString(1),
                    When = "",
                    What = r.IsDBNull(2) ? "" : r.GetString(2),
                });
            }
        }
        catch (Exception ex)
        {
            rows.Add(new { App = "error", Kind = "", Source = "", When = "", What = $"Manifest.db read failed: {ex.Message}" });
        }
        finally
        {
            try { File.Delete(staging); } catch { }
        }
        return rows;
    }

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
            return fs.Read(head) == 14 &&
                   head[0] == (byte)'A' && head[1] == (byte)'N' && head[2] == (byte)'D' && head[3] == (byte)'R' &&
                   head[4] == (byte)'O' && head[5] == (byte)'I' && head[6] == (byte)'D' && head[7] == (byte)' ' &&
                   head[8] == (byte)'B' && head[9] == (byte)'A' && head[10] == (byte)'C' && head[11] == (byte)'K' &&
                   head[12] == (byte)'U' && head[13] == (byte)'P';
        }
        catch { return false; }
    }

    /// <summary>
    /// Parses an Android adb backup (.ab). The file's first 4 lines are a text header:
    ///   ANDROID BACKUP\n
    ///   &lt;version&gt;\n     (1, 2, 3, 4, or 5)
    ///   &lt;compression&gt;\n (0 = none, 1 = deflate)
    ///   &lt;encryption&gt;\n  ("none" or "AES-256")
    /// Followed by the raw payload — a TAR archive that, if compression=1, is deflate-wrapped.
    /// Encrypted backups need the user's passphrase to derive the AES key — we don't support
    /// that path yet and surface a clear "encrypted, can't read" row.
    /// </summary>
    private static void ParseAdbBackup(string path, List<object> rows, CancellationToken ct)
    {
        try
        {
            using var fs = File.OpenRead(path);
            // Read header (up to ~256 bytes — generous; 4 short lines).
            var headerBytes = new byte[256];
            var n = fs.Read(headerBytes);
            var headerText = System.Text.Encoding.ASCII.GetString(headerBytes, 0, n);
            var lines = headerText.Split('\n');
            if (lines.Length < 4 || !lines[0].StartsWith("ANDROID BACKUP", StringComparison.Ordinal))
            {
                rows.Add(new { App = "error", Kind = "", Source = Path.GetFileName(path), When = "", What = "File starts with the ADB-backup magic but the header is malformed." });
                return;
            }
            // version = lines[1], compression = lines[2], encryption = lines[3]
            var compression = lines[2].Trim();
            var encryption = lines[3].Trim();
            if (!string.Equals(encryption, "none", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(new
                {
                    App = "info",
                    Kind = "encrypted",
                    Source = Path.GetFileName(path),
                    When = "",
                    What = $"Encrypted adb backup ({encryption}) — Cinder v0.1 needs the user's adb backup password to decrypt before browsing. Tracked.",
                });
                return;
            }
            // Find the start of the payload — after the 4th newline.
            int payloadOffset = 0, newlines = 0;
            for (int i = 0; i < n; i++)
            {
                if (headerBytes[i] == (byte)'\n') { newlines++; if (newlines == 4) { payloadOffset = i + 1; break; } }
            }
            if (payloadOffset == 0)
            {
                rows.Add(new { App = "error", Kind = "", Source = Path.GetFileName(path), When = "", What = "Could not find end of adb-backup header." });
                return;
            }
            fs.Seek(payloadOffset, SeekOrigin.Begin);

            // The payload is a raw zlib stream (deflate with a 2-byte zlib header) when
            // compression=1, otherwise a plain tar. We pipe through SharpCompress.
            Stream tarStream = fs;
            if (compression == "1")
            {
                // Skip the 2-byte zlib header (0x78 0x9C / 0x78 0xDA) so DeflateStream sees raw deflate.
                fs.ReadByte();
                fs.ReadByte();
                tarStream = new System.IO.Compression.DeflateStream(fs, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
            }
            using var tarReader = SharpCompress.Readers.Tar.TarReader.OpenReader(tarStream, new SharpCompress.Readers.ReaderOptions());
            while (tarReader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (rows.Count >= 50_000) break;
                var entry = tarReader.Entry;
                if (entry.IsDirectory) continue;
                // Android tar layout: apps/<pkg>/_manifest, apps/<pkg>/sp/_sharedprefs, apps/<pkg>/db/<db>, shared/0/<paths>
                var key = entry.Key ?? "(unnamed)";
                var pkg = "";
                var slash = key.IndexOf('/', "apps/".Length);
                if (key.StartsWith("apps/", StringComparison.Ordinal) && slash > 0)
                {
                    pkg = key["apps/".Length..slash];
                }
                else if (key.StartsWith("shared/", StringComparison.Ordinal))
                {
                    pkg = "shared";
                }
                rows.Add(new
                {
                    App = pkg,
                    Kind = "file",
                    Source = key,
                    When = entry.LastModifiedTime?.ToUniversalTime().ToString("u", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                    What = $"{entry.Size:N0} bytes",
                });
            }
        }
        catch (Exception ex)
        {
            rows.Add(new { App = "error", Kind = "", Source = Path.GetFileName(path), When = "", What = $"adb backup read failed: {ex.Message}" });
        }
    }
}

// ============================================================ RECYCLE BIN =========
// Decode Windows $I metadata files. Format:
//   v1 (Vista / 7): hdr_ver(8) | orig_size(8) | del_time FILETIME(8) | unicode_path[260]
//   v2 (Win10+):    hdr_ver(8) | orig_size(8) | del_time FILETIME(8) | name_len_chars(4) | unicode_path[*]
// The companion $R file (or directory) carries the actual deleted bytes.
// We accept either a $Recycle.Bin directory (walks every <SID> subdir) or any folder that
// directly contains $I* files.

public sealed partial class RecycleBinTool
{
    protected override async Task LoadAsync(string evidencePath, CancellationToken ct)
    {
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        foreach (var r in rows) Rows.Add(r);
    }

    private static List<object> Parse(string path, CancellationToken ct)
    {
        var rows = new List<object>();
        if (File.Exists(path) && Path.GetFileName(path).StartsWith("$I", StringComparison.Ordinal))
        {
            // Single $I file
            AddEntry(rows, path, owner: "");
            return rows;
        }

        if (!Directory.Exists(path))
        {
            rows.Add(new { Owner = "", OriginalPath = "(target is not a directory or $I file)", OriginalSize = 0L, Deleted = "", RFile = "" });
            return rows;
        }

        // Walk: every subdir whose name matches an SID (S-1-5-21-…) gets treated as a per-user
        // recycle bin. Anything else: look for $I files directly.
        var sidDirs = new List<(string Sid, string Path)>();
        foreach (var sub in Directory.EnumerateDirectories(path))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(sub);
            if (name.StartsWith("S-1-", StringComparison.Ordinal))
            {
                sidDirs.Add((name, sub));
            }
        }

        if (sidDirs.Count == 0)
        {
            // Flat folder of $I files.
            foreach (var f in Directory.EnumerateFiles(path, "$I*"))
            {
                ct.ThrowIfCancellationRequested();
                AddEntry(rows, f, owner: "");
            }
        }
        else
        {
            foreach (var (sid, dir) in sidDirs)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var f in Directory.EnumerateFiles(dir, "$I*"))
                {
                    AddEntry(rows, f, owner: sid);
                }
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(new { Owner = "", OriginalPath = "(no $I files found)", OriginalSize = 0L, Deleted = "", RFile = "" });
        }
        return rows;
    }

    private static void AddEntry(List<object> rows, string iFile, string owner)
    {
        try
        {
            var data = File.ReadAllBytes(iFile);
            if (data.Length < 24)
            {
                rows.Add(new { Owner = owner, OriginalPath = $"({Path.GetFileName(iFile)}: truncated)", OriginalSize = 0L, Deleted = "", RFile = "" });
                return;
            }
            var version = BitConverter.ToInt64(data, 0);
            var origSize = BitConverter.ToInt64(data, 8);
            var ft = BitConverter.ToInt64(data, 16);
            string origPath;
            if (version == 2)
            {
                if (data.Length < 28)
                {
                    origPath = "(v2 missing name length)";
                }
                else
                {
                    var nameChars = BitConverter.ToInt32(data, 24);
                    var byteCount = checked(nameChars * 2);
                    if (28 + byteCount > data.Length) byteCount = data.Length - 28;
                    origPath = System.Text.Encoding.Unicode.GetString(data, 28, Math.Max(0, byteCount)).TrimEnd('\0');
                }
            }
            else
            {
                // v1: fixed 520-byte UTF-16 path starting at offset 24.
                var end = Math.Min(data.Length, 24 + 520);
                origPath = System.Text.Encoding.Unicode.GetString(data, 24, end - 24).TrimEnd('\0');
            }
            var dt = ft > 0
                ? DateTime.FromFileTimeUtc(ft).ToString("u", System.Globalization.CultureInfo.InvariantCulture)
                : "";

            // Pair with $R file (replace $I prefix with $R)
            var rName = "$R" + Path.GetFileName(iFile)[2..];
            var rPath = Path.Combine(Path.GetDirectoryName(iFile) ?? "", rName);
            string rDisplay;
            if (File.Exists(rPath)) rDisplay = $"file ({new FileInfo(rPath).Length:N0} bytes)";
            else if (Directory.Exists(rPath)) rDisplay = "directory";
            else rDisplay = "(missing — recoverable bytes not present)";

            rows.Add(new
            {
                Owner = owner,
                OriginalPath = origPath,
                OriginalSize = origSize,
                Deleted = dt,
                RFile = rDisplay,
            });
        }
        catch (Exception ex)
        {
            rows.Add(new { Owner = owner, OriginalPath = $"({Path.GetFileName(iFile)}: {ex.Message})", OriginalSize = 0L, Deleted = "", RFile = "" });
        }
    }
}
