using System.Globalization;
using Cinder.Filesystems;
using Cinder.Imaging;
using DiscUtils;
using DiscUtils.Ntfs;

namespace Cinder.App.ViewModels.Tools;

/// <summary>
/// NTFS change journal viewer. Accepts an image (raw / E01 / VHD / VHDX, single volume or whole
/// disk) and reads <c>$Extend\$UsnJrnl:$J</c> from each NTFS volume in it, or an extracted
/// <c>$J</c> file straight from a triage collection.
/// </summary>
public sealed partial class UsnJournalTool : SidecarToolViewModel
{
    public override string Id => "usnjrnl";
    public override string Title => "USN journal";
    public override string Icon => "📜";
    public override string Subtitle => "List every file creation, deletion, rename and write recorded in an NTFS volume's change journal, with the time of each.";
    public override string EmptyStateHint => "Pick a disk or volume image (.dd, .E01, .vhd, .vhdx), or a $J file extracted from one.";
    public override string Phase => "3";
    public override string Kind => "usnjrnl";

    private const int RowBudget = 200_000;

    public override string HelpMarkdown => """
## What this is
NTFS keeps a change journal: one record for every create, delete, rename, write and
attribute change on the volume, each stamped with the time, the file name and the
MFT record it belongs to. It survives the file being deleted, which is what makes it
the single most-used NTFS artifact in incident response — it is how you learn that
`invoice.pdf.exe` existed for eleven seconds last Tuesday.

## When you'd use it
Whenever the question is "what changed, and when". Timeline reconstruction, proving
a file was created and then removed, catching a rename that hid a tool as a DLL, or
finding what was written in the minutes around an alert.

## How to use it in Cinder
1. Pick evidence: a disk image (.dd, .E01, .vhd, .vhdx — every NTFS volume in it is
   read) or an extracted `$J` file (KAPE and most triage tools write it as
   `$Extend\$UsnJrnl$J`).
2. Each row is one record: USN, timestamp (UTC), file name, reason flags (MFTECmd /
   Plaso naming), MFT record and sequence, parent record, attributes.
3. Filter, export CSV / JSON, or bookmark a row as a finding.

## Limits
The grid shows up to 200,000 records, oldest first, and says so when it stops. Export
writes the rows in the grid, so an export of a journal that was cut short is cut short
too. The journal only covers what NTFS still retains — a busy volume rolls it over in
days. An extracted file is recognised by its name, which must end in `$J` or start with
`$UsnJrnl`. The Timeline tool ingests `$J` files from a triage folder too.
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

        // An extracted $J: parse the file directly.
        var name = Path.GetFileName(path);
        if (name.EndsWith("$J", StringComparison.OrdinalIgnoreCase) || name.StartsWith("$UsnJrnl", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            truncated = Append(UsnJournal.Parse(fs, ct), "", rows, ct);
            return (rows, truncated, rows.Count == 0 ? "This journal file holds no records." : null);
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        using var stream = ext is ".vhd" or ".vhdx"
            ? null
            : EvidenceOpener.Open(path);

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
                ? $"The change journal in this evidence could not be read: {problems[0]}"
                : "No NTFS volume with a change journal ($UsnJrnl) was found. Pick a disk or volume image, or an extracted file named $J.");
        }
        var note = problems.Count == 0 ? null : $"Part of the journal could not be read, so these rows are incomplete: {string.Join("; ", problems)}";
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
    /// Reads one volume. Returns true when the volume is NTFS (whether or not it has a journal),
    /// so the caller knows the image was a bare volume. A failure after the volume was
    /// recognised is recorded in <paramref name="problems"/>: rows read before it are kept, and
    /// the grid says they are incomplete instead of passing them off as the whole journal.
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
            using var j = UsnJournal.Open(fs);
            if (j is null)
            {
                return true;   // NTFS, but no journal — still "handled"
            }
            Append(UsnJournal.Parse(j, ct), prefix, rows, ct);
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

    private static bool Append(IEnumerable<UsnRecord> records, string volume, List<object> rows, CancellationToken ct)
    {
        foreach (var r in records)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= RowBudget)
            {
                return true;
            }
            rows.Add(new
            {
                r.Usn,
                Timestamp = r.Timestamp == DateTimeOffset.MinValue ? "" : r.Timestamp.ToString("u", CultureInfo.InvariantCulture),
                r.FileName,
                Reason = r.ReasonText,
                Mft = $"{r.MftIndex}-{r.MftSequence}",
                Parent = $"{r.ParentMftIndex}-{r.ParentMftSequence}",
                Attributes = r.IsDirectory ? "DIR " + $"0x{r.FileAttributes:X}" : $"0x{r.FileAttributes:X}",
                Volume = volume,
                Note = r.Version == 3 ? "v3" : "v2",
            });
        }
        return false;
    }
}
