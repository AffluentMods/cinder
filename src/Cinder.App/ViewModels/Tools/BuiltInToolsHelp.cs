// =====================================================================================
// Long-form help content for every built-in tool.
//
// Each block is a `partial` override of `HelpMarkdown` on the matching tool view-model.
// The text is written for someone with ZERO forensics background: it explains what the
// artifact IS, why an investigator cares about it, how to use the tool inside Cinder,
// and what a realistic next step looks like.
//
// Light "## Section" markers are interpreted by HelpFlyoutView — anything starting with
// "## " becomes a bold section heading; blank lines become paragraph breaks.
// =====================================================================================
namespace Cinder.App.ViewModels.Tools;

// ----- EXAMINE -----------------------------------------------------------------------

public sealed partial class HexTool
{
    public override string HelpMarkdown => """
## What this is
A view of the raw bytes that make up a file, just like Notepad shows characters but at
the byte level. Every file on a computer is stored as bytes, and the hex viewer lets
you look at them directly without any program interpreting them first.

## When you'd use it
When a file won't open in its normal program, when you suspect a file has been altered,
when you need to find something a parser missed, or when you want to confirm a file's
type by its first few bytes (its "magic number"). It's also the safest way to peek at
suspicious files — no code is executed.

## How to use it in Cinder
1. Click "Open file" or press Ctrl+O, then pick any file.
2. The bytes appear on the left in hex (00–FF), with their text representation on the
   right.
3. Click any byte to place the caret. The right-side Inspector pane decodes it as every
   common integer / float / date type so you can spot timestamps and structure.
4. Press Ctrl+F to search for hex (`50 4B 03 04`), text (`password`), or both.
5. Press Ctrl+G to jump to an exact offset.
6. Click and drag to select a range. Ctrl+C copies the selection as hex.
7. Ctrl+D adds a bookmark you can name and jump back to later.

## Tip
The first 2–8 bytes of nearly every file format are a unique "signature." If the file
starts with `50 4B 03 04` it's a ZIP (or an Office document). If it starts with `25 50
44 46` it's a PDF. Cinder's signature scanner highlights mismatches between a file's
real type and its extension.
""";
}

public sealed partial class StringsTool
{
    public override string HelpMarkdown => """
## What this is
A scanner that pulls every readable word out of a file — usernames, URLs, file paths,
error messages, anything that looks like human text. Even compiled programs and disk
images contain thousands of these strings.

## When you'd use it
When you want a fast overview of what a binary file is about without analysing the
whole thing. Common uses: spotting hardcoded URLs in malware, recovering text from a
corrupted document, finding evidence of a program's purpose, or extracting paths a
program touched.

## How to use it in Cinder
1. Click "Pick file…" and select any file — executable, image, memory dump, whatever.
2. The "Filter" box at the top lets you live-search the results (e.g. type "http" to
   find every URL).
3. The "Hide gibberish" checkbox suppresses 4-byte ASCII runs that are almost
   certainly random coincidence from compressed bytes. Leave it on for sane output;
   turn it off if you're hunting for tiny tokens.
4. "Min length" controls how long a sequence of printable bytes has to be before it
   counts. 6 is the sane default; raise it to cut more noise, lower it to catch
   shorter tokens.
5. Each row shows the offset, encoding (ASCII or UTF-16), and the string itself.

## Heads-up on compressed files
If you open a `.docx`, `.xlsx`, `.zip`, `.gz`, `.pdf`, etc., a yellow banner appears
explaining that the strings shown are container metadata (filenames, dictionary
entries) — the actual document body lives inside compressed streams and is not
readable from raw bytes. For a Word doc, use the Documents tool instead.

## Tip
"Strings" is one of the oldest tricks in forensics — it works because computers store
plain text predictably. If a malware sample contains the string `discord.com/api/`,
it almost certainly talks to Discord, even if you never run it. Filter for `http` or
`https` first; that gets you 80% of the way on most samples.
""";
}

public sealed partial class GalleryTool
{
    public override string HelpMarkdown => """
## What this is
A thumbnail grid that turns a folder of images into a browsable gallery, like the
Photos app — but every image is also annotated with its EXIF data (camera, GPS, time
the photo was taken, original filename).

## When you'd use it
Reviewing photographs from a phone backup, a recovered photos folder, or a desktop
"Pictures" directory. Investigators commonly look for: GPS coordinates that prove
where someone was, timestamps that prove when, and altered images whose EXIF doesn't
match their visible content.

## How to use it in Cinder
1. Click "Pick folder" and select any directory containing images. Subfolders are
   walked automatically.
2. Thumbnails appear in a grid. Hover any tile to see its EXIF.
3. Sort by date taken, file size, or filename.
4. Click a tile to open the full image plus its complete EXIF table on the right.

## Tip
EXIF timestamps come from three places — when the camera created the file, when it
was last modified, and when its filesystem entry was last touched. Disagreements
between the three are often the most interesting evidence.
""";
}

