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
        var rows = await Task.Run(() => Parse(evidencePath, ct), ct);
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
    }

    private static List<object> Parse(string path, CancellationToken ct)
    {
        var rows = new List<object>();
        // DiscUtils' raw-disk path for VHD/VHDX, otherwise treat as a flat image.
        var ext = Path.GetExtension(path).ToLowerInvariant();
        // Open with FileShare.Read so a running OS that owns the image still lets us read.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            // .E01 — wrap the EWF reader as a Stream over the raw disk, then re-route as raw.
            if (ext == ".e01")
            {
                stream.Dispose();
                var ewf = EwfReader.Open(path);
                var ewfStream = ewf.OpenStream();
                rows.Add(new
                {
                    Inode = 0L,
                    Path = "[EWF metadata]",
                    Name = "E01",
                    Size = ewf.MediaSize,
                    IsDirectory = true,
                    IsDeleted = false,
                    Modified = ewf.AcquisitionDate ?? "",
                    Owner = "",
                    Note = $"EWF media_size={ewf.MediaSize:N0} bytes · sectors={ewf.NumberOfSectors:N0} · MD5={ewf.RecordedMd5 ?? "?"} · SHA1={ewf.RecordedSha1 ?? "?"}",
                });

                // From here we treat the EWF-backed stream as a raw disk image — try every parser.
                if (TryNtfs(ewfStream, rows, ct, path)) return rows;
                if (TryFat(ewfStream, rows, ct, path)) return rows;
                if (TryExt(ewfStream, rows, ct, path)) return rows;

                // Likely a whole-disk image — walk via VolumeManager.
                ewfStream.Position = 0;
                var vmEwf = new VolumeManager();
                vmEwf.AddDisk(ewfStream);
                foreach (var vol in vmEwf.GetLogicalVolumes())
                {
                    ct.ThrowIfCancellationRequested();
                    rows.Add(new
                    {
                        Inode = 0L,
                        Path = $"[partition] {vol.Identity}",
                        Name = vol.Identity,
                        Size = vol.Length,
                        IsDirectory = true,
                        IsDeleted = false,
                        Modified = "",
                        Owner = "",
                        Note = $"Partition · type {vol.PhysicalVolume.VolumeType}",
                    });
                    try
                    {
                        using var vs = vol.Open();
                        _ = TryNtfs(vs, rows, ct, path, partitionPrefix: vol.Identity)
                            || TryFat(vs, rows, ct, path, partitionPrefix: vol.Identity)
                            || TryExt(vs, rows, ct, path, partitionPrefix: vol.Identity);
                    }
                    catch { /* skip unreadable */ }
                }
                return rows;
            }

            // Try the container formats first.
            VirtualDisk? disk = ext switch
            {
                ".vhd" => new DiscUtils.Vhd.Disk(stream, Ownership.Dispose),
                ".vhdx" => new DiscUtils.Vhdx.Disk(stream, Ownership.Dispose),
                _ => null,
            };
            if (disk is not null)
            {
                EnumerateVirtualDisk(disk, rows, ct, path);
                return rows;
            }

            // Raw image. Try every parser DiscUtils knows about.
            // 1) ISO 9660 (Joliet) — works for CDs/DVDs and many install images.
            if (TryIso(stream, rows, ct, path)) return rows;

            // 2) NTFS — works on a partition image (not a full disk with MBR/GPT).
            if (TryNtfs(stream, rows, ct, path)) return rows;

            // 3) FAT family.
            if (TryFat(stream, rows, ct, path)) return rows;

            // 4) ext2/3/4 (Linux).
            if (TryExt(stream, rows, ct, path)) return rows;

            // 5) Last resort — try VolumeManager (handles whole-disk images with partitions).
            stream.Position = 0;
            var vm = new VolumeManager();
            vm.AddDisk(stream);
            var volumes = vm.GetLogicalVolumes();
            foreach (var vol in volumes)
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(new
                {
                    Inode = 0L,
                    Path = $"[partition] {vol.Identity}",
                    Name = vol.Identity,
                    Size = vol.Length,
                    IsDirectory = true,
                    IsDeleted = false,
                    Modified = "",
                    Owner = "",
                    Note = $"Partition · type {vol.PhysicalVolume.VolumeType}",
                });
                try
                {
                    using var volStream = vol.Open();
                    if (TryNtfs(volStream, rows, ct, path, partitionPrefix: vol.Identity) ||
                        TryFat(volStream, rows, ct, path, partitionPrefix: vol.Identity) ||
                        TryExt(volStream, rows, ct, path, partitionPrefix: vol.Identity))
                    {
                        // ok — rows appended
                    }
                }
                catch { /* skip unreadable volumes */ }
            }
            if (rows.Count == 0)
            {
                rows.Add(new
                {
                    Inode = 0L,
                    Path = "(no known filesystem detected)",
                    Name = "",
                    Size = stream.Length,
                    IsDirectory = false,
                    IsDeleted = false,
                    Modified = "",
                    Owner = "",
                    Note = "DiscUtils could not identify NTFS/FAT/ISO9660/ext on this image.",
                });
            }
        }
        finally
        {
            // VHD/VHDX paths take ownership; raw paths don't, so close here.
            try { stream.Dispose(); } catch { }
        }
        return rows;
    }

    private static bool TryIso(Stream s, List<object> rows, CancellationToken ct, string source, string partitionPrefix = "")
    {
        try
        {
            s.Position = 0;
            if (!CDReader.Detect(s)) return false;
            using var fs = new CDReader(s, joliet: true);
            EnumerateFs(fs, rows, ct, partitionPrefix);
            return true;
        }
        catch { return false; }
    }

    [UnconditionalSuppressMessage("Compatibility", "CA1416", Justification = "NTFS guard: try/catch fallback handles non-Windows paths.")]
    private static bool TryNtfs(Stream s, List<object> rows, CancellationToken ct, string source, string partitionPrefix = "")
    {
        try
        {
            s.Position = 0;
            if (!NtfsFileSystem.Detect(s)) return false;
            using var fs = new NtfsFileSystem(s);
            EnumerateFs(fs, rows, ct, partitionPrefix);
            return true;
        }
        catch { return false; }
    }

    private static bool TryFat(Stream s, List<object> rows, CancellationToken ct, string source, string partitionPrefix = "")
    {
        try
        {
            s.Position = 0;
            if (!FatFileSystem.Detect(s)) return false;
            using var fs = new FatFileSystem(s);
            EnumerateFs(fs, rows, ct, partitionPrefix);
            return true;
        }
        catch { return false; }
    }

    private static bool TryExt(Stream s, List<object> rows, CancellationToken ct, string source, string partitionPrefix = "")
    {
        // ExtFileSystem doesn't expose a Detect helper — just let the ctor throw if
        // the bytes don't look like ext.
        try
        {
            s.Position = 0;
            using var fs = new ExtFileSystem(s);
            EnumerateFs(fs, rows, ct, partitionPrefix);
            return true;
        }
        catch { return false; }
    }

    private static void EnumerateVirtualDisk(VirtualDisk disk, List<object> rows, CancellationToken ct, string source)
    {
        foreach (var part in disk.Partitions.Partitions)
        {
            ct.ThrowIfCancellationRequested();
            using var partStream = part.Open();
            var prefix = $"[part {part.FirstSector:N0}]";
            if (TryNtfs(partStream, rows, ct, source, prefix)) continue;
            if (TryFat(partStream, rows, ct, source, prefix)) continue;
            if (TryExt(partStream, rows, ct, source, prefix)) continue;
        }
    }

    private static void EnumerateFs(DiscFileSystem fs, List<object> rows, CancellationToken ct, string prefix)
    {
        const int MaxEntries = 25_000;
        var queue = new Queue<DiscDirectoryInfo>();
        queue.Enqueue(fs.Root);
        while (queue.Count > 0 && rows.Count < MaxEntries)
        {
            ct.ThrowIfCancellationRequested();
            var dir = queue.Dequeue();
            DiscFileSystemInfo[] entries;
            try
            {
                entries = dir.GetFileSystemInfos();
            }
            catch { continue; }
            foreach (var e in entries)
            {
                if (rows.Count >= MaxEntries) break;
                var isDir = (e.Attributes & FileAttributes.Directory) != 0;
                rows.Add(new
                {
                    Inode = 0L,
                    Path = string.IsNullOrEmpty(prefix) ? e.FullName : $"{prefix}{e.FullName}",
                    Name = e.Name,
                    Size = isDir ? 0L : ((DiscFileInfo)e).Length,
                    IsDirectory = isDir,
                    IsDeleted = false,
                    Modified = SafeUtc(e.LastWriteTimeUtc),
                    Owner = "",
                    Note = e.Attributes.ToString(),
                });
                if (isDir && rows.Count < MaxEntries)
                {
                    queue.Enqueue((DiscDirectoryInfo)e);
                }
            }
        }
    }

    private static string SafeUtc(DateTime d)
    {
        try { return d.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture); }
        catch { return ""; }
    }
}

// ============================================================ SHELLBAGS ============

public sealed partial class ShellbagsTool
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
    private const string EnergyEstTable   = "{FEE4E14F-02A9-4550-B5CE-5FA2DA202E37}";

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
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
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
            if (status == GetPacketStatus.NoRemainingPackets)
            {
                break;
            }
            if (status != GetPacketStatus.PacketRead)
            {
                continue;
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
                        ? $"flags={(tcp.Synchronize?"S":"")}{(tcp.Acknowledgment?"A":"")}{(tcp.Finished?"F":"")}{(tcp.Reset?"R":"")}{(tcp.Push?"P":"")}"
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
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
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
        foreach (var r in rows)
        {
            Rows.Add(r);
        }
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
