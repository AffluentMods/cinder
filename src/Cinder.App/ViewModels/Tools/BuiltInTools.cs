namespace Cinder.App.ViewModels.Tools;

// =================================================================================
// EXAMINE — per-evidence parsers + viewers
// =================================================================================

public sealed partial class HexTool : ToolViewModel
{
    public override string Id => "hex";
    public override string Title => "Hex Viewer";
    public override string Icon => "0x";
    public override string Subtitle => "Raw bytes · find · goto · inspector · selection · bookmarks";
    public override string Phase => "1";
    public override string Kind => "hex";
}

public sealed partial class StringsTool : ToolViewModel
{
    public override string Id => "strings";
    public override string Title => "Strings";
    public override string Icon => "Aa";
    public override string Subtitle => "Extract printable ASCII / UTF-16 strings from any buffer.";
    public override string Phase => "1";
    public override string Kind => "strings";
}

public sealed partial class FilesystemTool : SidecarToolViewModel
{
    public override string Id => "filesystem";
    public override string Title => "Filesystem";
    public override string Icon => "🗂";
    public override string Subtitle => "Browse NTFS / FAT / ext / APFS / HFS+ / UDF / Btrfs / XFS / ISO via pytsk3.";
    public override string Phase => "3";
    public override string Kind => "filesystem";
    public override string EmptyStateHint => "Open a disk image (.dd, .E01, .raw) or a mounted volume to browse the filesystem.";
    public override IReadOnlyList<string> RequiredPythonPackages => ["pytsk3", "libewf-python"];
    protected override Task LoadAsync(string evidencePath, CancellationToken ct) => Task.CompletedTask;
}

public sealed partial class RegistryTool : SidecarToolViewModel
{
    public override string Id => "registry";
    public override string Title => "Registry";
    public override string Icon => "📒";
    public override string Subtitle => "Parse Windows hives in-process: NTUSER, SYSTEM, SOFTWARE, SAM, Amcache, transaction logs.";
    public override string Phase => "4";
    public override string Kind => "registry";
    public override string EmptyStateHint => "Open a registry hive (NTUSER.DAT, SYSTEM, SOFTWARE, SAM, Amcache.hve, etc.).";
}

public sealed partial class EventLogTool : SidecarToolViewModel
{
    public override string Id => "evtx";
    public override string Title => "Event Log";
    public override string Icon => "📋";
    public override string Subtitle => "Parse Windows .evtx via python-evtx; rule-based highlighting + filtering.";
    public override string Phase => "4";
    public override string Kind => "evtx";
    public override string EmptyStateHint => "Open an Event Log (.evtx) file from C:\\Windows\\System32\\winevt\\Logs\\.";
}

public sealed partial class PrefetchTool : SidecarToolViewModel
{
    public override string Id => "prefetch";
    public override string Title => "Prefetch";
    public override string Icon => "▶";
    public override string Subtitle => "Parse .pf files: program execution timeline + loaded files per run.";
    public override string Phase => "4";
    public override string Kind => "prefetch";
    public override string EmptyStateHint => "Open a Prefetch directory (typically C:\\Windows\\Prefetch\\).";
}

public sealed partial class ShellbagsTool : SidecarToolViewModel
{
    public override string Id => "shellbags";
    public override string Title => "Shellbags";
    public override string Icon => "🗄";
    public override string Subtitle => "Folder-access history from UsrClass.dat / NTUSER.DAT BagMRU.";
    public override string Phase => "4";
    public override string Kind => "shellbags";
    public override string EmptyStateHint => "Open NTUSER.DAT or UsrClass.dat from a user profile.";
    public override IReadOnlyList<string> RequiredPythonPackages => ["regipy"];
    protected override Task LoadAsync(string evidencePath, CancellationToken ct) => Task.CompletedTask;
}

public sealed partial class JumplistsTool : SidecarToolViewModel
{
    public override string Id => "jumplists";
    public override string Title => "Jumplists";
    public override string Icon => "↗";
    public override string Subtitle => ".automaticDestinations / .customDestinations from %APPDATA%\\…\\Recent.";
    public override string Phase => "4";
    public override string Kind => "jumplists";
    public override string EmptyStateHint => "Open the Jumplists directory (C:\\Users\\<u>\\AppData\\Roaming\\Microsoft\\Windows\\Recent\\AutomaticDestinations).";
}

public sealed partial class LnkTool : SidecarToolViewModel
{
    public override string Id => "lnk";
    public override string Title => "LNK shortcuts";
    public override string Icon => "🔗";
    public override string Subtitle => "Windows .lnk shell-link parser via pylnk3 — target paths, MAC times, machine ID.";
    public override string Phase => "4";
    public override string Kind => "lnk";
    public override string EmptyStateHint => "Open a .lnk file or a folder containing them.";
}

