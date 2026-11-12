namespace Cinder.App.ViewModels.Tools;

// =================================================================================
// The identity of every tool in the rail: id, title, one-line subtitle, and (for the tools
// that read a picked file or folder) the sentence shown before anything is loaded.
//
// Subtitles and hints are written for an examiner. Each is one sentence in sentence case
// that says what the tool shows or what to give it, as the tool works today. No library
// names, no implementation detail, no promises about future versions.
// =================================================================================

// =================================================================================
// EXAMINE — one piece of evidence at a time
// =================================================================================

public sealed partial class HexTool : ToolViewModel
{
    public override string Id => "hex";
    public override string Title => "Hex Viewer";
    public override string Icon => "0x";
    public override string Subtitle => "Read a file byte by byte, search it, jump to an offset and decode the values under the cursor.";
    public override string Phase => "1";
    public override string Kind => "hex";
}

public sealed partial class StringsTool : ToolViewModel
{
    public override string Id => "strings";
    public override string Title => "Strings";
    public override string Icon => "Aa";
    public override string Subtitle => "List the readable text inside any file and filter it by words, a pattern or a kind of identifier.";
    public override string Phase => "1";
    public override string Kind => "strings";
}

public sealed partial class FilesystemTool : SidecarToolViewModel
{
    public override string Id => "filesystem";
    public override string Title => "Filesystem";
    public override string Icon => "🗂";
    public override string Subtitle => "List the files and folders inside a disk image, including deleted entries that can still be found on NTFS.";
    public override string Phase => "3";
    public override string Kind => "filesystem";
    public override string EmptyStateHint => "Pick a disk or partition image: .dd, .raw, .E01, .aff4, .vhd, .vhdx or .iso.";
}

public sealed partial class RegistryTool : SidecarToolViewModel
{
    public override string Id => "registry";
    public override string Title => "Registry";
    public override string Icon => "📒";
    public override string Subtitle => "List the keys and values in a Windows registry hive, with each key's last-write time.";
    public override string Phase => "4";
    public override string Kind => "registry";
    public override string EmptyStateHint => "Pick a registry hive file such as NTUSER.DAT, SYSTEM, SOFTWARE, SAM or Amcache.hve.";
}

public sealed partial class EventLogTool : SidecarToolViewModel
{
    public override string Id => "evtx";
    public override string Title => "Event Log";
    public override string Icon => "📋";
    public override string Subtitle => "List the events in a Windows event log file: time, event ID, provider, computer, user and description.";
    public override string Phase => "4";
    public override string Kind => "evtx";
    public override string EmptyStateHint => "Pick an .evtx file, normally found in Windows\\System32\\winevt\\Logs.";
}

public sealed partial class PrefetchTool : SidecarToolViewModel
{
    public override string Id => "prefetch";
    public override string Title => "Prefetch";
    public override string Icon => "▶";
    public override string Subtitle => "Show which programs ran, how many times and when, from Windows Prefetch files.";
    public override string Phase => "4";
    public override string Kind => "prefetch";
    public override string EmptyStateHint => "Pick the Prefetch folder (Windows\\Prefetch) or a single .pf file.";
}

public sealed partial class ShellbagsTool : SidecarToolViewModel
{
    public override string Id => "shellbags";
    public override string Title => "Shellbags";
    public override string Icon => "🗄";
    public override string Subtitle => "Show the folders a user opened in File Explorer, including folders that no longer exist.";
    public override string Phase => "4";
    public override string Kind => "shellbags";
    public override string EmptyStateHint => "Pick a user's NTUSER.DAT or UsrClass.dat registry hive.";
}

public sealed partial class JumplistsTool : SidecarToolViewModel
{
    public override string Id => "jumplists";
    public override string Title => "Jump lists";
    public override string Icon => "↗";
    public override string Subtitle => "Show the files and folders a user recently opened in each program, from Windows jump lists.";
    public override string Phase => "4";
    public override string Kind => "jumplists";
    public override string EmptyStateHint => "Pick a user's Recent\\AutomaticDestinations or Recent\\CustomDestinations folder, or a single jump list file.";
}

