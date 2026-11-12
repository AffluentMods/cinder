using System.Globalization;
using System.Security.Cryptography;
using DiscUtils;
using DiscUtils.Ext;
using DiscUtils.Fat;
using DiscUtils.Iso9660;
using DiscUtils.Ntfs;
using DiscUtils.Ntfs.Internals;

namespace Cinder.Filesystems;

// CA1416: DiscUtils annotates NtfsFileSystem and its Internals MFT API as Windows-only. The
// annotation is earned by the *write* path — formatting and creating files go through
// System.Security.Principal.SecurityIdentifier, which throws on Linux — but this walker only
// reads, and the read path is pure managed parsing of on-disk structures with no OS
// dependency. DiscUtilsWalkerTests runs against a checked-in NTFS image on Linux in CI, which
// is what makes suppressing the analyzer here a verified decision rather than a hopeful one.
// Nothing in this file may call a DiscUtils NTFS write API.
#pragma warning disable CA1416

/// <summary>A hash-set answer for one file, kept free of any dependency on the search project.</summary>
public sealed record HashVerdict(string Verdict, string SetName, string? Label);

/// <summary>Tunables for a walk. Defaults match what the Filesystem tool shows without configuration.</summary>
public sealed record WalkOptions(
    int MaxEntries = 25_000,
    int MaxDeletedEntries = 10_000,
    bool HashFiles = false,
    long HashSizeLimitBytes = 64L * 1024 * 1024,
    long HashTotalLimitBytes = 8L * 1024 * 1024 * 1024,
    Func<string, string, HashVerdict?>? Lookup = null,
    int MaxDepth = 128,
    int MaxPathLength = 4096);

/// <summary>What a walk produced, with the truncation flags a caller must surface.</summary>
public sealed class WalkResult
{
    public List<FileEntry> Entries { get; } = new();
    public bool Truncated { get; set; }
    public bool DeletedTruncated { get; set; }

    /// <summary>
    /// True when at least one directory was listed but not descended into because it sat at
    /// <see cref="WalkOptions.MaxDepth"/> or its path exceeded <see cref="WalkOptions.MaxPathLength"/>.
    /// <see cref="Truncated"/> is set as well, so a caller that only checks that flag still
    /// tells the user the listing is incomplete.
    /// </summary>
    public bool DepthTruncated { get; set; }
    public long HashedFiles { get; set; }
    public long HashedBytes { get; set; }
    public long HashSkipped { get; set; }
    public List<string> Detected { get; } = new();
    public string? DeletedRecoveryError { get; set; }
}

/// <summary>
/// Filesystem enumeration over DiscUtils: NTFS / FAT / ext2-4 / ISO 9660, either a single
/// volume or a whole disk walked partition by partition. Produces typed <see cref="FileEntry"/>
/// records with all timestamps DiscUtils exposes, optional MD5 + SHA-1 per file with a
/// hash-set verdict, and — on NTFS — the names of deleted files recovered from $MFT records
/// that are no longer in use.
/// </summary>
public static class DiscUtilsWalker
{
    /// <summary>
    /// <see cref="FileEntry.Extras"/> key that says how an entry's timestamps relate to UTC.
    /// Absent means they are UTC instants, as the field names say.
    /// </summary>
    public const string TimeBasisKey = "time_basis";

    /// <summary>
    /// <see cref="TimeBasisKey"/> value for FAT: the volume records local wall-clock time and
    /// no zone. The entry's timestamp fields carry that wall-clock reading unchanged (offset
    /// +00:00 is a placeholder, not a claim); converting it to an instant needs the zone of
    /// the machine that wrote the volume, which the evidence does not record.
    /// </summary>
    public const string TimeBasisLocalUnknownZone = "local-unknown-zone";

    /// <summary><see cref="FileEntry.Extras"/> key set on a directory that was listed but not walked.</summary>
    public const string NotDescendedKey = "not_descended";