public sealed partial class BrowserHistoryTool : SidecarToolViewModel
{
    public override string Id => "browser";
    public override string Title => "Browser history";
    public override string Icon => "🌐";
    public override string Subtitle => "Chrome / Edge / Firefox / Brave / Opera — direct SQLite parsing.";
    public override string Phase => "4";
    public override string Kind => "browser";
    public override string EmptyStateHint => "Point at a browser profile directory (e.g. %LOCALAPPDATA%\\Google\\Chrome\\User Data\\Default).";
}

public sealed partial class UsbHistoryTool : SidecarToolViewModel
{
    public override string Id => "usb";
    public override string Title => "USB history";
    public override string Icon => "🔌";
    public override string Subtitle => "USBSTOR + MountedDevices + Setup API logs — every device that's been plugged in.";
    public override string Phase => "4";
    public override string Kind => "usb";
    public override string EmptyStateHint => "Open the SYSTEM hive (typically C:\\Windows\\System32\\config\\SYSTEM).";
}

public sealed partial class WifiHistoryTool : SidecarToolViewModel
{
    public override string Id => "wifi";
    public override string Title => "Wi-Fi history";
    public override string Icon => "📶";
    public override string Subtitle => "Saved network profiles + first/last seen per SSID.";
    public override string Phase => "4";
    public override string Kind => "wifi";
    public override string EmptyStateHint => "Open the SOFTWARE hive (C:\\Windows\\System32\\config\\SOFTWARE).";
}

public sealed partial class SrumTool : SidecarToolViewModel
{
    public override string Id => "srum";
    public override string Title => "SRUM";
    public override string Icon => "⏱";
    public override string Subtitle => "Per-user application + network usage from SRUDB.dat (libesedb).";
    public override string Phase => "4";
    public override string Kind => "srum";
    public override string EmptyStateHint => "Open SRUDB.dat (C:\\Windows\\System32\\sru\\SRUDB.dat).";
    public override IReadOnlyList<string> RequiredPythonPackages => ["libesedb-python"];
    protected override Task LoadAsync(string evidencePath, CancellationToken ct) => Task.CompletedTask;
}

public sealed partial class AmcacheTool : SidecarToolViewModel
{
    public override string Id => "amcache";
    public override string Title => "Amcache";
    public override string Icon => "🧾";
    public override string Subtitle => "Application execution history with SHA-1, publisher, first-seen.";
    public override string Phase => "4";
    public override string Kind => "amcache";
    public override string EmptyStateHint => "Open Amcache.hve (C:\\Windows\\AppCompat\\Programs\\Amcache.hve).";
}

public sealed partial class ShimcacheTool : SidecarToolViewModel
{
    public override string Id => "shimcache";
    public override string Title => "ShimCache";
    public override string Icon => "🛡";
    public override string Subtitle => "Application Compatibility cache — what executed (or was *seen*) when.";
    public override string Phase => "4";
    public override string Kind => "shimcache";
    public override string EmptyStateHint => "Open the SYSTEM hive.";
}

public sealed partial class LinuxArtifactsTool : SidecarToolViewModel
{
    public override string Id => "linux";
    public override string Title => "Linux artifacts";
    public override string Icon => "🐧";
    public override string Subtitle => "shell history · auth log · journalctl · cron · SSH · trash · packages · systemd.";
    public override string Phase => "5";
    public override string Kind => "linux";
    public override string EmptyStateHint => "Point at a mounted Linux root (or a triage folder containing /etc, /home, /var).";
    protected override Task LoadAsync(string evidencePath, CancellationToken ct) => Task.CompletedTask;
}

public sealed partial class MemoryTool : SidecarToolViewModel
{
    public override string Id => "memory";
    public override string Title => "Memory";
    public override string Icon => "🧠";
    public override string Subtitle => "volatility3 — pstree · netscan · dlllist · malfind · hashdump · lsadump.";
    public override string Phase => "7";
    public override string Kind => "memory";
    public override string EmptyStateHint => "Open a memory image (.dmp, .raw, .lime).";
    public override IReadOnlyList<string> RequiredPythonPackages => ["volatility3"];
    protected override Task LoadAsync(string evidencePath, CancellationToken ct) => Task.CompletedTask;
}