public sealed partial class DocumentsTool
{
    public override string HelpMarkdown => """
## What this is
A previewer for Office documents, PDFs, and rich text — without launching Word or
Acrobat. It also surfaces the document's metadata: original author, last editor,
revision count, embedded images, and comments that were never resolved.

## When you'd use it
Reviewing emailed attachments without risking macro execution, comparing the
"document author" against who supposedly wrote it, or extracting comments and tracked
changes that the visible final draft hides.

## How to use it in Cinder
1. Open a .pdf, .docx, .xlsx, .pptx, .rtf, or .odt file.
2. The body renders on the left. The metadata panel on the right shows author,
   created/modified times, revision count, and any embedded files.
3. Click "Show comments" to surface every comment ever made (including resolved ones).
""";
}

public sealed partial class FilesystemTool
{
    public override string HelpMarkdown => """
## What this is
A file-explorer view for raw disk images. You can browse the files and folders that
were on the original drive without ever booting the suspect's operating system. Cinder
understands NTFS (Windows), FAT (USB sticks, older systems), ext2/3/4 (Linux), APFS
and HFS+ (Mac), and several others.

## When you'd use it
This is usually the first thing you do with a disk image: open it up and start looking
around. From here you can right-click any file to open it in another tool — a
suspicious .lnk in the Hex Viewer, a .pst in the Email tool, etc.

## How to use it in Cinder
1. Open a disk image (.dd, .raw, .E01, .vhd, .iso) or a mounted volume.
2. Cinder lists every partition. Pick the one you want.
3. Browse like Explorer or Finder — but you also see deleted files (greyed out) and
   timestamps in MAC format (Modified / Accessed / Created).
4. Right-click a file to open it in the right tool, hash it, or carve neighbouring
   slack space.

## Tip
"Deleted" rarely means gone. Until the operating system reuses a file's blocks,
they're still on disk. Cinder shows you both the deleted directory entry AND the
recoverable contents.
""";
}

public sealed partial class RegistryTool
{
    public override string HelpMarkdown => """
## What this is
The Windows Registry is the giant database where Windows stores everything from your
desktop wallpaper to which USB sticks have ever been plugged in. The Registry is split
into "hives" — files like NTUSER.DAT (per-user settings), SYSTEM (hardware + services),
SOFTWARE (installed programs), and SAM (local user accounts and password hashes).

## When you'd use it
This is one of the highest-yield places on a Windows system. Every Most-Recently-Used
list, every saved Wi-Fi password, every Run program, every installed update — it's
all here. Forensic investigators check the Registry on almost every Windows case.

## How to use it in Cinder
1. Open a registry hive file. Common locations on a live system:
   - `C:\Windows\System32\config\SYSTEM`
   - `C:\Windows\System32\config\SOFTWARE`
   - `C:\Windows\System32\config\SAM`
   - `C:\Users\<name>\NTUSER.DAT`
   - `C:\Users\<name>\AppData\Local\Microsoft\Windows\UsrClass.dat`
2. Cinder shows the key tree on the left. Click any key to see its values.
3. The "Plugins" tab runs pre-built parsers (typed lists, RunMRU, UserAssist,
   ShellBags, etc.) that pull just the interesting bits out for you.

## Tip
A registry hive in a "dirty" state (the system didn't shut down cleanly) needs its
LOG/LOG1/LOG2 transaction logs replayed before parsing. Cinder does this
automatically and shows a banner if the hive was dirty.
""";
}

public sealed partial class EventLogTool
{
    public override string HelpMarkdown => """
## What this is
Windows keeps detailed logs of nearly everything that happens — every login, every
service start, every PowerShell command, every USB insertion. These logs live as
`.evtx` files. There are typically dozens of them on a single machine.

## When you'd use it
Reconstructing what happened on a system at a given time. Common questions:
- Who logged in, when, and from where?
- Were any new accounts created or privileges escalated?
- What scripts ran via PowerShell?
- Did the antivirus detect anything?

## How to use it in Cinder
1. Open a `.evtx` file from `C:\Windows\System32\winevt\Logs\`. Useful ones:
   - `Security.evtx` — logons, account changes, audit events
   - `System.evtx` — boots, services, drivers
   - `Application.evtx` — application crashes and info messages
   - `Microsoft-Windows-PowerShell%4Operational.evtx` — every PowerShell command
2. Cinder parses every record and lays them out as a table.
3. Filter by Event ID (e.g. 4624 = successful logon, 4625 = failed logon, 4720 = user
   account created, 1102 = Security log cleared — that last one is highly suspicious).
4. Right-click any event to add it to your timeline.

## Tip
A gap in the Security log right before "interesting" activity is itself evidence —
that's usually somebody clearing logs with `wevtutil cl`. Cinder flags clear events
(1102) in red.
""";
}

