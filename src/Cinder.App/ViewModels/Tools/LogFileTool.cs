using System.Globalization;
using Cinder.Filesystems;
using Cinder.Imaging;
using DiscUtils;
using DiscUtils.Ntfs;

namespace Cinder.App.ViewModels.Tools;

/// <summary>
/// NTFS transaction-log viewer. Accepts an image (raw / E01 / VHD / VHDX, single volume or whole
/// disk) and reads <c>$LogFile</c> from each NTFS volume in it, or an extracted <c>$LogFile</c>
/// straight from a triage collection.
/// </summary>
public sealed partial class LogFileTool : SidecarToolViewModel
{
    public override string Id => "logfile";
    public override string Title => "$LogFile";
    public override string Icon => "🧾";
    public override string Subtitle => "List the recent file creations, deletions and renames recorded in an NTFS volume's transaction log, with the file names it still holds.";
    public override string EmptyStateHint => "Pick a disk or volume image (.dd, .E01, .vhd, .vhdx), or a $LogFile extracted from one.";
    public override string Phase => "3";
    public override string Kind => "logfile";

    private const int RowBudget = 200_000;

    public override string HelpMarkdown => """
## What this is
`$LogFile` is NTFS's transaction journal: before the filesystem changes an MFT record
or a directory index it writes what it is about to do (redo) and how to reverse it
(undo). Every file creation, deletion and rename passes through it, and the payloads
carry a full `$FILE_NAME` — so the log still holds a deleted file's name and parent
after the MFT record has been reused and the USN journal has rolled over.

## When you'd use it
- The USN journal has rolled past the window you care about (it usually covers days;
  `$LogFile` is 64 MB by default and covers the last hours to days of metadata churn).
- You need the *order* of operations inside one transaction, or to see that a rename
  and a delete were the same transaction.
- To recover names from slack: the log is circular, and pages keep records from
  earlier cycles past their current end. Those rows are flagged **stale**.

## How to use it in Cinder
1. Pick evidence: a disk image (every NTFS volume in it is read) or an extracted
   `$LogFile` (KAPE and most triage tools collect it).
2. Each row is one log record: LSN, transaction, redo and undo operation (the names
   Microsoft uses — `InitializeFileRecordSegment`, `AddIndexEntryAllocation`,
   `DeleteIndexEntryAllocation`, …), the MFT record it targets, and — when the payload
   carried one — the file name, its parent record and the timestamps in the name
   attribute.
3. Filter on an operation or a name, export, or bookmark a row as a finding.

## Reading it
- **InitializeFileRecordSegment** with a name: a file record was created (new file, or
  a reused record).
- **AddIndexEntryAllocation / Root** with a name: the name was added to its parent
  directory — creation or the new side of a rename.
- **DeleteIndexEntryAllocation / Root** with a name: removed from the directory —
  deletion or the old side of a rename. The name comes from the undo payload.
- **DeallocateFileRecordSegment**: the MFT record was freed.
- Records in the same **transaction** happened together.

## Limits
Timestamps are only those inside `$FILE_NAME` payloads (creation and last-modified of
the file at that moment); the log itself does not stamp records. The MFT record number
is derived from the target VCN assuming the volume's cluster size (read from the
volume when an image is opened; 4 KiB assumed for an extracted file). The grid stops at
200,000 rows and says so. Export writes the rows in the grid, so an export of a log that
was cut short is cut short too. An extracted file is recognised by its name, which must
start with `$LogFile`.
""";

