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
        // NTUSER.DAT BagMRU: Software\Microsoft\Windows\Shell\BagMRU
        // UsrClass.dat:     Local Settings\Software\Microsoft\Windows\Shell\BagMRU
        // We try both — the underlying key tree is the same shape.
        var hive = new global::Registry.RegistryHive(path);
        hive.ParseHive();
        var rows = new List<object>();
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
            WalkBagMru(key, "", rows, ct);
        }
        return rows;
    }

    private static void WalkBagMru(global::Registry.Abstractions.RegistryKey key,
                                    string pathTrail, List<object> rows, CancellationToken ct)
    {
        if (rows.Count >= 25_000) return;

        // Each BagMRU subkey is numbered (0, 1, 2…). Its NodeSlot points at the
        // Bags\<n>\Shell value that has the visited folder's preferences. We surface
        // the NodeSlot + decoded MRUListEx + LastWriteTime for each step.
        var nodeSlot = key.Values.FirstOrDefault(v => v.ValueName == "NodeSlot");
        var mruList = key.Values.FirstOrDefault(v => v.ValueName == "MRUListEx");
        rows.Add(new
        {
            Path = string.IsNullOrEmpty(pathTrail) ? key.KeyName : pathTrail,
            NodeSlot = nodeSlot?.ValueData ?? "",
            EntryCount = key.SubKeys.Count,
            MRUListEx = mruList?.ValueData ?? "",
            LastWrite = key.LastWriteTime?.ToString("u", CultureInfo.InvariantCulture) ?? "",
            HiveKey = key.KeyPath,
        });
        foreach (var sub in key.SubKeys)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= 25_000) return;
            WalkBagMru(sub, $"{pathTrail}\\{sub.KeyName}", rows, ct);
        }
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

    private static List<object> Parse(string path, CancellationToken ct)
    {
        var rows = new List<object>();
        // We stage SRUDB.dat into a fresh working directory so ESE can replay its
        // logs without polluting the source folder. Microsoft.Database.Isam expects
        // checkpoint/log/temp paths to be writable.
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
            // Walk the table catalog. A full ESE row extractor is its own subsystem;
            // for v0.1 we surface the table list so the user sees the database opened
            // successfully and which SRUM extensions are present.
            foreach (Microsoft.Database.Isam.TableDefinition t in db.Tables)
            {
                ct.ThrowIfCancellationRequested();
                if (rows.Count >= 5_000) break;
                rows.Add(new
                {
                    Table = t.Name,
                    Columns = t.Columns.Count,
                    Note = SrumTableLabel(t.Name),
                });
            }
            if (rows.Count == 0)
            {
                rows.Add(new
                {
                    Table = "(empty)",
                    Columns = 0,
                    Note = "SRUDB.dat opened but contains no tables — possibly truncated or wrong file.",
                });
            }
        }
        catch (Exception ex)
        {
            rows.Add(new
            {
                Table = "(error)",
                Columns = 0,
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

    private static string SrumTableLabel(string name) => name switch
    {
        "{973F5D5C-1D90-4944-BE8E-24B94231A174}" => "Network data usage (per-app bytes sent/recv)",
        "{D10CA2FE-6FCF-4F6D-848E-B2E99266FA89}" => "Application resource usage (per-app CPU + active time)",
        "{DD6636C4-8929-4683-974E-22C046A43763}" => "Network connectivity (per-interface uptime)",
        "{FEE4E14F-02A9-4550-B5CE-5FA2DA202E37}" => "Energy estimation (per-app power draw)",
        "{D10CA2FE-6FCF-4F6D-848E-B2E99266FA86}" => "Push notifications",
        "SruDbIdMapTable" => "ID → name lookup table (resolves all of the above)",
        _ => "",
    };
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
        if (File.Exists(root))
        {
            rows.Add(new { App = "info", Kind = "", Source = "", When = "", What = "Point Mobile at an iOS backup folder (the folder containing Manifest.db / Info.plist), not a single file." });
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
}