public sealed partial class PrefetchTool
{
    public override string HelpMarkdown => """
## What this is
Prefetch is a Windows performance feature: every time a program runs, Windows writes
a `.pf` file in `C:\Windows\Prefetch\` describing what was loaded and when. It's
meant to make subsequent launches faster — but for forensics, it's a perfect record
of program execution.

## When you'd use it
To prove a specific program ran on this computer, when it ran, and how many times.
Especially useful for proving someone ran a tool they shouldn't have (like
`mimikatz.exe` or a packed `winrar.exe` from `%TEMP%`).

## How to use it in Cinder
1. Point Cinder at the `C:\Windows\Prefetch\` directory (or a `.pf` file).
2. Each row is one program. Columns: name, first run, last 8 run times, total runs,
   files loaded.
3. Sort by "Last run" to see the most recent activity, or by "Run count" to spot
   tools that were executed dozens of times.

## Tip
Prefetch is disabled by default on Windows servers, and on SSDs in some
configurations. If a workstation has NO prefetch files at all, that's notable —
somebody may have wiped them.
""";
}

public sealed partial class ShellbagsTool
{
    public override string HelpMarkdown => """
## What this is
Every time you open a folder in Windows Explorer, Windows remembers what view you
used (icons vs. details), what columns you sorted by, even the window position. These
preferences are stored as "shellbags" inside NTUSER.DAT and UsrClass.dat. As a side
effect, they record every folder a user ever browsed — including network shares and
external drives that are long gone.

## When you'd use it
Proving a user opened a specific folder, even if no files were modified. Especially
powerful for showing access to network paths, USB drives, or cloud-sync folders.

## How to use it in Cinder
1. Open the user's NTUSER.DAT or UsrClass.dat hive.
2. Cinder produces a flat list: every folder the user has visited.
3. Sort by "First touched" or filter by drive letter / network path.
""";
}

public sealed partial class JumplistsTool
{
    public override string HelpMarkdown => """
## What this is
Windows 7+ taskbar "right-click → recent files" lists are stored on disk. Each
program gets two files: `.automaticDestinations` (managed by Windows) and
`.customDestinations` (managed by the program). Together they record every recently
opened file per application.

## When you'd use it
To prove a user opened a specific document or visited a specific URL in a browser —
even if the file has since been deleted.

## How to use it in Cinder
1. Point Cinder at `C:\Users\<name>\AppData\Roaming\Microsoft\Windows\Recent\AutomaticDestinations\`.
2. Cinder parses every jumplist and lists each entry with its associated app.
""";
}

public sealed partial class LnkTool
{
    public override string HelpMarkdown => """
## What this is
`.lnk` files are Windows shortcuts. Every desktop icon, every "Recent files" entry,
every Start menu shortcut is a `.lnk`. They contain way more than the visible target
path — they also embed the original drive's volume serial, the host machine's name
and MAC address, the file's size and timestamps at link time, and (often) UNC paths.

## When you'd use it
Linking a file to a particular USB stick (via volume serial), proving a file was
opened from a now-disconnected network share, or recovering paths to files that have
been deleted.

## How to use it in Cinder
1. Open a `.lnk` file or a folder full of them (the user's Recent folder is gold).
2. Each row decodes the shortcut into target path, working directory, original
   timestamps, machine ID, MAC address, and volume info.

## Tip
The MAC address embedded in a `.lnk` is the host machine's, not the suspect's —
useful when proving an `.lnk` was created on a specific physical computer.
""";
}

public sealed partial class BrowserHistoryTool
{
    public override string HelpMarkdown => """
## What this is
Every modern browser stores its history, bookmarks, downloads, cookies, and saved
passwords in SQLite database files. Cinder reads these databases directly — no
need to launch the browser or run third-party tools.

## When you'd use it
The single highest-value source on any modern device. Browser history reveals
intent: searches, sites visited (and *when*), files downloaded, web-based accounts
used, even autofilled form data.

## How to use it in Cinder
1. Point Cinder at the browser's profile directory:
   - Chrome / Edge / Brave: `%LOCALAPPDATA%\Google\Chrome\User Data\Default\`
   - Firefox: `%APPDATA%\Mozilla\Firefox\Profiles\<random>.default-release\`
2. Cinder parses History, Downloads, Bookmarks, Cookies, and Login Data into one
   combined view.
3. Filter by domain, date range, or search keyword.

## Tip
"Incognito" / private browsing leaves NO trace in History — but cookies, cache, and
DNS lookups often still record visits.
""";
}