public sealed partial class LnkTool : SidecarToolViewModel
{
    public override string Id => "lnk";
    public override string Title => "LNK shortcuts";
    public override string Icon => "🔗";
    public override string Subtitle => "Read Windows shortcut files: where each pointed, the target's dates, and the drive and computer it was made on.";
    public override string Phase => "4";
    public override string Kind => "lnk";
    public override string EmptyStateHint => "Pick a .lnk file, or a folder of them such as a user's Recent folder.";
}

public sealed partial class BrowserHistoryTool : SidecarToolViewModel
{
    public override string Id => "browser";
    public override string Title => "Browser history";
    public override string Icon => "🌐";
    public override string Subtitle => "List the addresses visited in Chrome, Edge, Brave, Opera and Firefox, with the last visit time and visit count.";
    public override string Phase => "4";
    public override string Kind => "browser";
    public override string EmptyStateHint => "Pick a browser profile folder, or a single History or places.sqlite file.";
}

public sealed partial class UsbHistoryTool : SidecarToolViewModel
{
    public override string Id => "usb";
    public override string Title => "USB history";
    public override string Icon => "🔌";
    public override string Subtitle => "List the USB storage devices recorded in a Windows SYSTEM hive, with each device's serial number.";
    public override string Phase => "4";
    public override string Kind => "usb";
    public override string EmptyStateHint => "Pick the SYSTEM registry hive (Windows\\System32\\config\\SYSTEM).";
}

public sealed partial class WifiHistoryTool : SidecarToolViewModel
{
    public override string Id => "wifi";
    public override string Title => "Network history";
    public override string Icon => "📶";
    public override string Subtitle => "List the wireless and wired networks a Windows computer has joined, with first and last connection times.";
    public override string Phase => "4";
    public override string Kind => "wifi";
    public override string EmptyStateHint => "Pick the SOFTWARE registry hive (Windows\\System32\\config\\SOFTWARE).";
}

public sealed partial class SrumTool : SidecarToolViewModel
{
    public override string Id => "srum";
    public override string Title => "SRUM";
    public override string Icon => "⏱";
    public override string Subtitle => "Show the processor time, network traffic and energy use Windows recorded for each program and user.";
    public override string Phase => "4";
    public override string Kind => "srum";
    public override string EmptyStateHint => "Pick SRUDB.dat (Windows\\System32\\sru\\SRUDB.dat).";
}

public sealed partial class AmcacheTool : SidecarToolViewModel
{
    public override string Id => "amcache";
    public override string Title => "Amcache";
    public override string Icon => "🧾";
    public override string Subtitle => "List the program files Windows recorded, with each one's path, SHA-1 hash, publisher and version.";
    public override string Phase => "4";
    public override string Kind => "amcache";
    public override string EmptyStateHint => "Pick Amcache.hve (Windows\\AppCompat\\Programs\\Amcache.hve).";
}

public sealed partial class ShimcacheTool : SidecarToolViewModel
{
    public override string Id => "shimcache";
    public override string Title => "ShimCache";
    public override string Icon => "🛡";
    public override string Subtitle => "List the program files Windows noted in its compatibility cache, with each file's last-modified time.";
    public override string Phase => "4";
    public override string Kind => "shimcache";
    public override string EmptyStateHint => "Pick the SYSTEM registry hive (Windows\\System32\\config\\SYSTEM).";
}

public sealed partial class RecycleBinTool : SidecarToolViewModel
{
    public override string Id => "recyclebin";
    public override string Title => "Recycle Bin";
    public override string Icon => "🗑";
    public override string Subtitle => "Show what was sent to the Recycle Bin: where it was deleted from, its size, when, and by which user.";
    public override string Phase => "4";
    public override string Kind => "recyclebin";
    public override string EmptyStateHint => "Pick a $Recycle.Bin folder, one user's folder inside it, or a single $I file.";
}

public sealed partial class LinuxArtifactsTool : SidecarToolViewModel
{
    public override string Id => "linux";
    public override string Title => "Linux artifacts";
    public override string Icon => "🐧";
    public override string Subtitle => "Read login records, system and sign-in logs, shell history and account files from a Linux system.";
    public override string Phase => "5";
    public override string Kind => "linux";
    public override string EmptyStateHint => "Pick the top folder of a mounted Linux filesystem or triage collection, or a single log file.";
}