public sealed partial class NetworkTool : SidecarToolViewModel
{
    public override string Id => "network";
    public override string Title => "Network (PCAP)";
    public override string Icon => "📡";
    public override string Subtitle => "TCP flows · HTTP transactions · DNS · JA3/JA4 fingerprints from .pcap / .pcapng.";
    public override string Phase => "10";
    public override string Kind => "network";
    public override string EmptyStateHint => "Open a .pcap or .pcapng capture.";
    public override IReadOnlyList<string> RequiredPythonPackages => ["dpkt", "scapy"];
    protected override Task LoadAsync(string evidencePath, CancellationToken ct) => Task.CompletedTask;
}

public sealed partial class MobileTool : SidecarToolViewModel
{
    public override string Id => "mobile";
    public override string Title => "Mobile backup";
    public override string Icon => "📱";
    public override string Subtitle => "iOS / Android backup — messages · calls · apps.";
    public override string Phase => "10";
    public override string Kind => "mobile";
    public override string EmptyStateHint => "Point at an iOS backup folder or an Android adb backup.";
    public override IReadOnlyList<string> RequiredPythonPackages => ["iphone-backup-decrypt"];
    protected override Task LoadAsync(string evidencePath, CancellationToken ct) => Task.CompletedTask;
}

public sealed partial class EmailTool : SidecarToolViewModel
{
    public override string Id => "email";
    public override string Title => "Email (PST/MBOX)";
    public override string Icon => "✉";
    public override string Subtitle => "Outlook PST/OST + MBOX + EML + Apple Mail.";
    public override string Phase => "4";
    public override string Kind => "email";
    public override string EmptyStateHint => "Open a .msg, .eml, or .mbox file. PST/OST still requires the libpff sidecar.";
}

// GalleryTool, DocumentsTool, StringsTool, CasesTool, CustodyTool, HashSetsTool, YaraTool,
// VirusTotalTool, MapTool, GraphTool, ImagerTool, VerifyTool, MountTool, ConvertTool,
// ShadowCopyTool, RamCaptureTool, CarverTool, CloudPullTool, WorkflowsTool, PluginsTool —
// each lives in its own file under ViewModels/Tools/ to keep them tractable.

public sealed partial class DocumentsTool : ToolViewModel
{
    public override string Id => "documents";
    public override string Title => "Documents";
    public override string Icon => "📄";
    public override string Subtitle => "PDF / DOCX / XLSX / RTF / ODF preview.";
    public override string Phase => "1";
    public override string Kind => "documents";
}

// =================================================================================
// ANALYZE — case-wide
// =================================================================================

public sealed partial class TimelineTool : ToolViewModel
{
    public override string Id => "timeline";
    public override string Title => "Super-timeline";
    public override string Icon => "⏳";
    public override string Subtitle => "Every artifact with a timestamp on one navigable axis. Filter by user / source / MITRE.";
    public override string Phase => "6";
    public override string Kind => "timeline";
}

public sealed partial class MapTool : ToolViewModel
{
    public override string Id => "map";
    public override string Title => "GPS map";
    public override string Icon => "🗺";
    public override string Subtitle => "EXIF + Wi-Fi + browser geo points.";
    public override string Phase => "6";
    public override string Kind => "map";
}

public sealed partial class GraphTool : ToolViewModel
{
    public override string Id => "graph";
    public override string Title => "Comm graph";
    public override string Icon => "🕸";
    public override string Subtitle => "Force-directed who-talked-to-whom across email + chat.";
    public override string Phase => "6";
    public override string Kind => "graph";
}

public sealed partial class SearchTool : ToolViewModel
{
    public override string Id => "search";
    public override string Title => "Full-text search";
    public override string Icon => "🔎";
    public override string Subtitle => "Lucene.NET case-wide index — sub-second queries across every parsed artifact.";
    public override string Phase => "6";
    public override string Kind => "search";
}

public sealed partial class HashSetsTool : ToolViewModel
{
    public override string Id => "hashsets";
    public override string Title => "Hash sets";
    public override string Icon => "#";
    public override string Subtitle => "NSRL bulk import + custom whitelist/blocklist matching.";
    public override string Phase => "6";
    public override string Kind => "hashsets";
}

public sealed partial class YaraTool : ToolViewModel
{
    public override string Id => "yara";
    public override string Title => "YARA";
    public override string Icon => "🔬";
    public override string Subtitle => "YARA rule manager + scan files / RAM / unallocated.";
    public override string Phase => "6";
    public override string Kind => "yara";
}

public sealed partial class VirusTotalTool : ToolViewModel
{
    public override string Id => "virustotal";
    public override string Title => "VirusTotal";
    public override string Icon => "VT";
    public override string Subtitle => "Hash-only lookup. Opt-in. Never uploads bytes. User-supplied API key.";
    public override string Phase => "6";
    public override string Kind => "virustotal";
}