public sealed partial class UsbHistoryTool
{
    public override string HelpMarkdown => """
## What this is
Every time a USB device is plugged in, Windows writes its make, model, serial number,
and first/last connection times to multiple Registry keys plus the SetupAPI log.
Cinder pulls all of these together into one table.

## When you'd use it
Proving (or disproving) that a specific USB stick was connected to a specific
computer at a specific time. Crucial in IP-theft and insider-threat cases.

## How to use it in Cinder
1. Open the SYSTEM hive (`C:\Windows\System32\config\SYSTEM`).
2. Cinder cross-references USBSTOR, MountedDevices, and SetupAPI logs to produce one
   row per unique device.
3. Each row shows: vendor/product/serial, first plug-in, last plug-in, last drive
   letter assigned.

## Tip
USB serial numbers are globally unique. If two cases share a serial, the same
physical stick was on both machines.
""";
}

public sealed partial class WifiHistoryTool
{
    public override string HelpMarkdown => """
## What this is
Windows remembers every Wi-Fi network you've ever connected to — the SSID, the
authentication type, the date you first joined, and the network's BSSID (the access
point's MAC address).

## When you'd use it
Placing a device at a specific physical location. BSSIDs can be looked up against
public databases (WiGLE, Apple's location service) to get latitude/longitude.

## How to use it in Cinder
1. Open the SOFTWARE hive (`C:\Windows\System32\config\SOFTWARE`).
2. Cinder lists every saved Wi-Fi profile.
""";
}

public sealed partial class SrumTool
{
    public override string HelpMarkdown => """
## What this is
The System Resource Usage Monitor is a Windows feature that logs, hour by hour,
which applications were active, how much CPU and network they used, and which user
was logged in. The database is `C:\Windows\System32\sru\SRUDB.dat`.

## When you'd use it
SRUM is the only place in Windows that ties a process to network bytes sent and
received. If you need to prove an application was actively used at a specific time
(not just "installed"), SRUM is your evidence.

## How to use it in Cinder
1. Open `SRUDB.dat`.
2. Cinder parses every hourly snapshot and groups by user + application.
""";
}

public sealed partial class AmcacheTool
{
    public override string HelpMarkdown => """
## What this is
Amcache is a Windows file that records every program that has ever run on the
system, plus its SHA-1 hash, publisher, install date, and last execution time. It
lives at `C:\Windows\AppCompat\Programs\Amcache.hve`.

## When you'd use it
Confirming a program was on the system. Even if the executable has since been
deleted, its SHA-1 in Amcache lets you confirm "yes, exactly this binary ran here."

## How to use it in Cinder
1. Open `Amcache.hve`.
2. Cinder lists every InventoryApplicationFile entry with its hash, path, publisher,
   and product name.
""";
}

public sealed partial class ShimcacheTool
{
    public override string HelpMarkdown => """
## What this is
Shimcache (Application Compatibility cache) records executables that have appeared
on the system — even if they were never run. Windows uses it to decide whether
compatibility shims should apply.

## When you'd use it
Spotting binaries that were placed on disk but didn't (necessarily) execute. Useful
for catching malware that was staged but failed to launch.

## How to use it in Cinder
1. Open the SYSTEM hive.
2. Cinder lists every Shimcache entry with its path and last-modified time.

## Tip
A path in Shimcache without a matching Prefetch or Amcache entry is a strong signal
that the file existed but never ran — which is sometimes the most interesting kind
of evidence.
""";
}

public sealed partial class RecycleBinTool
{
    public override string HelpMarkdown => """
## What this is
The Windows Recycle Bin pairs every deleted file with a small metadata file. On
Vista+ these are named `$I…` (the metadata) and `$R…` (the actual file contents).
The `$I` file records the **original full path**, the **original size**, and the
**deletion time** — even after the user empties the bin, the `$I` files are often
the last surviving record of *what was on disk and when it was removed*.

## When you'd use it
For any case where deleted files matter: data exfiltration, evidence tampering,
spoliation, or simply "what did this person have before they panicked." The Recycle
Bin sits inside `$Recycle.Bin\<user-SID>\` on every Windows volume — one folder per
user that ever deleted something on that drive.

## How to use it in Cinder
1. Click "Open evidence" and point at a `$Recycle.Bin` directory (or any folder
   that contains `$I*` files).
2. Cinder walks every `S-1-5-…` SID sub-folder and decodes each metadata file.
3. The grid shows: owning SID, original path, original size, deletion UTC, and
   whether the companion `$R` file is still on disk (recoverable).

## Tip
The owning SID maps to a username via the **SAM hive** under
`SAM\Domains\Account\Users\Names\` — open SAM in the Registry tool, and the value
type field of each name entry IS the RID. Append it to the machine SID
(`S-1-5-21-…`) and you have the full mapping from "who deleted this" to "who".
""";
}