public sealed partial class MemoryTool : SidecarToolViewModel
{
    public override string Id => "memory";
    public override string Title => "Memory";
    public override string Icon => "🧠";
    public override string Subtitle => "List the processes that were running when a Windows memory image was captured, as a parent and child tree.";
    public override string Phase => "7";
    public override string Kind => "memory";
    public override string EmptyStateHint => "Pick a Windows memory image (.raw, .dmp or .vmem). Volatility 3 must be installed.";
    public override IReadOnlyList<string> RequiredPythonPackages => ["volatility3"];

    protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct) => Task.Run(async () =>
    {
        if (!await Cinder.App.Services.Vol3Runner.IsAvailableAsync(ct))
        {
            throw new InvalidOperationException(
                "Volatility 3 was not found. Memory needs Python 3 with the volatility3 package. " +
                "Install it with:  python -m pip install volatility3  — then run this again.");
        }

        var pstree = await Cinder.App.Services.Vol3Runner.RunAsync(evidencePath, "windows.pstree.PsTree", ct);
        if (pstree.Error is not null)
        {
            // A failed run is an error, not a one-row result.
            throw new InvalidOperationException(pstree.Error);
        }
        var rows = ToRows(pstree.Rows);
        return new ParseResult(rows, false, null,
            rows.Count == 0 ? "Volatility ran but listed no processes. The image may not be a Windows memory image." : null);
    }, ct);

    /// <summary>
    /// Maps flattened pstree records to grid rows. The process name is indented by its depth in
    /// the tree, and the depth is also its own column so it survives sorting and export.
    /// </summary>
    internal static List<object> ToRows(IReadOnlyList<IDictionary<string, object?>> records)
    {
        var rows = new List<object>(records.Count);
        foreach (var r in records)
        {
            var depth = (int)TryInt(r, Cinder.App.Services.Vol3Runner.DepthKey);
            rows.Add(new
            {
                Pid = TryInt(r, "PID"),
                ParentPid = TryInt(r, "PPID"),
                Depth = depth,
                Name = new string(' ', depth * 2) + TryStr(r, "ImageFileName"),
                Threads = TryInt(r, "Threads"),
                Handles = TryInt(r, "Handles"),
                Created = TryStr(r, "CreateTime"),
                Exited = TryStr(r, "ExitTime"),
                Path = TryStr(r, "Path"),
                CommandLine = TryStr(r, "Cmd"),
            });
        }
        return rows;
    }

    private static long TryInt(IDictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) ? v switch { long l => l, int i => i, _ => 0 } : 0;

    private static string TryStr(IDictionary<string, object?> d, string k)
        => d.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
}

public sealed partial class NetworkTool : SidecarToolViewModel
{
    public override string Id => "network";
    public override string Title => "Network (PCAP)";
    public override string Icon => "📡";
    public override string Subtitle => "List the packets in a capture file with their time, addresses, ports, protocol and TCP flags.";
    public override string Phase => "10";
    public override string Kind => "network";
    public override string EmptyStateHint => "Pick a .pcap or .pcapng capture file.";
}

public sealed partial class MobileTool : SidecarToolViewModel
{
    public override string Id => "mobile";
    public override string Title => "Mobile backup";
    public override string Icon => "📱";
    public override string Subtitle => "List the files held in an iPhone or iPad backup folder or an Android .ab backup file.";
    public override string Phase => "10";
    public override string Kind => "mobile";
    public override string EmptyStateHint => "Pick an iPhone or iPad backup folder (the one containing Manifest.db) or an Android .ab file.";
}

public sealed partial class EmailTool : SidecarToolViewModel
{
    public override string Id => "email";
    public override string Title => "Email";
    public override string Icon => "✉";
    public override string Subtitle => "List the messages in mail files with sender, recipients, subject and sent time.";
    public override string Phase => "4";
    public override string Kind => "email";
    public override string EmptyStateHint => "Pick a .msg, .eml or .mbox file. Outlook .pst and .ost files also need the optional libpff-python package.";
}