public sealed partial class AiCopilotTool : ToolViewModel
{
    public override string Id => "ai";
    public override string Title => "AI Copilot";
    public override string Icon => "✨";
    public override string Subtitle => "BYOM (Ollama / LM Studio / OpenAI-compatible / cloud). Structured prompts from artifacts.";
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
    public override string Subtitle => "E01 / EX01 / AFF4 / raw .dd. Bad-sector handling. Hash-on-read.";
    public override string Phase => "2";
    public override string Kind => "imager";
}

public sealed partial class VerifyTool : ToolViewModel
{
    public override string Id => "verify";
    public override string Title => "Image verify";
    public override string Icon => "✓";
    public override string Subtitle => "Re-hash + bit-compare any image against its recorded digest.";
    public override string Phase => "2";
    public override string Kind => "verify";
}

public sealed partial class MountTool : ToolViewModel
{
    public override string Id => "mount";
    public override string Title => "Mount image";
    public override string Icon => "📂";
    public override string Subtitle => "Read-only mount: VHD/VHDX/ISO via Mount-DiskImage; Linux loop+FUSE; Arsenal for E01.";
    public override string Phase => "2";
    public override string Kind => "mount";
}

public sealed partial class ConvertTool : ToolViewModel
{
    public override string Id => "convert";
    public override string Title => "Convert format";
    public override string Icon => "↔";
    public override string Subtitle => "E01 ↔ raw ↔ VHD with hash preservation.";
    public override string Phase => "2";
    public override string Kind => "convert";
}

public sealed partial class ShadowCopyTool : ToolViewModel
{
    public override string Id => "shadowcopy";
    public override string Title => "Shadow copies";
    public override string Icon => "📸";
    public override string Subtitle => "VSS (Windows) + btrfs/LVM/ZFS snapshots (Linux).";
    public override string Phase => "2";
    public override string Kind => "shadowcopy";
}

public sealed partial class RamCaptureTool : ToolViewModel
{
    public override string Id => "ramcapture";
    public override string Title => "RAM capture";
    public override string Icon => "🧪";
    public override string Subtitle => "Live RAM acquisition. Windows: signed driver / winpmem fallback. Linux: LiME.";
    public override string Phase => "7";
    public override string Kind => "ramcapture";
}

public sealed partial class CarverTool : ToolViewModel
{
    public override string Id => "carver";
    public override string Title => "File carver";
    public override string Icon => "🪓";
    public override string Subtitle => "Header+footer carving across slack / unallocated. 30 default signatures.";
    public override string Phase => "3";
    public override string Kind => "carver";
}

public sealed partial class CloudPullTool : ToolViewModel
{
    public override string Id => "cloud";
    public override string Title => "Cloud pull";
    public override string Icon => "☁";
    public override string Subtitle => "Google Drive / OneDrive / Dropbox — OAuth/PKCE. User-supplied client_id (see docs/cloud-setup.md).";
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
    public override string Subtitle => "Workspace — recent cases, create new, multi-examiner branches.";
    public override string Phase => "8";
    public override string Kind => "cases";
}

public sealed partial class ReportsTool : ToolViewModel
{
    public override string Id => "reports";
    public override string Title => "Reports";
    public override string Icon => "📝";
    public override string Subtitle => "Court-ready / IR / audit templates. Markdown · HTML · PDF · DOCX · JSON playbook.";
    public override string Phase => "8";
    public override string Kind => "reports";
}

public sealed partial class CustodyTool : ToolViewModel
{
    public override string Id => "custody";
    public override string Title => "Chain of custody";
    public override string Icon => "🔐";
    public override string Subtitle => "Append-only hash-chained log. Tamper-evident verification.";
    public override string Phase => "0";
    public override string Kind => "custody";
}

public sealed partial class WorkflowsTool : ToolViewModel
{
    public override string Id => "workflows";
    public override string Title => "Workflows";
    public override string Icon => "🔁";
    public override string Subtitle => "Visual node-graph automation — drop image, get triage report. JSON-serialisable playbooks.";
    public override string Phase => "0";
    public override string Kind => "workflows";
}

public sealed partial class PluginsTool : ToolViewModel
{
    public override string Id => "plugins";
    public override string Title => "Plugins";
    public override string Icon => "🧩";
    public override string Subtitle => "Drop-in C# plugin SDK + embedded Python scripting host.";
    public override string Phase => "0";
    public override string Kind => "plugins";
}

public sealed partial class SettingsTool : ToolViewModel
{
    public override string Id => "settings";
    public override string Title => "Settings";
    public override string Icon => "⚙";
    public override string Subtitle => "Theme · density · Python · AI provider · cloud client_ids · plugins.";
    public override string Phase => "0";
    public override string Kind => "settings";
}