public sealed partial class EmailTool
{
    public override string HelpMarkdown => """
## What this is
A parser for Outlook (.pst / .ost), Unix mail (.mbox), and individual email files
(.eml). Cinder walks every folder, decodes every message, and lets you read or search
without needing Outlook installed.

## When you'd use it
Most "did this person say X?" investigations end up here. Email is also one of the
richest sources of attachments — malware, leaked documents, etc.

## How to use it in Cinder
1. Open a .pst, .ost, .mbox, or .eml file.
2. Browse folders on the left, messages in the middle, the message body on the right.
3. Right-click an attachment to extract it to your case and (optionally) feed it back
   into Cinder as evidence.

## Tip
Encrypted Outlook items (S/MIME, Office 365 message encryption) require the user's
private key to read. Cinder will surface that they exist; it can't decrypt them
without the key.
""";
}

public sealed partial class LinuxArtifactsTool
{
    public override string HelpMarkdown => """
## What this is
A bundle of parsers for the most useful Linux forensic artifacts: shell history
(`.bash_history`, `.zsh_history`), system journal (`journalctl`), auth log
(`/var/log/auth.log`), cron jobs, SSH keys and known_hosts, package manager logs,
and the trash folder.

## When you'd use it
Investigating a Linux server, NAS, or workstation. The Linux equivalents of "what
ran when" and "who logged in" all live in plain-text logs that Cinder normalises
into one timeline-friendly view.

## How to use it in Cinder
1. Point Cinder at a mounted Linux filesystem or a triage folder that contains
   `/etc`, `/home`, and `/var/log`.
2. Each artifact gets its own tab. Switch between them or merge everything into the
   super-timeline.
""";
}

public sealed partial class MemoryTool
{
    public override string HelpMarkdown => """
## What this is
A wrapper around Volatility 3, the standard open-source memory-forensics framework.
Cinder runs it against a captured RAM image and lays the results out as tabs:
process tree, network connections, loaded DLLs, injected code, password hashes,
cached secrets.

## When you'd use it
Live malware analysis. RAM contains what disk never sees: running processes,
decrypted in-memory passwords, command-and-control connections, code that was
injected into legitimate processes.

## How to use it in Cinder
1. Capture RAM first (use the RAM Capture tool) or open an existing memory image
   (.raw, .dmp, .lime, .vmem, .core).
2. Cinder profiles the OS and shows the available plugins.
3. Click any plugin (pstree, netscan, malfind, …) to run it. Results render as a
   table.

## Tip
Memory captures should be the FIRST thing you do on a live system — once it shuts
down, RAM is gone. Cinder's RAM Capture tool wraps the safe options.
""";
}

public sealed partial class NetworkTool
{
    public override string HelpMarkdown => """
## What this is
A parser for packet captures (.pcap / .pcapng) — the raw record of every byte that
crossed a network interface. Cinder reconstructs TCP flows, HTTP transactions, DNS
queries, and TLS fingerprints (JA3 / JA4).

## When you'd use it
Confirming what a piece of malware actually talked to, what was exfiltrated, or
recovering files transferred over HTTP. Network captures are also the gold standard
for proving timing of remote sessions.

## How to use it in Cinder
1. Open a `.pcap` or `.pcapng`.
2. Cinder shows: top talkers, TCP flow reassembly, HTTP requests, DNS queries.
3. Right-click an HTTP transaction to export the response body as a file.
""";
}

public sealed partial class MobileTool
{
    public override string HelpMarkdown => """
## What this is
A parser for unencrypted iOS device backups (created by iTunes / Finder) and Android
adb backups. Reveals messages, call history, photos, and per-app data.

## When you'd use it
When you have access to a phone backup but not the phone itself. Apple backups in
particular contain almost everything from the device, structured as SQLite
databases that Cinder reads directly.

## How to use it in Cinder
1. Pick the backup folder. On macOS that's
   `~/Library/Application Support/MobileSync/Backup/<UDID>/`; on Windows it's
   `%APPDATA%\Apple\MobileSync\Backup\<UDID>\`.
2. Cinder enumerates the backed-up apps and shows messages, call history, photos,
   and per-app SQLite databases.

## Tip
If the backup is encrypted you need the user's iTunes backup password. Without it,
Cinder can list filenames but not contents.
""";
}

// ----- ANALYZE -----------------------------------------------------------------------