public sealed partial class DocumentsTool : ToolViewModel
{
    public override string Id => "documents";
    public override string Title => "Documents";
    public override string Icon => "📄";
    public override string Subtitle => "Read the text of a PDF, Word, Excel, PowerPoint or OpenDocument file without opening it in its own program.";
    public override string Phase => "1";
    public override string Kind => "documents";
}

// =================================================================================
// ANALYZE — across evidence
// =================================================================================

public sealed partial class TimelineTool : ToolViewModel
{
    public override string Id => "timeline";
    public override string Title => "Super-timeline";
    public override string Icon => "⏳";
    public override string Subtitle => "Put every timestamped event from a folder of evidence in one list and filter it by date, source, user, text or ATT&CK technique.";
    public override string Phase => "6";
    public override string Kind => "timeline";
}

public sealed partial class MapTool : ToolViewModel
{
    public override string Id => "map";
    public override string Title => "GPS map";
    public override string Icon => "🗺";
    public override string Subtitle => "List the GPS positions recorded in a folder of photos, with the time and which clock it came from.";
    public override string Phase => "6";
    public override string Kind => "map";
}

public sealed partial class GraphTool : ToolViewModel
{
    public override string Id => "graph";
    public override string Title => "Comm graph";
    public override string Icon => "🕸";
    public override string Subtitle => "Show who wrote to whom across a folder of mail files, with the most-connected people first.";
    public override string Phase => "6";
    public override string Kind => "graph";
}

public sealed partial class SearchTool : ToolViewModel
{
    public override string Id => "search";
    public override string Title => "Full-text search";
    public override string Icon => "🔎";
    public override string Subtitle => "Index the files in a folder once, then search the text inside all of them at the same time.";
    public override string Phase => "6";
    public override string Kind => "search";
}

public sealed partial class HashSetsTool : ToolViewModel
{
    public override string Id => "hashsets";
    public override string Title => "Hash sets";
    public override string Icon => "#";
    public override string Subtitle => "Keep a database of known file hashes and look a hash up to see whether it is known good or known bad.";
    public override string Phase => "6";
    public override string Kind => "hashsets";
}

public sealed partial class YaraTool : ToolViewModel
{
    public override string Id => "yara";
    public override string Title => "YARA";
    public override string Icon => "🔬";
    public override string Subtitle => "Scan a file with YARA rules that use text and hex strings; other parts of the rule language are skipped and reported.";
    public override string Phase => "6";
    public override string Kind => "yara";
}

public sealed partial class VirusTotalTool : ToolViewModel
{
    public override string Id => "virustotal";
    public override string Title => "VirusTotal";
    public override string Icon => "VT";
    public override string Subtitle => "Look a file hash up on VirusTotal with your own API key; file contents are never uploaded.";
    public override string Phase => "6";
    public override string Kind => "virustotal";
}

public sealed partial class AiCopilotTool : ToolViewModel
{
    public override string Id => "ai";
    public override string Title => "AI Copilot";
    public override string Icon => "✨";
    public override string Subtitle => "Chat with a language model you provide: Ollama, LM Studio or any OpenAI-compatible server.";
    public override string Phase => "9";
    public override string Kind => "ai";
}

// =================================================================================
// ACQUIRE
// =================================================================================

public sealed partial class ImagerTool : ToolViewModel
{
    public override string Id => "imager";
    public override string Title => "Disk imager";
    public override string Icon => "💽";
    public override string Subtitle => "Copy a disk or an existing image into a raw, E01, AFF4, VHD or VHDX image file, hashing every byte as it is read.";
    public override string Phase => "2";
    public override string Kind => "imager";
}

public sealed partial class VerifyTool : ToolViewModel
{
    public override string Id => "verify";
    public override string Title => "Image verify";
    public override string Icon => "✓";
    public override string Subtitle => "Re-read an image and compare its hash with the one recorded when it was made.";
    public override string Phase => "2";
    public override string Kind => "verify";
}