    public static WalkResult Walk(Stream image, WalkOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var o = options ?? new WalkOptions();
        var r = new WalkResult();

        if (TryIso(image, "", r, o, ct) || TryNtfs(image, "", r, o, ct) || TryFat(image, "", r, o, ct) || TryExt(image, "", r, o, ct))
        {
            return r;
        }

        // Whole-disk image: enumerate partitions and try each.
        image.Position = 0;
        var vm = new VolumeManager();
        vm.AddDisk(image);
        foreach (var vol in vm.GetLogicalVolumes())
        {
            ct.ThrowIfCancellationRequested();
            var prefix = $"[{vol.Identity}]";
            try
            {
                using var vs = vol.Open();
                _ = TryNtfs(vs, prefix, r, o, ct) || TryFat(vs, prefix, r, o, ct) || TryExt(vs, prefix, r, o, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* unreadable volume — the others may still parse */ }
        }
        return r;
    }

    public static WalkResult WalkDisk(VirtualDisk disk, WalkOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        var o = options ?? new WalkOptions();
        var r = new WalkResult();
        foreach (var part in disk.Partitions.Partitions)
        {
            ct.ThrowIfCancellationRequested();
            using var ps = part.Open();
            var prefix = $"[part {part.FirstSector:N0}]";
            _ = TryNtfs(ps, prefix, r, o, ct) || TryFat(ps, prefix, r, o, ct) || TryExt(ps, prefix, r, o, ct);
        }
        return r;
    }

    private static bool TryIso(Stream s, string prefix, WalkResult r, WalkOptions o, CancellationToken ct)
    {
        try
        {
            s.Position = 0;
            if (!CDReader.Detect(s)) return false;
            using var fs = new CDReader(s, joliet: true);
            r.Detected.Add("iso9660");
            WalkFileSystem(fs, prefix, r, o, ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static bool TryNtfs(Stream s, string prefix, WalkResult r, WalkOptions o, CancellationToken ct)
    {
        try
        {
            s.Position = 0;
            if (!NtfsFileSystem.Detect(s)) return false;
            using var fs = new NtfsFileSystem(s);
            r.Detected.Add("ntfs");
            WalkFileSystem(fs, prefix, r, o, ct);
            WalkDeletedMft(fs, prefix, r, o, ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static bool TryFat(Stream s, string prefix, WalkResult r, WalkOptions o, CancellationToken ct)
    {
        try
        {
            s.Position = 0;
            if (!FatFileSystem.Detect(s)) return false;

            // FAT stores local wall-clock time with no zone. DiscUtils' default converter
            // "fixes" that by assuming the zone of the machine doing the reading, so the same
            // image produced different timestamps on different examiners' workstations. The
            // identity converter hands back what is on disk; the entries are marked so nobody
            // mistakes it for UTC.
            using var fs = new FatFileSystem(s, DiscUtils.Streams.Ownership.None, static (time, _) => time);
            r.Detected.Add("fat");
            WalkFileSystem(fs, prefix, r, o, ct, TimeBasisLocalUnknownZone);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static bool TryExt(Stream s, string prefix, WalkResult r, WalkOptions o, CancellationToken ct)
    {
        // ExtFileSystem has no Detect helper; the constructor throws on non-ext bytes.
        try
        {
            s.Position = 0;
            using var fs = new ExtFileSystem(s);
            r.Detected.Add("ext");
            WalkFileSystem(fs, prefix, r, o, ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>
    /// Breadth-first walk of the live directory tree. Bounded three ways, because the tree is
    /// whatever the image says it is: by entry count, by depth and by path length. A directory
    /// whose entry points back at one of its ancestors is an endless tree, and without the
    /// last two it is walked until the entry budget runs out with every path longer than the
    /// one before.
    /// </summary>
    internal static void WalkFileSystem(DiscFileSystem fs, string prefix, WalkResult r, WalkOptions o, CancellationToken ct, string? timeBasis = null)
    {
        var queue = new Queue<(DiscDirectoryInfo Dir, int Depth)>();
        queue.Enqueue((fs.Root, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (r.Entries.Count >= o.MaxEntries)
            {
                r.Truncated = true;
                return;
            }

            var (dir, depth) = queue.Dequeue();

            // GetDirectories()/GetFiles() return typed infos; GetFileSystemInfos() returns the
            // base type, which would make `is DiscFileInfo` false for every entry and silently
            // zero every size, skip every hash and never descend a subdirectory.
            DiscDirectoryInfo[] subdirs;
            DiscFileInfo[] files;
            try
            {
                subdirs = dir.GetDirectories();
                files = dir.GetFiles();
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }

            foreach (var d in subdirs)
            {
                if (r.Entries.Count >= o.MaxEntries)
                {
                    r.Truncated = true;
                    return;
                }
                var extras = AttributesOf(d, timeBasis);
                var descend = true;
                if (depth + 1 >= o.MaxDepth)
                {
                    extras[NotDescendedKey] = $"depth limit ({o.MaxDepth}) reached";
                    descend = false;
                }
                else if (SafeFullNameLength(d) > o.MaxPathLength)
                {
                    extras[NotDescendedKey] = $"path longer than {o.MaxPathLength} characters";
                    descend = false;
                }
                r.Entries.Add(LiveEntry(d, prefix, isDir: true, size: 0, extras));
                if (descend)
                {
                    queue.Enqueue((d, depth + 1));
                }
                else
                {
                    r.DepthTruncated = true;
                    r.Truncated = true;
                }
            }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (r.Entries.Count >= o.MaxEntries)
                {
                    r.Truncated = true;
                    return;
                }
                var extras = AttributesOf(f, timeBasis);
                var size = SafeLength(f);
                if (o.HashFiles)
                {
                    HashFile(f, size, extras, r, o, ct);
                }
                r.Entries.Add(LiveEntry(f, prefix, isDir: false, size, extras));
            }
        }
    }

    private static int SafeFullNameLength(DiscFileSystemInfo e)
    {
        try { return e.FullName.Length; } catch { return int.MaxValue; }
    }

    private static Dictionary<string, string> AttributesOf(DiscFileSystemInfo e, string? timeBasis)
    {
        var extras = new Dictionary<string, string>(StringComparer.Ordinal);
        try { extras["attributes"] = e.Attributes.ToString(); } catch { }
        if (timeBasis is not null)
        {
            extras[TimeBasisKey] = timeBasis;
        }
        return extras;
    }

    private static FileEntry LiveEntry(DiscFileSystemInfo e, string prefix, bool isDir, long size, Dictionary<string, string> extras)
        => new(
            Inode: RecordNumberOf(e, extras),
            Path: string.IsNullOrEmpty(prefix) ? e.FullName : prefix + e.FullName,
            Name: e.Name,
            Size: size,
            IsDirectory: isDir,
            IsDeleted: false,
            CreatedUtc: SafeUtc(() => e.CreationTimeUtc),
            ModifiedUtc: SafeUtc(() => e.LastWriteTimeUtc),
            AccessedUtc: SafeUtc(() => e.LastAccessTimeUtc),
            MetadataChangedUtc: null,
            Owner: null,
            Group: null,
            UnixMode: null,
            Extras: extras);

    /// <summary>
    /// The file's record number in its file system: the MFT record on NTFS (the low 48 bits of
    /// the file reference; the high 16 are the sequence number, kept in <c>mft_sequence</c>),
    /// the inode on ext. FAT and ISO 9660 have no such number, and 0 is returned for them.
    /// Live entries used to be given 0 unconditionally, so the table showed a column of zeros
    /// and a deleted file's record could not be compared with the live file that reused it.
    /// </summary>
    private static long RecordNumberOf(DiscFileSystemInfo e, Dictionary<string, string> extras)
    {
        try
        {
            switch (e.FileSystem)
            {
                case NtfsFileSystem ntfs:
                    var reference = ntfs.GetFileId(e.FullName);
                    extras["mft_sequence"] = ((reference >> 48) & 0xFFFF).ToString(CultureInfo.InvariantCulture);
                    return reference & 0x0000_FFFF_FFFF_FFFF;
                case IUnixFileSystem unix:
                    return unix.GetUnixFileInfo(e.FullName).Inode;
                default:
                    return 0;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A damaged entry must not stop the listing; it simply has no number.
            return 0;
        }
    }

    /// <summary>
    /// MD5 + SHA-1 in one read, with the size and total-bytes caps applied first. The verdict
    /// callback (typically a hash-set lookup) is consulted with both digests.
    /// </summary>
    private static void HashFile(DiscFileInfo fi, long size, Dictionary<string, string> extras, WalkResult r, WalkOptions o, CancellationToken ct)
    {
        if (size > o.HashSizeLimitBytes)
        {
            extras["hash_skipped"] = $"over size limit ({o.HashSizeLimitBytes:N0} bytes)";
            r.HashSkipped++;
            return;
        }
        if (r.HashedBytes + size > o.HashTotalLimitBytes)
        {
            extras["hash_skipped"] = "hashing budget for this walk exhausted";
            r.HashSkipped++;
            return;
        }

        try
        {
            using var s = fi.OpenRead();
            var outcome = HashStream(s, o.HashSizeLimitBytes, o.HashTotalLimitBytes - r.HashedBytes, ct,
                out var md5Hex, out var sha1Hex, out var bytesRead);

            // Bytes read count against the walk's budget whether or not a digest came out of
            // them; otherwise a volume full of files that lie about their size is read in
            // full, one "skipped" file at a time.
            r.HashedBytes += bytesRead;

            switch (outcome)
            {
                case HashOutcome.OverSizeLimit:
                    extras["hash_skipped"] = $"over size limit ({o.HashSizeLimitBytes:N0} bytes): the data is longer than the {size:N0} bytes the directory entry records";
                    r.HashSkipped++;
                    return;
                case HashOutcome.OverTotalBudget:
                    extras["hash_skipped"] = "hashing budget for this walk exhausted";
                    r.HashSkipped++;
                    return;
            }

            extras["md5"] = md5Hex;
            extras["sha1"] = sha1Hex;
            r.HashedFiles++;

            if (o.Lookup is not null)
            {
                var v = o.Lookup(sha1Hex, md5Hex);
                extras["verdict"] = v?.Verdict ?? "Unknown";
                if (v is not null)
                {
                    extras["hash_set"] = v.SetName;
                    if (!string.IsNullOrEmpty(v.Label)) extras["hash_label"] = v.Label;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            extras["hash_skipped"] = "unreadable: " + ex.Message;
            r.HashSkipped++;
        }
    }

    internal enum HashOutcome { Hashed, OverSizeLimit, OverTotalBudget }

    /// <summary>
    /// Hashes <paramref name="s"/> to its end, enforcing both caps on the bytes actually read.
    /// The size a directory entry reports is evidence, not a promise: on NTFS it comes from
    /// the index entry while the data comes from the file record, and nothing forces the two
    /// to agree. Checking the reported size alone let a file that claims ten bytes and holds a
    /// terabyte of sparse data be read to the end, with no way to cancel.
    /// </summary>
    internal static HashOutcome HashStream(Stream s, long sizeLimit, long budgetRemaining, CancellationToken ct,
        out string md5Hex, out string sha1Hex, out long bytesRead)
    {
        md5Hex = "";
        sha1Hex = "";
        bytesRead = 0;

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[1 << 16];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var n = s.Read(buffer, 0, buffer.Length);
            if (n <= 0) break;
            bytesRead += n;
            if (bytesRead > sizeLimit)
            {
                return HashOutcome.OverSizeLimit;
            }
            if (bytesRead > budgetRemaining)
            {
                return HashOutcome.OverTotalBudget;
            }
            md5.AppendData(buffer, 0, n);
            sha1.AppendData(buffer, 0, n);
        }

        md5Hex = Convert.ToHexStringLower(md5.GetHashAndReset());
        sha1Hex = Convert.ToHexStringLower(sha1.GetHashAndReset());
        return HashOutcome.Hashed;
    }

    /// <summary>
    /// Names of deleted files from $MFT records no longer in use. Windows deletes a file by
    /// clearing one flag; $FILE_NAME and $STANDARD_INFORMATION survive until the record is
    /// reused, so name, size and all timestamps are recoverable. Content is not: the data runs
    /// may already belong to another file — that is the carver's job.
    /// </summary>
    internal static void WalkDeletedMft(NtfsFileSystem fs, string prefix, WalkResult r, WalkOptions o, CancellationToken ct)
    {
        try
        {
            var mft = fs.GetMasterFileTable();
            var added = 0;
            foreach (var entry in mft.GetEntries(EntryStates.NotInUse))
            {
                ct.ThrowIfCancellationRequested();
                if (added >= o.MaxDeletedEntries)
                {
                    r.DeletedTruncated = true;
                    return;
                }

                FileNameAttribute? name = null;
                StandardInformationAttribute? si = null;
                foreach (var attr in entry.Attributes)
                {
                    // A record can carry both an 8.3 and a long $FILE_NAME; keep the longest.
                    if (attr is FileNameAttribute fn && !string.IsNullOrEmpty(fn.FileName) &&
                        (name is null || fn.FileName.Length > name.FileName.Length))
                    {
                        name = fn;
                    }
                    else if (attr is StandardInformationAttribute s)
                    {
                        si = s;
                    }
                }
                if (name is null)
                {
                    continue;   // never populated or already reused — nothing to recover
                }

                var isDir = (entry.Flags & MasterFileTableEntryFlags.IsDirectory) != 0;
                r.Entries.Add(new FileEntry(
                    Inode: entry.Index,
                    Path: $"{prefix}[deleted]/{name.FileName}",
                    Name: name.FileName,
                    Size: name.RealSize,
                    IsDirectory: isDir,
                    IsDeleted: true,
                    CreatedUtc: AsUtc(si?.CreationTime ?? name.CreationTime),
                    ModifiedUtc: AsUtc(si?.ModificationTime ?? name.ModificationTime),
                    AccessedUtc: AsUtc(si?.LastAccessTime ?? name.LastAccessTime),
                    MetadataChangedUtc: AsUtc(si?.MasterFileTableChangedTime ?? name.MasterFileTableChangedTime),
                    Owner: null,
                    Group: null,
                    UnixMode: null,
                    Extras: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["mft_index"] = entry.Index.ToString(CultureInfo.InvariantCulture),
                        ["mft_sequence"] = entry.SequenceNumber.ToString(CultureInfo.InvariantCulture),
                        ["note"] = "Deleted — MFT record not in use. Name and timestamps recovered from $FILE_NAME; content may be overwritten — carve to recover.",
                    }));
                added++;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The live listing is already in the result; a failure here must not discard it.
            r.DeletedRecoveryError = ex.Message;
        }
    }
    private static long SafeLength(DiscFileInfo fi)
    {
        try { return fi.Length; } catch { return 0; }
    }

    private static DateTimeOffset? SafeUtc(Func<DateTime> get)
    {
        try { return AsUtc(get()); } catch { return null; }
    }

    /// <summary>
    /// Wraps a timestamp DiscUtils returned from a <c>*Utc</c> accessor without moving it.
    /// Only a value that is explicitly <see cref="DateTimeKind.Local"/> is converted. An
    /// <see cref="DateTimeKind.Unspecified"/> value is already the reading the caller wants
    /// (ext returns UTC that way; FAT, through the identity converter, the on-disk wall
    /// clock) — calling <c>ToUniversalTime()</c> on it would subtract this workstation's zone
    /// offset and make the result depend on where the examiner sits.
    /// </summary>
    internal static DateTimeOffset? AsUtc(DateTime d)
    {
        if (d == default || d.Year < 1601) return null;
        try
        {
            var utc = d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d;
            return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        }
        catch { return null; }
    }
}

#pragma warning restore CA1416