public sealed partial class TimelineTool
{
    public override string HelpMarkdown => """
## What this is
Every artifact Cinder parses produces events with timestamps — login events, file
modifications, browser visits, USB insertions, registry writes. The super-timeline
merges all of them into one massive chronological view.

## When you'd use it
This is usually the SECOND thing you build, right after opening the disk image. A
super-timeline turns "what happened" into a question you can answer by scrolling.

## How to use it in Cinder
1. After parsing artifacts, open this tool.
2. Cinder merges every timestamped event into one table sorted by time.
3. Filter by source (browser only? logon events only?), by user, or by MITRE
   ATT&CK technique.
4. Zoom into a specific hour to see exactly what happened then.

## Tip
The "Live response" timeline filter (last 24 hours) is the fastest way to triage a
"something happened today" incident.
""";
}

public sealed partial class MapTool
{
    public override string HelpMarkdown => """
## What this is
A geographic view of every location mentioned in your evidence: photo GPS, Wi-Fi
BSSID lookups, browser geolocation, cell tower data.

## When you'd use it
Placing the suspect or the device at specific places at specific times.

## How to use it in Cinder
1. After parsing photos, Wi-Fi history, and browser history, open this tool.
2. Each point is one geolocated event. Click for details + a link back to the
   originating artifact.
""";
}

public sealed partial class GraphTool
{
    public override string HelpMarkdown => """
## What this is
A force-directed network graph of "who talked to whom" — built from email senders/
recipients, chat conversations, and shared documents.

## When you'd use it
Mapping a conspiracy or an org chart from raw communications. Useful for getting
oriented when you have hundreds of email addresses and don't know who matters.

## How to use it in Cinder
1. After parsing email or chat artifacts, open this tool.
2. Each node is an identity (email address, username). Each edge is a message.
3. Pin nodes you care about and the graph re-lays out around them.
""";
}

public sealed partial class SearchTool
{
    public override string HelpMarkdown => """
## What this is
A case-wide full-text search powered by Lucene. Every artifact Cinder parses gets
indexed so you can search across all of them at once — instead of opening 17 tools
and running a search in each.

## When you'd use it
Looking for a name, email address, password, or domain across every piece of
evidence in your case at once.

## How to use it in Cinder
1. Open this tool.
2. Type a query. Lucene syntax is supported: `john AND password`, `"exact phrase"`,
   `domain:evil.com`, `created:[2024-01-01 TO 2024-06-30]`.
3. Hits are grouped by source artifact.
""";
}

public sealed partial class HashSetsTool
{
    public override string HelpMarkdown => """
## What this is
A way to compare every file in your evidence against known-good and known-bad hash
lists. The most famous is NSRL — NIST's "we've seen this benign Windows DLL on
millions of computers" set, which lets you filter out boring system files. The
opposite is a custom blocklist of CSAM hashes, malware hashes, or proprietary
documents you're tracking.

## When you'd use it
Cutting an investigation's noise down by 90%. A typical Windows install has 500k+
benign files — knowing they're benign lets you focus on the few thousand that aren't.

## How to use it in Cinder
1. Import a hash set (NSRL .iso / .zip, custom CSV, or one-hash-per-line text).
2. After parsing the filesystem, this tool tells you which files match a hash
   set and which don't.
""";
}

public sealed partial class YaraTool
{
    public override string HelpMarkdown => """
## What this is
YARA is a rule language for matching patterns in files. You write a rule that says
"if these strings appear AND the file is between 100KB and 5MB AND the PE
characteristics look like X" and YARA finds every file in your evidence that
matches.

## When you'd use it
Hunting for malware families, intellectual-property leaks, or any byte/text pattern
you can describe.

## How to use it in Cinder
1. Import or write a `.yara` rule file.
2. Pick what to scan: files on disk, RAM, unallocated space, or all of the above.
3. Hits show up with the rule name and the offset that matched.
""";
}

public sealed partial class VirusTotalTool
{
    public override string HelpMarkdown => """
## What this is
A lookup that sends a file's hash (not the file itself) to VirusTotal and asks
"do you already know about this?" If VT has seen the hash before, you get all the
antivirus detections, behaviour reports, and community comments instantly.

## When you'd use it
Quickly classifying a suspicious file as "definitely malware" or "definitely
benign" without uploading anything sensitive.

## How to use it in Cinder
1. Add your VirusTotal API key under Settings (free keys are available).
2. Pick a file or a list of hashes.
3. Cinder queries VT for each hash. Nothing about the file content itself is
   transmitted.

## Privacy note
Cinder will NEVER upload file contents to VirusTotal automatically. Only the
hash leaves your computer.
""";
}