    protected override async Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct)
    {
        var (rows, truncated, note) = await Task.Run(() => Parse(evidencePath, ct), ct);
        return new ParseResult(rows, truncated, null, note);
    }

    private static (List<object> Rows, bool Truncated, string? Note) Parse(string path, CancellationToken ct)
    {
        var rows = new List<object>();
        var problems = new List<string>();
        var truncated = false;

        var name = Path.GetFileName(path);
        if (name.StartsWith("$LogFile", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            truncated = Append(NtfsLogFile.Parse(fs, 4096, ct), "", rows, ct);
            return (rows, truncated, null);
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        using var stream = ext is ".vhd" or ".vhdx" ? null : EvidenceOpener.Open(path);

        if (stream is not null)
        {
            truncated = WalkVolumes(stream, rows, problems, ct);
        }
        else
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using VirtualDisk disk = ext == ".vhd"
                ? new DiscUtils.Vhd.Disk(file, DiscUtils.Streams.Ownership.None)
                : new DiscUtils.Vhdx.Disk(file, DiscUtils.Streams.Ownership.None);
            if (disk.Partitions is null)
            {
                // No partition table: the disk is one volume.
                using var whole = disk.Content;
                TryVolume(whole, "", rows, problems, ct);
            }
            else
            {
                foreach (var part in disk.Partitions.Partitions)
                {
                    ct.ThrowIfCancellationRequested();
                    using var ps = part.Open();
                    TryVolume(ps, $"[part {part.FirstSector:N0}]", rows, problems, ct);
                    if (rows.Count >= RowBudget) break;
                }
            }
            truncated = rows.Count >= RowBudget;
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException(problems.Count > 0
                ? $"The $LogFile in this evidence could not be read: {problems[0]}"
                : "No NTFS volume with a $LogFile was found. Pick a disk or volume image, or an extracted file whose name starts with $LogFile.");
        }
        var note = problems.Count == 0 ? null : $"Part of the log could not be read, so these rows are incomplete: {string.Join("; ", problems)}";
        return (rows, truncated, note);
    }

    private static bool WalkVolumes(Stream image, List<object> rows, List<string> problems, CancellationToken ct)
    {
        if (TryVolume(image, "", rows, problems, ct))
        {
            return rows.Count >= RowBudget;
        }
        image.Position = 0;
        var vm = new VolumeManager();
        vm.AddDisk(image);
        foreach (var vol in vm.GetLogicalVolumes())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var vs = vol.Open();
                TryVolume(vs, $"[{vol.Identity}]", rows, problems, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A partition that cannot be opened at all — try the next one.
            }
            if (rows.Count >= RowBudget) return true;
        }
        return false;
    }

#pragma warning disable CA1416 // read-only NTFS access; see DiscUtilsWalker
    /// <summary>
    /// Reads one volume. Returns true when the volume is NTFS (whether or not it has a log), so
    /// the caller knows the image was a bare volume. A failure after the volume was recognised
    /// is recorded in <paramref name="problems"/>: rows read before it are kept, and the grid
    /// says they are incomplete instead of passing them off as the whole log.
    /// </summary>
    private static bool TryVolume(Stream s, string prefix, List<object> rows, List<string> problems, CancellationToken ct)
    {
        var recognised = false;
        try
        {
            s.Position = 0;
            if (!NtfsFileSystem.Detect(s)) return false;
            recognised = true;
            using var fs = new NtfsFileSystem(s);
            using var log = NtfsLogFile.Open(fs, out var clusterSize);
            if (log is null)
            {
                return true;
            }
            Append(NtfsLogFile.Parse(log, clusterSize, ct), prefix, rows, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (recognised)
            {
                problems.Add($"{(prefix.Length > 0 ? prefix + " " : "")}{ex.Message}");
            }
            return recognised;
        }
    }
#pragma warning restore CA1416

    private static bool Append(IEnumerable<LogFileRecord> records, string volume, List<object> rows, CancellationToken ct)
    {
        foreach (var r in records)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= RowBudget)
            {
                return true;
            }
            var flags = string.Join(' ', new[]
            {
                r.IsStale ? "stale" : null,
                r.IsTruncated ? "truncated" : null,
                r.IsCheckpoint ? "checkpoint" : null,
            }.Where(f => f is not null));
            rows.Add(new
            {
                r.Lsn,
                Transaction = r.TransactionId,
                Redo = r.IsCheckpoint ? "Checkpoint" : r.RedoText,
                Undo = r.IsCheckpoint ? "" : r.UndoText,
                Mft = r.MftRecordNumber?.ToString(CultureInfo.InvariantCulture) ?? "",
                FileName = r.FileName ?? "",
                Parent = r.ParentMftRecord?.ToString(CultureInfo.InvariantCulture) ?? "",
                Created = r.Created?.ToString("u", CultureInfo.InvariantCulture) ?? "",
                Modified = r.Modified?.ToString("u", CultureInfo.InvariantCulture) ?? "",
                Flags = flags,
                Volume = volume,
                Note = $"vcn {r.TargetVcn} blk {r.ClusterBlockOffset} attr 0x{r.TargetAttribute:X} prev {r.PreviousLsn}",
            });
        }
        return false;
    }
}