public sealed partial class MountTool : ToolViewModel
{
    public override string Id => "mount";
    public override string Title => "Mount image";
    public override string Icon => "📂";
    public override string Subtitle => "Attach a disk image to this computer read-only so other programs can browse it.";
    public override string Phase => "2";
    public override string Kind => "mount";
}

public sealed partial class ConvertTool : ToolViewModel
{
    public override string Id => "convert";
    public override string Title => "Convert format";
    public override string Icon => "↔";
    public override string Subtitle => "Convert an image between raw, E01, AFF4, VHD and VHDX, then check that the copy hashes the same as the source.";
    public override string Phase => "2";
    public override string Kind => "convert";
}

public sealed partial class ShadowCopyTool : ToolViewModel
{
    public override string Id => "shadowcopy";
    public override string Title => "Shadow copies";
    public override string Icon => "📸";
    public override string Subtitle => "List the volume snapshots that exist on this computer.";
    public override string Phase => "2";
    public override string Kind => "shadowcopy";
}

public sealed partial class RamCaptureTool : ToolViewModel
{
    public override string Id => "ramcapture";
    public override string Title => "RAM capture";
    public override string Icon => "🧪";
    public override string Subtitle => "Capturing this computer's memory is not available yet; this screen explains which other programs to use.";
    public override string Phase => "7";
    public override string Kind => "ramcapture";
}

public sealed partial class CarverTool : ToolViewModel
{
    public override string Id => "carver";
    public override string Title => "File carver";
    public override string Icon => "🪓";
    public override string Subtitle => "Recover files from an image by their file signatures, including from deleted and unused space.";
    public override string Phase => "3";
    public override string Kind => "carver";
}

public sealed partial class CloudPullTool : ToolViewModel
{
    public override string Id => "cloud";
    public override string Title => "Cloud pull";
    public override string Icon => "☁";
    public override string Subtitle => "Downloading from Google Drive, OneDrive or Dropbox is not available yet; this screen only builds the sign-in link.";
    public override string Phase => "10";
    public override string Kind => "cloud";
}

// =================================================================================
// CASE
// =================================================================================

public sealed partial class CasesTool : ToolViewModel
{
    public override string Id => "cases";
    public override string Title => "Cases";
    public override string Icon => "📁";
    public override string Subtitle => "Create a new case file or reopen one you worked on recently.";
    public override string Phase => "8";
    public override string Kind => "cases";
}

public sealed partial class ReportsTool : ToolViewModel
{
    public override string Id => "reports";
    public override string Title => "Reports";
    public override string Icon => "📝";
    public override string Subtitle => "Write a report from a template, add your bookmarked findings as exhibits, and export it as Markdown, HTML, PDF, Word or JSON.";
    public override string Phase => "8";
    public override string Kind => "reports";
}

public sealed partial class CustodyTool : ToolViewModel
{
    public override string Id => "custody";
    public override string Title => "Chain of custody";
    public override string Icon => "🔐";
    public override string Subtitle => "Read a case's custody log, check that no entry has been altered, and sign its latest entry.";
    public override string Phase => "0";
    public override string Kind => "custody";
}

public sealed partial class WorkflowsTool : ToolViewModel
{
    public override string Id => "workflows";
    public override string Title => "Workflows";
    public override string Icon => "🔁";
    public override string Subtitle => "Run a saved sequence of steps, such as hash, list files, carve and report, from a workflow file.";
    public override string Phase => "0";
    public override string Kind => "workflows";
}

public sealed partial class PluginsTool : ToolViewModel
{
    public override string Id => "plugins";
    public override string Title => "Plugins";
    public override string Icon => "🧩";
    public override string Subtitle => "Load add-on tools from a folder of plugin files.";
    public override string Phase => "0";
    public override string Kind => "plugins";
}

public sealed partial class SettingsTool : ToolViewModel
{
    public override string Id => "settings";
    public override string Title => "Settings";
    public override string Icon => "⚙";
    public override string Subtitle => "Change the theme, the hash-set database, the custody time-stamp service, the AI model and cloud sign-in details.";
    public override string Phase => "0";
    public override string Kind => "settings";
}