public sealed partial class AiCopilotTool
{
    public override string HelpMarkdown => """
## What this is
A chat panel backed by an AI model that you choose and run yourself — Ollama, LM
Studio, or any OpenAI-compatible API. Cinder feeds it structured snippets of
artifacts (an event log entry, a piece of registry data, a suspicious string) and
asks it to explain or contextualise.

## When you'd use it
As a research assistant for unfamiliar artifacts: "what does Event ID 4769 mean?",
"is this PowerShell command malicious?", "what programming language is this binary
written in?"

## How to use it in Cinder
1. Configure a provider in Settings → AI. Local-first (Ollama) is the default.
2. From any tool, click "Explain" to send the selected artifact to the model.
3. Ask follow-up questions in the chat panel.

## Privacy note
If you point Cinder at a local model (Ollama / LM Studio) NOTHING leaves your
machine. If you use a cloud provider, the artifact text you send is subject to
that provider's terms.
""";
}

// ----- ACQUIRE -----------------------------------------------------------------------

public sealed partial class ImagerTool
{
    public override string HelpMarkdown => """
## What this is
A tool that copies an entire physical disk into a single forensic image file (E01,
AFF4, or raw .dd), hashing every byte on the way. Once imaged, you do all analysis
against the copy and the original drive stays write-protected.

## When you'd use it
This is step ZERO of nearly every disk investigation: image first, never analyse
the original. Cinder verifies the SHA-256 of the source matches the image so you
can prove they're identical.

## How to use it in Cinder
1. Plug the source drive into a write-blocker (hardware preferred) or engage
   Cinder's software write-blocker.
2. Select source disk, destination path, and format. E01 is the standard.
3. Cinder reads the entire disk, computes hashes, and writes the image. Bad
   sectors are recorded but don't stop the imaging.
""";
}

public sealed partial class VerifyTool
{
    public override string HelpMarkdown => """
## What this is
After an image is created, this tool re-reads it and recomputes the hashes to
confirm the image is bit-for-bit identical to what was acquired. It's the
defensive sanity check that catches storage corruption.

## When you'd use it
Before every analysis session, or any time an image has been copied, moved, or
sat on slow storage for a while.

## How to use it in Cinder
1. Pick the image file.
2. Cinder reads every block and compares against the hashes recorded when the
   image was created.
""";
}

public sealed partial class MountTool
{
    public override string HelpMarkdown => """
## What this is
A way to expose an image file as a read-only drive so other software can browse
it. On Windows, Cinder uses Mount-DiskImage (built into Windows 10+) or Arsenal
Image Mounter if you have it installed. On Linux, it uses loop devices and FUSE.

## When you'd use it
When you need to run a third-party tool that doesn't speak forensic image formats.
Once mounted, the image appears as a regular drive letter (Windows) or mount path
(Linux), but writes are blocked at the kernel level.
""";
}

public sealed partial class ConvertTool
{
    public override string HelpMarkdown => """
## What this is
A converter between forensic image formats: raw .dd ↔ E01 ↔ VHD ↔ AFF4. Hashes
are preserved through the conversion so chain of custody is maintained.

## When you'd use it
When the analysis tool you need only understands one format and your image is in
another.
""";
}

public sealed partial class ShadowCopyTool
{
    public override string HelpMarkdown => """
## What this is
Volume Shadow Copies are Windows's built-in versioning system — periodically the OS
takes a copy-on-write snapshot of every drive. These snapshots are gold because
they often contain DELETED files that the live filesystem no longer has.

## When you'd use it
When you suspect a user deleted evidence before imaging. Shadow copies may still
have the originals from days or weeks earlier.

## How to use it in Cinder
1. Open a mounted Windows disk image.
2. Cinder enumerates every VSS shadow copy and lets you mount each one as a
   read-only snapshot — like time-travelling the filesystem.

## Linux note
On Linux this tool also surfaces btrfs / LVM / ZFS snapshots.
""";
}

public sealed partial class RamCaptureTool
{
    public override string HelpMarkdown => """
## What this is
Captures the live RAM of the running computer into a `.raw` file. On Windows
Cinder uses a signed kernel driver (or falls back to winpmem); on Linux it uses
LiME.

## When you'd use it
First thing on a live system. RAM has decrypted passwords, in-memory malware,
network connections — none of which survive a shutdown.

## How to use it in Cinder
1. Open this tool from a USB stick (don't write to the suspect drive).
2. Click "Capture". Cinder grabs RAM into your chosen destination.
3. Analyse it later with the Memory tool.
""";
}

public sealed partial class CarverTool
{
    public override string HelpMarkdown => """
## What this is
File carving is the recovery of files based on their signatures, even when the
filesystem says they don't exist anymore. Cinder scans unallocated space, slack,
and free clusters for file headers (the magic bytes at the start of every file
format) and reconstructs what it can.

## When you'd use it
Recovering deleted files when the directory entry is gone. Especially powerful
for images, documents, and archives.

## How to use it in Cinder
1. Open a disk image.
2. Pick which signatures to carve for (JPEG, PNG, PDF, ZIP, etc. — 30 built in).
3. Cinder writes recovered files to your case folder.

## Tip
Carving fragmented files often only recovers the first fragment. Cinder also tries
"smart carving" — using format-internal length fields to find each file's real
end.
""";
}

public sealed partial class CloudPullTool
{
    public override string HelpMarkdown => """
## What this is
With the user's consent and OAuth credentials, Cinder downloads everything from a
cloud account (Google Drive, OneDrive, Dropbox) into a case folder for analysis.

## When you'd use it
Civil discovery, custodian-cooperating investigations, or imaging a user's cloud
storage as part of a broader collection.

## How to use it in Cinder
1. Pick a provider.
2. Cinder opens a browser window to sign in. You (the examiner) supply your own
   client_id (instructions in docs/cloud-setup.md).
3. After consent, every file is downloaded along with its metadata.
""";
}

// ----- CASE --------------------------------------------------------------------------

public sealed partial class CasesTool
{
    public override string HelpMarkdown => """
## What this is
The case manager — every investigation in Cinder lives inside a "case", which is
a folder containing evidence, parsed artifacts, your notes, and the chain of
custody log. This tool lists every case you've worked on and lets you open, copy,
or close them.

## When you'd use it
Every time you start or resume an investigation.

## How to use it in Cinder
1. Click "New case" to create a fresh case. Cinder asks for a name, an examiner
   name (you), and an optional description.
2. Open existing cases by clicking them in the list.
3. Use "Branch" to fork a case — useful when several examiners are working in
   parallel on different angles.
""";
}

public sealed partial class ReportsTool
{
    public override string HelpMarkdown => """
## What this is
A report builder that turns your case findings into a court-ready document
(Markdown, HTML, PDF, or DOCX). Templates cover: criminal court reports,
incident-response reports, audit reports, and free-form.

## When you'd use it
At the end of every case. The report is the deliverable.

## How to use it in Cinder
1. Pick a template.
2. Cinder pre-fills examiner name, case name, evidence inventory, and the chain of
   custody.
3. Add your narrative sections, screenshots, and exhibits. Cinder auto-numbers
   exhibits in the order you reference them.
4. Export to your chosen format.
""";
}

public sealed partial class CustodyTool
{
    public override string HelpMarkdown => """
## What this is
The chain of custody log records every action taken inside the case: who created
it, what evidence was added, who opened what, which parsers ran. Each entry is
hashed and chained to the previous so any tampering is visible.

## When you'd use it
You always look at this before presenting evidence. If the chain has been broken,
the evidence is challengeable.

## How to use it in Cinder
1. Open this tool — it shows every entry as a row.
2. Click "Verify chain" to recompute hashes and confirm nothing has been altered.
3. Export the log as part of your report.

## Why this matters
Defense counsel WILL ask whether you can prove the evidence you're showing is the
same evidence you collected. The custody log + image hashes is how you answer
that.
""";
}

public sealed partial class WorkflowsTool
{
    public override string HelpMarkdown => """
## What this is
A visual node-graph editor for automating multi-step investigations. Drag a disk
image onto the canvas, connect it to a "parse all artifacts" node, then a
"timeline merge" node, then a "report" node — and Cinder runs the whole chain.

## When you'd use it
For repeatable case types (e.g. you do the same 12 steps on every laptop) and for
documenting your methodology in a way colleagues can re-run.

## How to use it in Cinder
1. Drag nodes from the palette on the left.
2. Connect outputs to inputs.
3. Click "Run". Cinder executes the graph, showing progress on each node.
4. Save as JSON to reuse across cases.
""";
}

public sealed partial class PluginsTool
{
    public override string HelpMarkdown => """
## What this is
A plugin manager. Cinder loads C# plugins (managed assemblies) and Python scripts
from a per-user plugin folder. Plugins can add new tools, new parsers, new report
templates.

## When you'd use it
When you need to extend Cinder for a niche format that isn't built in. The plugin
SDK is small enough that a one-off parser can live as 200 lines of Python.

## How to use it in Cinder
1. Open this tool to see installed plugins.
2. Drop a `.dll` or `.py` into the plugin folder (shown at the top of this view)
   and Cinder picks it up on next start.
""";
}

public sealed partial class SettingsTool
{
    public override string HelpMarkdown => """
## What this is
Everywhere you configure Cinder: theme (dark / light), UI density, Python venv
location, default case folder, AI provider, VirusTotal API key, cloud client IDs,
trusted plugin signers.

## When you'd use it
Once on first launch, and any time you add a new external service.
""";
}
