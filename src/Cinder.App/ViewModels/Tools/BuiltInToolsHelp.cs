// =====================================================================================
// Long-form help content for every built-in tool.
//
// Each block is a `partial` override of `HelpMarkdown` on the matching tool view-model.
// The text is written for someone with no forensics background: what the artifact is, why
// an investigator cares, and how the tool works.
//
// THE RULE FOR THIS FILE: describe what the tool does today. Every step, column and limit
// named here must exist in the code. A feature that is planned belongs in ROADMAP.md, not
// here. An examiner who reads "first plug-in time" and finds a key last-write time has been
// misled, and help text is the easiest place to do that by accident. When a tool changes,
// change its help in the same commit. Each block ends with "## Limits" where there are any.
//
// Light "## Section" markers are interpreted by the help flyout — a line starting with
// "## " becomes a heading; "- " and "1. " become list items; blank lines break paragraphs.
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
1. Click "Open file" or press Ctrl+O, then pick any file. Each file opens in its own tab.
2. The bytes appear on the left in hex (00–FF), with their text representation on the
   right.
3. Click any byte to place the caret. The Inspector pane decodes the bytes there as the
   common integer, float and date types so you can spot timestamps and structure.
   Ctrl+Shift+B shows or hides the Inspector.
4. Press Ctrl+F to search for text, UTF-16 text, a byte pattern (`50 4B 03 04`) or a
   regular expression. Enter runs the search; F3 and Shift+F3 step through the hits.
5. Press Ctrl+G to jump to an offset (hex with 0x, or decimal).
6. Click and drag to select a range. Ctrl+C copies the selection as hex.
7. Ctrl+D bookmarks the caret position so you can jump back to it.

## Tip
The first 2–8 bytes of nearly every file format are a unique "signature." If the file
starts with `50 4B 03 04` it's a ZIP (or an Office document). If it starts with `25 50
44 46` it's a PDF, whatever its extension says.
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
2. The picker at the left of the filter row sets how the filter is applied:
   "Contains text" is a plain substring, "Regex" is a .NET regular expression, and
   the rest are presets — email addresses, URLs, IP addresses, domain names, payment
   card numbers, phone numbers, cryptocurrency addresses, hashes, Windows paths and
   more. Pick a preset and the list shows only strings containing one; type in the
   box as well to narrow within it ("Email addresses" + "example.com").
3. The Export menu writes whatever the list currently shows.
4. "Hide gibberish" suppresses short runs that are almost certainly coincidence from
   compressed bytes. Leave it on for readable output; turn it off if you're hunting
   for tiny tokens.
5. "Min length" controls how long a run of printable bytes has to be before it
   counts. 6 is a sensible default; raise it to cut noise, lower it to catch shorter
   tokens. Use "Scan again" after changing it.
6. Each row shows the offset, the encoding (ASCII or UTF-16) and the string itself.

## Heads-up on compressed files
If you open a `.docx`, `.xlsx`, `.zip`, `.gz`, `.pdf` and similar, a banner explains
that the strings shown are container metadata — the actual document body lives inside
compressed streams and is not readable from raw bytes. For a Word document, use the
Documents tool instead.

## Limits
Only ASCII and UTF-16 (little-endian) text is found; text in other encodings is not.
A scan stops after 50,000 strings, so on a large image the list can cover only the
start of the file.
""";
}

public sealed partial class GalleryTool
{
    public override string HelpMarkdown => """
## What this is
A thumbnail grid for a folder of pictures. Selecting a picture shows it at full size
with the details stored inside the file: camera make and model, exposure settings,
the date the camera recorded, and the GPS position if there is one.

## When you'd use it
Reviewing photographs from a phone backup, a recovered photos folder, or a "Pictures"
directory. Investigators commonly look for GPS coordinates that show where a picture
was taken and embedded dates that show when.

## How to use it in Cinder
1. Click "Pick folder…" and select a folder. Its subfolders are searched too.
2. Thumbnails appear as they are read, in the order the files are found.
3. Click a thumbnail to see the full picture, its size, pixel dimensions, file
   modified time, GPS position and the camera details.

## Limits
- At most 500 pictures are shown for one folder. When a folder holds more, the status
  line says so; pick a subfolder to see the others.
- Formats Cinder cannot draw (HEIC, AVIF) are listed without a thumbnail or preview,
  but their embedded details are still read where possible.
- "Modified" is the file's last-modified time on disk, which copying a file can
  change. The camera's own date is in the details list.
""";
}

public sealed partial class DocumentsTool
{
    public override string HelpMarkdown => """
## What this is
A reader that pulls the text out of a document and shows it as plain text, without
starting Word, Excel or a PDF program.

## When you'd use it
Reading an attachment without risking a macro running, or getting at the words in a
file when the program that made it is not installed.

## How to use it in Cinder
1. Click "Pick document…" and choose a PDF, Word, Excel, PowerPoint, OpenDocument,
   EPUB, rich-text or plain-text file.
2. The text appears in the tool. If nothing can be extracted, the tool says why.

## Limits
Text only: there is no page layout, no pictures, and no display of author details,
comments or tracked changes. Scanned documents that are pictures of text contain no
text to extract.
""";
}

public sealed partial class FilesystemTool
{
    public override string HelpMarkdown => """
## What this is
A listing of the files and folders inside a disk image. You can see what was on the
original drive without ever starting the suspect's operating system. Cinder reads
NTFS (Windows), FAT (USB sticks, older systems), ext2/3/4 (Linux) and ISO 9660 (CD
and DVD images).

## When you'd use it
This is usually the first thing you do with a disk image: list it and look around.

## How to use it in Cinder
1. Pick a disk image (.dd, .raw, .E01, .aff4, .vhd, .vhdx, .iso).
2. Cinder detects the filesystem — or, for a whole-disk image, walks each partition —
   and lists every file with its size and timestamps.
3. On NTFS volumes it then reads the $MFT for records that are no longer in use and
   appends them with IsDeleted = true, under a `[deleted]/` path. Windows deletes a
   file by clearing one bit; the name, size and timestamps stay in the record until
   it is reused.
4. The filter box narrows by any column. The Export menu writes the rows shown.
5. Known-file filtering: turn on "Hash files during filesystem enumeration" in
   Settings and choose the hash-set database from the Hash sets tool. Every file
   then gets a SHA-1 and a Verdict — the verdict from the hash set, or Unknown.
   Type `Unknown` in the filter box and the catalogued files disappear. Files over
   the size limit are noted as skipped rather than left blank.
6. Select a row and choose "Bookmark" to keep it as a finding; Reports turns
   bookmarks into exhibits.

## Limits
- The listing stops at a fixed number of entries on large volumes. A banner says so
  when it does, and the export then holds only the rows shown.
- A few rows are notes rather than files: `[EWF metadata]`, `[deleted]` and
  `[hashing]` rows carry information about the image or the run.
- A deleted entry gives you the name and the timestamps, not the content. To get
  content back, run the File carver over the image.
- An image with no filesystem Cinder recognises is reported as an error.
""";
}

public sealed partial class RegistryTool
{
    public override string HelpMarkdown => """
## What this is
The Windows Registry is the database where Windows and most programs keep their
settings. It lives in a handful of files called hives: SYSTEM, SOFTWARE, SAM and
SECURITY for the computer, and NTUSER.DAT and UsrClass.dat for each user.

## When you'd use it
Whenever you want to look at a hive directly: installed programs, services, recently
used files, network settings. Several other tools (USB history, Network history,
Shellbags, Amcache, ShimCache) read specific parts of a hive and present them more
conveniently; this tool shows everything.

## How to use it in Cinder
1. Pick a hive file.
2. Each row is one value: the key it is in, the value's name and type, its data, and
   the key's last-write time (when anything in that key last changed).
3. Use the filter box to find a key or value by any text in the row.

## Limits
- The grid stops at 25,000 values and at 8 levels of key nesting. A banner says so
  when either limit is reached; a large SOFTWARE hive is always cut short.
- Value data longer than 256 characters is shortened with "…".
- Deleted keys and values are not recovered.
""";
}

public sealed partial class EventLogTool
{
    public override string HelpMarkdown => """
## What this is
Windows records what happens on a computer — sign-ins, services starting, programs
crashing, security events — in event log files with the extension .evtx. They live
in `Windows\System32\winevt\Logs`.

## When you'd use it
Answering "who signed in and when", "what was installed", "was the log cleared".
Security.evtx holds sign-ins and account changes; System.evtx holds services and
shutdowns; many programs have a log of their own.

## How to use it in Cinder
1. Pick an .evtx file.
2. Each row is one event: time (UTC), channel, provider, event ID, level, computer,
   user and a short description.
3. Type an event ID or any text in the filter box to narrow the list. For example
   4624 is a successful sign-in and 1102 is the security log being cleared.

## Limits
The grid stops at 100,000 events and says so. To see the same events alongside
other evidence in time order, ingest the folder in the Super-timeline tool.
""";
}

public sealed partial class PrefetchTool
{
    public override string HelpMarkdown => """
## What this is
To make programs start faster, Windows keeps a small file for each program it has
run, in `Windows\Prefetch`. As a side effect each .pf file records that the program
ran, how many times, and when.

## When you'd use it
Showing that a program was run on the computer, even if the program itself has since
been deleted.

## How to use it in Cinder
1. Pick the Prefetch folder, or a single .pf file.
2. Each row is one .pf file: the program name, its run count, the last run time, up
   to eight recent run times, and how many files and folders it loaded.

## Limits
- At most 5,000 files are read from one folder; a banner says so if there are more.
- Subfolders are not searched.
- A file that cannot be parsed stays in the list with the reason in its Note column.
- Prefetch files from Windows 10 and 11 are compressed in a way that can only be
  read when Cinder runs on Windows.
""";
}

public sealed partial class ShellbagsTool
{
    public override string HelpMarkdown => """
## What this is
Windows remembers how each folder window was laid out. To do that it keeps a record
of every folder a user has opened in File Explorer — including folders on USB sticks
and network shares, and folders that have since been deleted.

## When you'd use it
Showing that a user browsed to a particular folder, or that a folder existed.

## How to use it in Cinder
1. Pick a user's NTUSER.DAT or UsrClass.dat (on Windows 7 and later most shellbags
   are in UsrClass.dat).
2. Each row is one folder: its path as far as it can be rebuilt, how many subfolders
   are recorded beneath it, and the last-write time of its registry key.

## Limits
- The key last-write time is when the record last changed. It is often, but not
  always, when the folder was last opened.
- Folder names are rebuilt from the stored records; a record in a form Cinder does
  not recognise shows whatever readable text it contains, or the key's number.
- The grid stops at 25,000 folders and says so.
""";
}

public sealed partial class JumplistsTool
{
    public override string HelpMarkdown => """
## What this is
Right-click a program on the Windows taskbar and you see its recent files. That list
is a jump list, stored per program in the user's `Recent\AutomaticDestinations` and
`Recent\CustomDestinations` folders.

## When you'd use it
Showing which files and folders a user opened with a particular program, including
files on removable drives that are no longer connected.

## How to use it in Cinder
1. Pick one of those folders, or a single jump list file.
2. Each row is one entry: which program the list belongs to, the path that was
   opened, the computer name and network card address stored with it, the entry's
   created and last-modified times, and whether it was pinned.

## Limits
- At most 20,000 entries are shown; a banner says so if there are more.
- A file that cannot be parsed stays in the list with the reason in its Note column.
- Programs are identified by an ID; the name is shown only for IDs Cinder knows.
""";
}

public sealed partial class LnkTool
{
    public override string HelpMarkdown => """
## What this is
`.lnk` files are Windows shortcuts. Every desktop icon and every "Recent files"
entry is one. A shortcut stores more than the path it points to: the target's
timestamps when the shortcut was made, the serial number of the drive, and often
the name and network card address of the computer.

## When you'd use it
Linking a file to a particular USB stick (by volume serial number), showing a file
was opened from a network share, or recovering the path of a file that has been
deleted.

## How to use it in Cinder
1. Pick a .lnk file, or a folder of them. Subfolders are searched; a user's Recent
   folder is the usual place to start.
2. Each row is one shortcut: the target path, arguments, working folder, the
   target's created, modified and accessed times as recorded in the shortcut, the
   volume serial number, and the computer name and network card (MAC) address when
   the shortcut carries them.

## Limits
- At most 10,000 shortcuts are read; a banner says so if there are more.
- Not every shortcut records a computer name or MAC address; those cells are blank
  when it does not.
- A file that cannot be parsed stays in the list with the reason in its Note column.
""";
}

public sealed partial class BrowserHistoryTool
{
    public override string HelpMarkdown => """
## What this is
Web browsers keep a database of the addresses a user has visited. Chrome, Edge,
Brave and Opera keep it in a file named History; Firefox uses places.sqlite.

## When you'd use it
Seeing what a user looked at and searched for, and when they last visited it.

## How to use it in Cinder
1. Pick a browser profile folder — or the folder above several profiles — or a
   single History or places.sqlite file.
2. Each row is one address: the browser, which database it came from (so two
   profiles can be told apart), the last visit time (UTC), the address, the page
   title and how many times it was visited.

## Limits
- One row per address, with its most recent visit. Individual earlier visits are
  not listed.
- At most 50,000 addresses are read from one database and 100,000 in total, newest
  first. A banner says so when either limit is reached.
- Cinder reads a private copy of each database, including its write-ahead log when
  one is present, and never writes to the original.
- The browser is named from the folder path. A profile copied somewhere unusual is
  labelled as a Chromium-based browser that could not be identified.
- Downloads, cookies, saved sign-ins and cached pages are not shown.
""";
}

public sealed partial class UsbHistoryTool
{
    public override string HelpMarkdown => """
## What this is
Windows keeps a record of every USB storage device that has been connected: its
maker, model and — for most devices — its serial number. The record is in the
SYSTEM registry hive.

## When you'd use it
Showing that a particular USB stick or drive was connected to a particular computer.

## How to use it in Cinder
1. Pick the SYSTEM hive (`Windows\System32\config\SYSTEM`).
2. Each row is one device in one control set: the device class (maker and model),
   the instance name (usually the serial number), the friendly name and description,
   and the last-write time of the device's registry key.

## Limits
- The key last-write time is when Windows last changed that key. It is not a first
  or last connection time, and recent versions of Windows rewrite these keys during
  updates. Connection times need other sources, such as the setup log and event
  logs, which this tool does not read.
- Drive letters and the user who connected the device are not shown.
- An instance name with "&" as its second character was generated by Windows because
  the device has no serial number of its own; it does not identify one device.
- A hive without control sets is reported as "not a SYSTEM hive". A SYSTEM hive with
  no USB storage key is reported as having no devices recorded.
""";
}

public sealed partial class WifiHistoryTool
{
    public override string HelpMarkdown => """
## What this is
Windows keeps a profile for every network a computer has joined, wireless or wired,
in the SOFTWARE registry hive. Each profile has the network's name and the dates the
computer first and most recently connected to it.

## When you'd use it
Showing that a computer was connected to a particular network — a home network, a
hotel, a company guest network — and when.

## How to use it in Cinder
1. Pick the SOFTWARE hive (`Windows\System32\config\SOFTWARE`).
2. Each row is one network profile: its name and description, whether it is
   wireless, wired or mobile broadband, the category Windows gave it (Public,
   Private or Domain), the first and last connection times, and the last-write time
   of the profile's registry key.

## Limits
- The first and last connection times are stored in the computer's local time, with
  no time zone. They are shown exactly as stored; work out the zone from the SYSTEM
  hive before comparing them with UTC times.
- The access point's hardware address (BSSID) and the security type are not shown.
- A hive that is not a SOFTWARE hive is reported as an error.
""";
}

public sealed partial class SrumTool
{
    public override string HelpMarkdown => """
## What this is
SRUM (System Resource Usage Monitor) is a Windows database that records, roughly
hourly, how much processor time and network traffic each program used and under
which user account. It lives at `Windows\System32\sru\SRUDB.dat` and keeps about a
month of history.

## When you'd use it
Showing that a program ran or moved data on a given day, even if the program is
gone — for example how many bytes a file-transfer tool sent.

## How to use it in Cinder
1. Pick SRUDB.dat.
2. Each row is one record from one of three tables, named in the Table column:
   "app" (processor time in the foreground and background), "net" (bytes sent and
   received) or "energy". The row gives the time (UTC), the user, the program and
   the values.

## Limits
- At most 25,000 rows are read from each table. A banner says so when a table holds
  more.
- Reading this database uses a Windows component, so the tool works only on Windows.
- A database copied from a running computer may be marked as not cleanly shut down
  and refuse to open. The error says so; repair a copy with `esentutl /p` and open
  the repaired copy.
- Other SRUM tables (network connections, push notifications) are not read.
""";
}

public sealed partial class AmcacheTool
{
    public override string HelpMarkdown => """
## What this is
Amcache is a registry hive in which Windows records program files it has seen, with
each file's path, SHA-1 hash, publisher and version. It lives at
`Windows\AppCompat\Programs\Amcache.hve`.

## When you'd use it
Confirming a program file was present on the computer and identifying exactly which
file it was by its hash — even after the file has been deleted.

## How to use it in Cinder
1. Pick Amcache.hve.
2. Each row is one file: name, path, SHA-1, publisher, program ID, version, size and
   the last-write time of its registry key.
3. Copy a SHA-1 into the Hash sets or VirusTotal tool to see whether it is known.

## Limits
- An entry shows the file was present and noticed by Windows. On its own it does not
  prove the program was run, and the tool shows no execution time.
- The key last-write time is when the entry was last updated, not when the file
  first appeared.
- Windows hashes only the first 31 MB of a large file, so the SHA-1 of a bigger file
  will not match a hash of the whole file.
- At most 25,000 entries are shown; a banner says so if there are more.
""";
}

public sealed partial class ShimcacheTool
{
    public override string HelpMarkdown => """
## What this is
ShimCache (the Application Compatibility Cache) is a list Windows keeps of program
files it has examined to decide whether they need a compatibility fix. It is stored
in the SYSTEM registry hive.

## When you'd use it
Showing that a program file existed at a particular path, including files that have
since been deleted and files on drives that are no longer attached.

## How to use it in Cinder
1. Pick the SYSTEM hive (`Windows\System32\config\SYSTEM`).
2. Each row is one entry: the control set it came from, its position in the cache
   (0 is the most recently added), the file's path, and the file's own last-modified
   time as Windows recorded it.

## Limits
- The time shown is the file's last-modified time, not the time it was run or added
  to the cache. On Windows 10 and 11 an entry does not prove the program was run.
- The cache is written to the hive when Windows shuts down, so activity since the
  last shutdown is missing from a hive taken from a running computer.
- Windows 8, 8.1, 10 and 11 are supported. A cache from an older version of Windows
  is reported as one that could not be decoded.
""";
}

public sealed partial class RecycleBinTool
{
    public override string HelpMarkdown => """
## What this is
When a file is sent to the Recycle Bin, Windows moves it into a hidden `$Recycle.Bin`
folder and writes a small companion file whose name starts with `$I`. That file
records where the item came from, how big it was and when it was deleted. The
content itself sits beside it under the same name starting with `$R`.

## When you'd use it
Showing what a user deleted, from where and when — including when the content has
since been emptied but the `$I` record has been recovered.

## How to use it in Cinder
1. Pick a `$Recycle.Bin` folder, one user's folder inside it, or a single `$I` file.
2. Each row is one deleted item: the owner (the user's security identifier, taken
   from the folder name), the original path, the original size, the deletion time
   (UTC), and whether the matching `$R` content is present beside it.

## Limits
- The owner is shown as a security identifier, not a user name.
- The tool lists deletion records. It does not open or restore the deleted content.
- A record that cannot be read stays in the list with the reason in its Note column.
""";
}

public sealed partial class EmailTool
{
    public override string HelpMarkdown => """
## What this is
A list of the messages in a mail file: who sent each one, to whom, the subject and
when. It reads single messages (.msg from Outlook, .eml from most other programs),
MBOX mailboxes, and — with an optional extra component — Outlook .pst and .ost
mailboxes.

## When you'd use it
Getting an overview of a mailbox or a saved message: who was writing to whom, about
what, and when.

## How to use it in Cinder
1. Pick a .msg, .eml, .mbox, .pst or .ost file.
2. Each row is one message: sender, recipients (To and Cc), subject, sent time, and
   for single messages the file size and number of attachments.
3. Filter by any text, export the list, or bookmark a row.

## Limits
- Headers only: message bodies are not shown, and attachments cannot be opened or
  saved from here.
- .pst and .ost files need Python with the libpff-python package installed. Without
  it the tool says what to install. For these mailboxes the list shows folder,
  sender name, subject, delivery time and attachment count; recipients are not read.
- For an MBOX file the sent time is the message's Date header exactly as written,
  including its time zone; it is not converted to UTC.
- At most 50,000 messages are shown; a banner says so if there are more.
""";
}

public sealed partial class LinuxArtifactsTool
{
    public override string HelpMarkdown => """
## What this is
A reader for the plain files a Linux system keeps about its users and its activity:
sign-in records, the system and authentication logs, each user's shell command
history, and the account files.

## When you'd use it
Investigating a Linux server or workstation: who signed in and from where, what
commands they ran, which accounts exist.

## How to use it in Cinder
1. Pick the top folder of a mounted Linux filesystem or a triage collection — the
   folder that contains `var`, `etc` and `home` — or pick a single log file.
2. From a folder, Cinder reads these files when they are present: `var/log/auth.log`
   or `secure`, `var/log/syslog` or `messages`, `var/log/wtmp` and `btmp` (sign-ins
   and failed sign-ins), `etc/crontab`, `etc/passwd`, `etc/shadow`,
   `etc/ssh/ssh_known_hosts`, and the bash, zsh, fish and Python history of every
   user under `home` and of root.
3. Each row is one line or record: the file it came from, a category, the time, what
   kind of time that is, the user where known, and the text.

## Reading the time columns
- "UTC" — the record carried a time zone or is stored in UTC, and has been converted.
- "log's local time" — a full date and time with no zone: the clock of the computer
  that wrote the log.
- "no year in log" — older log formats write only month, day and time. Cinder shows
  them as `MM-DD HH:MM:SS` and does not guess the year.
- Shell history, account files and scheduled-task files have no time per line.

## Limits
- All files are shown in one list; use the filter box (for example the category
  name) to look at one kind.
- The systemd journal, rotated or compressed logs, per-user scheduled tasks, package
  manager logs and the trash folder are not read.
- At most 50,000 rows are shown; a banner says so if there are more.
""";
}

public sealed partial class MemoryTool
{
    public override string HelpMarkdown => """
## What this is
A list of the programs that were running on a Windows computer at the moment its
memory was captured, arranged as a tree: each process under the process that
started it. Cinder gets the list by running Volatility 3, a separate open-source
memory analysis program.

## When you'd use it
Looking for a process that should not be there, or an ordinary process started by an
unusual parent — a command prompt started by a document viewer, for example.

## How to use it in Cinder
1. Install Volatility 3 for the Python that Cinder uses:
   `python -m pip install volatility3`.
2. Pick a Windows memory image (.raw, .dmp, .vmem).
3. Each row is one process: its ID, its parent's ID, its depth in the tree (the name
   is indented to match), thread and handle counts, start and exit times, and its
   path and command line when Volatility reports them.

## Limits
- Only the process tree is available. Network connections, loaded modules, injected
  code and credential extraction are not offered here; run Volatility directly for
  those.
- Windows memory images only.
- The first run on an image can take several minutes, and Volatility may need
  internet access to download symbol files. Use "Stop" to end a run.
- If Volatility fails, its own error message is shown.
""";
}

public sealed partial class NetworkTool
{
    public override string HelpMarkdown => """
## What this is
A packet capture (.pcap or .pcapng) is a recording of the data that crossed a
network connection. This tool lists the packets in one.

## When you'd use it
Seeing which addresses a computer talked to, on which ports and when.

## How to use it in Cinder
1. Pick a .pcap or .pcapng file.
2. Each row is one packet: its number in the file, time (UTC), protocol, source and
   destination address and port, size, and for TCP the flags that were set.
3. Filter by an address or port to follow one conversation.

## Limits
- This is a packet list. Conversations are not reassembled, and web requests, name
  lookups and transferred files are not extracted.
- The tool needs a packet-capture library installed on this computer: Npcap on
  Windows, libpcap on Linux and macOS. Without it the tool says so.
- At most 50,000 packets are shown; a banner says so if the capture holds more. If
  the file is cut short or damaged, the status line says where reading stopped.
""";
}

public sealed partial class MobileTool
{
    public override string HelpMarkdown => """
## What this is
A list of the files held inside a phone or tablet backup: an iPhone or iPad backup
folder made by Finder or iTunes, or an Android backup file (.ab) made with adb.

## When you'd use it
Finding out what a backup contains — which apps have data in it, and where a
particular database or file is — before extracting anything.

## How to use it in Cinder
1. For an iPhone or iPad, pick the backup folder: the one that contains Manifest.db.
   For Android, pick the .ab file.
2. Each row is one file in the backup. For an iPhone backup: the domain (which app
   or part of the system it belongs to), whether it is a file, folder or link, its
   path on the device, and the name it is stored under inside the backup folder.
   For an Android backup: the app package, the path, the modified time and the size.

## Limits
- This is a listing. Messages, call logs and app data are not decoded, and files are
  not extracted.
- Encrypted backups cannot be opened; the tool says so.
- At most 50,000 files are listed; a banner says so if the backup holds more.
""";
}

// ----- ANALYZE -----------------------------------------------------------------------

public sealed partial class TimelineTool
{
    public override string HelpMarkdown => """
## What this is
One list, in time order, of the timestamped events Cinder can read from a folder of
evidence: sign-ins and other event log records, programs run, shortcuts created,
web visits, mail sent, files deleted, and NTFS journal records.

## When you'd use it
Usually second, right after listing the disk image. A timeline turns "what happened"
into a question you answer by scrolling.

## How to use it in Cinder
1. Click "Ingest folder…" and choose a folder of evidence: a triage collection, a
   mounted image's Windows folder, an exported user profile. Cinder reads the event
   logs, Prefetch files, shortcuts, NTUSER.DAT hives, browser history, mail files,
   Recycle Bin records and extracted `$J` / `$LogFile` files it finds there. You can
   ingest more folders; their events are added to the same timeline.
2. Set the From and To dates and the filters: user, source (part of a source name,
   several separated by commas), text in the summary, or an ATT&CK technique.
3. Press Enter in a filter box, or the refresh button, to apply them. Until you do,
   the grid keeps showing the previous filter and a note says the filters changed.
4. Select an event and choose "Bookmark" to keep it as a finding.

## ATT&CK tags
Events are tagged when they are read, where the mapping is defensible: Windows
Security event IDs (4624 sign-in → T1078 Valid Accounts, 4698 → T1053.005 Scheduled
Task, 7045 → T1543.003 Windows Service, 1102 → T1070.001 Clear Event Logs, and so
on), Sysmon (8 → T1055 Process Injection), PowerShell script-block logging (4104 →
T1059.001), Prefetch and UserAssist execution evidence (TA0002 Execution), Recycle
Bin deletions (T1070.004). Type a prefix such as `T1053` in the ATT&CK box to see
every matching event. A generic process-creation event is deliberately not tagged.

## Export
The Export menu writes every event matching the filter the grid is showing — all of
them, not just the rows on screen — as Timesketch JSONL, Timesketch CSV, or the
Sleuth Kit body format that mactime reads. If you have changed a filter box without
applying it, the export still uses the applied filter and says so. Every export is
recorded in the case's custody log.

## Limits
- The grid shows the first 5,000 matching events, earliest first, and the status
  line says how many matched in all.
- The timeline lives in memory. It is not saved in the case; ingest the folders
  again after restarting Cinder.
- Ingesting the same folder twice adds its events twice. "Clear the timeline" in the
  "…" menu starts over.
- While a folder is being read, refresh and export wait; "Stop" abandons the read
  and leaves the timeline as it was.
""";
}

public sealed partial class MapTool
{
    public override string HelpMarkdown => """
## What this is
A list of places taken from evidence: the GPS position stored in each photo in a
folder, plus any points you type in yourself.

## When you'd use it
Placing a device at a location at a time, from the pictures taken with it.

## How to use it in Cinder
1. Click "Add photos from folder…" and choose a folder. Every photo that carries a
   GPS position becomes a row.
2. Each row has the latitude and longitude, a time, and where that time came from:
   the GPS fix (UTC), the camera's clock (no time zone), or the file's modified time
   when the photo has neither.
3. "Add a point" adds a location by hand. It has no time.
4. The Export menu writes the list as CSV or JSON, or as KML to open in a map
   program such as Google Earth.

## Limits
- This tool lists positions; it does not draw a map.
- Only photo GPS data is read. Positions from Wi-Fi networks, browsers or cell towers
  are not collected.
- One folder adds at most 25,000 points, and the status line says so when it stops.
""";
}

public sealed partial class GraphTool
{
    public override string HelpMarkdown => """
## What this is
A who-wrote-to-whom view built from mail files: a list of every address found, with
how many messages each sent and received, and the messages between them.

## When you'd use it
Getting oriented in a large set of mail: who are the most active people, and who was
in contact with a particular address.

## How to use it in Cinder
1. Click "Add mail from folder…" and choose a folder of .eml, .msg or .mbox files.
   Subfolders are searched.
2. The people list shows every address, most-connected first. Select one to see only
   the messages to or from it; "Show everyone" clears the selection.
3. "Add a link" records a contact by hand. It has no time.
4. The Export menu writes the message list.

## Limits
- Lists, not a drawn graph.
- Mail files only: chat logs and Outlook .pst mailboxes are not read.
- One folder adds at most 25,000 sender-to-recipient links, and the status line says
  so when it stops.
""";
}

public sealed partial class SearchTool
{
    public override string HelpMarkdown => """
## What this is
A text search across every file in a folder. Cinder reads the files once and builds
an index; after that, searches come back straight away.

## When you'd use it
Looking for a name, an email address, an account number or a phrase across a whole
folder of evidence at once.

## How to use it in Cinder
1. Click "Index a folder…", choose the folder to read, and choose an empty folder to
   keep the index in. Documents are indexed by their text; other files by the
   readable strings inside them.
2. Type what you are looking for and press Enter. Use "quotes" for a phrase and
   AND / OR between words.
3. Each result shows the file, where it is, and the matching text. An index built
   earlier can be reopened with "Open existing index…".

## Limits
- Only the folders you index are searched. Rows shown in other tools — registry
  values, event log entries, browser history — are not in the index unless the
  files they came from are in a folder you indexed, and then only as raw text.
- Very large files are skipped, and the status line reports how many.
- A search shows a limited number of results and says so when there are more.
""";
}

public sealed partial class HashSetsTool
{
    public override string HelpMarkdown => """
## What this is
A database of known file hashes. The best known source is NSRL, the list NIST
publishes of files that belong to ordinary software, which lets you set aside the
hundreds of thousands of system files on a computer and look at what is left.

## When you'd use it
Cutting the noise in a file listing, or checking whether one hash is already known.

## How to use it in Cinder
1. "New hash set…" creates a database file; "Open hash set…" opens one you made
   earlier. Cinder remembers the choice.
2. "Import NSRL list…" reads an NSRL minimal CSV file into it.
3. Paste a hash and choose "Look up" to see whether it is in the database.
4. To use the database on a whole disk image, turn on "Hash files during filesystem
   enumeration" in Settings. The Filesystem tool then gives every file a SHA-1 and a
   verdict, and typing `Unknown` in its filter box hides everything the database
   knows.

## Limits
Only the NSRL minimal CSV layout can be imported. A lookup needs the exact hash.
""";
}

public sealed partial class IocMatchTool
{
    public override string HelpMarkdown => """
## What this is
Takes a list of indicators — file hashes, IP addresses, domains, URLs, email
addresses, file names, any text — and checks all of them against a folder of
evidence at once, four ways: file hashes, file paths, text inside files (both ASCII
and UTF-16), and the timestamped events the timeline can read from the folder.

## When you'd use it
The first hour of an incident, when you have been handed a list and the question is
"is any of this on the computer". Also at the end, to confirm a cleaned computer no
longer has it.

## How to use it in Cinder
1. Pick the indicator list: a text file with one indicator per line. Lines starting
   with `#`, blank lines and `type,value` rows are accepted. Cinder works out what
   kind each line is and says how many of each it found.
2. Pick the folder to scan: a triage collection, a mounted image, an export.
3. Run the scan. Each match is a row: the indicator, its kind, where it matched
   (hash, path, content or timeline), the file, and the offset or time.
4. Filter, export, or bookmark a row as a finding.

## Limits
- Files over 256 MB are not hashed and files over 512 MB are not searched for text.
- The scan stops at 200,000 files and 50,000 matches, and says so.
- A hash indicator must be the exact hash.
- Changing the list or the folder clears the results; run the scan again.
""";
}

public sealed partial class YaraTool
{
    public override string HelpMarkdown => """
## What this is
YARA is a language for describing patterns in files: "this text, or these bytes".
This tool scans one file with rules you write or load, and lists where they matched.

## When you'd use it
Checking a file for known malware strings, or for any text or byte pattern you can
write down.

## How to use it in Cinder
1. Write rules in the editor, or use "Load rules file…".
2. Click "Scan a file…" and pick the file.
3. The results list each match with its rule, the string that matched and the
   offset. A second list says what each rule did: matched, did not match, or was
   skipped and why.

## Limits
- Cinder supports part of the YARA language: text strings (with `nocase`, `wide` and
  `ascii`), hex strings without wildcards or jumps, and conditions built from the
  strings (`any of them`, `all of them`, `2 of them`, `$a and $b`). Regular
  expressions, modules, file-size tests and other conditions are not supported; a
  rule that needs them is skipped and listed as skipped.
- One file at a time. Memory and unallocated space can be scanned only as a file.
- A scan stops after a fixed number of matches and says so.
""";
}

public sealed partial class VirusTotalTool
{
    public override string HelpMarkdown => """
## What this is
A lookup that sends a file's hash — never the file — to VirusTotal and shows how
many antivirus products there have flagged a file with that hash.

## When you'd use it
Checking whether a suspicious file is already known, without uploading anything.

## How to use it in Cinder
1. Paste your VirusTotal API key. It is kept only while Cinder is running.
2. Paste an MD5, SHA-1 or SHA-256 hash and choose "Look up on VirusTotal".
3. The result shows how many products called it malicious, suspicious, harmless or
   undetected, and when VirusTotal first saw and last analysed it.

## Limits
- One hash at a time.
- "No record" means VirusTotal has never seen that hash. It does not mean the file
  is safe.
- Free API keys allow only a few lookups a minute and a limited number a day.

## Privacy
Only the hash leaves your computer. The hash itself can reveal that you hold a
particular file, which matters when the file is unique to your case.
""";
}

public sealed partial class AiCopilotTool
{
    public override string HelpMarkdown => """
## What this is
A chat window connected to a language model that you provide: one running on your
own computer through Ollama or LM Studio, or a hosted service that offers an
OpenAI-compatible interface.

## When you'd use it
As a research aid for unfamiliar material: "what does event 4769 mean?", "what does
this PowerShell command do?". Paste the text you are asking about into the question.

## How to use it in Cinder
1. Choose where the model runs, enter its address in full (for example
   `http://localhost:11434`) and the model name. A hosted service also needs your
   API key.
2. "Test connection" checks that the address answers.
3. Type a question and choose "Send". The conversation so far is sent with each
   question, so follow-up questions work.

## Limits
- Cinder does not send evidence to the model by itself. The model sees only what you
  type or paste.
- A language model can be confidently wrong. Check anything it tells you against the
  evidence before relying on it.

## Privacy
With a model on your own computer nothing leaves it. With a hosted service,
everything you type or paste is sent to that service under its terms.
""";
}

// ----- ACQUIRE -----------------------------------------------------------------------

public sealed partial class ImagerTool
{
    public override string HelpMarkdown => """
## What this is
A tool that copies an entire physical disk, or an existing image, into a single
forensic image file, hashing every byte on the way. Once imaged, you do all
analysis against the copy and leave the original alone.

## When you'd use it
This is step zero of nearly every disk investigation: image first, never analyse
the original.

## How to use it in Cinder
1. Connect the source drive through a hardware write-blocker. Cinder opens the
   source for reading only, but it does not stop Windows or other programs from
   writing to a drive that is connected without one.
2. Give the source: a device (`\\.\PhysicalDrive1` on Windows, `/dev/sdb` on
   Linux — both need Administrator / root), a plain file, or an E01 (which is
   decoded as it is read, so this doubles as E01 → raw conversion).
3. Pick the destination and format.
   - **Raw**: MD5 / SHA-1 / SHA-256 are computed as the bytes are read, a `.sha256`
     companion file (sha256sum format, so the Verify tool reads it) and a
     `.log.json` acquisition log are written beside the image, and the run is
     recorded in the custody log.
   - **E01**: EnCase 6 layout, 32 KiB chunks, zlib compression, 1.5 GB segments
     (`.E01`, `.E02`, …), case details in the header, MD5 + SHA-1 in the
     container, unreadable sectors in its error section.
   - **AFF4**: AFF4 Standard v1.0 ZIP volume, zlib-compressed 32 KiB chunks,
     MD5 / SHA-1 / SHA-256 and case details recorded inside.
   - **VHD / VHDX**: dynamic virtual disks that Hyper-V or Disk Management can
     attach. These need the source size up front, so a device that does not
     report one must go to raw or E01.
4. Read errors: Cinder retries, then re-reads the failing block one sector at a
   time so a bad sector costs 512 zero bytes rather than a megabyte. Every
   zero-filled sector is counted, and its offset is listed in the log.

5. Stopping and failures: while it runs, the image is written under the output
   name plus `.partial` and renamed when it finishes, so nothing unfinished
   ever sits at the final name. **Stop** deletes the partial file. If the source
   stops responding (a failing or unplugged disk), imaging ends, the partial
   file is kept, and the log beside it is marked incomplete: it is real data
   from a dying disk, but it is not a full image and must not be treated as one.

## What the hashes mean
They are the hash of what was *written*, which — if there were bad sectors — is
the hash of the zero-filled image, not of the original media. The log says how
many sectors were substituted; put that number in your report.

E01, VHD and VHDX store whole sectors. If the source is not a whole number of
sectors (a single file, for example), zeros fill the last one, and the hashes
cover the image as stored, padding included, so that Verify and other tools
agree with them. The log also records the hashes of the source bytes alone.

If the source is itself an E01 or AFF4 with damaged chunks, those regions are
zeros in the new image and the result says how many chunks were affected.
""";
}

public sealed partial class VerifyTool
{
    public override string HelpMarkdown => """
## What this is
After an image is created, this tool re-reads it and recomputes the hashes to
confirm the image still matches what was acquired. It is the check that catches
storage corruption and alteration.

## When you'd use it
Before every analysis session, or any time an image has been copied, moved, or
sat on slow storage for a while.

## How to use it in Cinder
1. Pick the image file.
2. Cinder reads every block and compares against a reference hash. For an E01
   that reference is the MD5 / SHA-1 the acquisition tool wrote inside the
   container; for an AFF4 container it is the MD5 / SHA-1 / SHA-256 recorded
   inside it; for a raw image it is a .sha256 / .sha1 / .md5 companion file, or a
   SHA256SUMS line naming the image, sitting next to it.

## Reading the result
There are three distinct outcomes, and the difference matters.

- Verified — a reference hash existed and the bytes on disk reproduce it.
- VERIFICATION FAILED — a reference existed and the bytes do not match it, or
  chunks were too damaged to decode. Treat the image as unreliable and go back
  to the source media.
- Unverifiable — there was nothing to compare against. This is not a pass. It
  means the container recorded no acquisition hash and no companion file was
  found, so nothing about the image's integrity has been established.

## Why the recorded hash isn't enough
The Filesystem tool shows an E01's recorded MD5 and SHA-1 in its metadata row,
marked UNVERIFIED. Those are the hashes the container claims about itself — an
assertion written by whatever produced the file, which a tampered or corrupt
image will keep displaying. Only this tool re-reads the media and turns that
claim into a finding.
""";
}

public sealed partial class MountTool
{
    public override string HelpMarkdown => """
## What this is
A way to attach an image file to this computer as a read-only drive, so other
software can browse it.

## When you'd use it
When you need to run another program that cannot read forensic image formats.

## How to use it in Cinder
1. Click "Pick image and mount read-only…" and choose the image.
2. The tool shows where the image was mounted.

## Limits
- Which image formats can be mounted depends on what is installed on this computer:
  Windows can attach VHD, VHDX and ISO files by itself; other formats need extra
  software, and the tool reports it when the mount fails.
- Cinder cannot unmount the image again. Detach it with the operating system's own
  tools (Disk Management or "Eject" on Windows, `umount` on Linux).
- Mounting is not available on macOS.
""";
}

public sealed partial class ConvertTool
{
    public override string HelpMarkdown => """
## What this is
Conversion between forensic image formats — raw, E01, AFF4, VHD, VHDX — in any
direction. The source is read through whichever reader understands it, hashed as it
is read, and written in the target format; the finished output is then re-read and
checked against that hash.

## When you'd use it
When the tool you need next only understands raw images and your evidence arrived
as E01; or when a raw `.dd` has to travel as a compressed, self-hashing E01 instead.

## How to use it in Cinder
1. Pick the source (for an E01, its first segment — the rest are followed) and the
   target format.
2. Pick the output path. A `.log.json` is written beside it; raw output also gets
   a `.sha256`.
3. Run. The result line reports two checks:
   - whether the output, re-read, hashes back to what was written. If it does not,
     do not use the output.
   - when the source was a container with a recorded acquisition hash, whether the
     source matched it. If it did not, do not trust either file until you know why.
   If the source container recorded no hash, the output is still a faithful decode
   of it, but nothing ties it to the original media.
4. The conversion is recorded in the custody log with the hashes.
""";
}

public sealed partial class ShadowCopyTool
{
    public override string HelpMarkdown => """
## What this is
Windows periodically takes a snapshot of a drive, called a Volume Shadow Copy.
Snapshots often still contain files that have since been changed or deleted.

## When you'd use it
Finding out which snapshots exist on the computer Cinder is running on, as a first
step before examining them with other tools.

## How to use it in Cinder
Click "List snapshots". Each row is one snapshot with the volume it belongs to and
when it was created.

## Limits
- Lists the snapshots of this computer only. Snapshots inside a disk image are not
  read, and a snapshot cannot be opened or mounted from here.
- Listing snapshots may need Cinder to be run as Administrator.
""";
}

public sealed partial class RamCaptureTool
{
    public override string HelpMarkdown => """
## What this is
Capturing the memory of a running computer preserves what is lost at shutdown:
running programs, network connections, passwords held in memory.

## Status
Cinder cannot capture memory yet. This screen does not capture anything.

## What to do instead
Capture memory with a dedicated program — WinPmem on Windows, LiME on Linux — run
from removable media so nothing is written to the suspect drive. Then open the
resulting file in the Memory tool.
""";
}

public sealed partial class CarverTool
{
    public override string HelpMarkdown => """
## What this is
File carving recovers files by recognising the bytes that mark the start (and for
many formats the end) of a file, without relying on the filesystem. It finds files
whose directory entries are gone.

## When you'd use it
Recovering deleted pictures, documents and archives from a disk image, a memory
dump or any other file.

## How to use it in Cinder
1. Pick what to carve from, and an empty folder to save recovered files in. Do not
   save onto the evidence drive.
2. Click "Carve". Each recovered file is listed with its type, where in the source
   it was found, its size and the name it was saved under.

## Limits
- All built-in file types are searched for; there is no way to choose a subset.
- A file stored in separate pieces on the disk is recovered only up to the end of
  its first piece, and may not open.
- A run stops after a fixed number of recovered files and says so.
- Recovered files have no original names, folders or timestamps.
""";
}

public sealed partial class CloudPullTool
{
    public override string HelpMarkdown => """
## What this is
The start of a tool for collecting files from a Google Drive, OneDrive or Dropbox
account with the account holder's consent.

## Status
Cinder cannot download from cloud accounts yet. This screen only builds the sign-in
link for the provider you choose, using the client ID entered in Settings. Signing in
with that link does not download anything.

## What to do instead
Use the provider's own export (Google Takeout, a OneDrive or Dropbox folder
download) and examine the downloaded files with the other tools.
""";
}

// ----- CASE --------------------------------------------------------------------------

public sealed partial class CasesTool
{
    public override string HelpMarkdown => """
## What this is
A case is a single file (ending in .cinder) that holds your bookmarks and the
custody log: the record of what was done during the examination. This tool creates
case files and reopens recent ones.

## When you'd use it
At the start of an examination, and each time you come back to it.

## How to use it in Cinder
1. "New case…" creates a case file where you choose.
2. "Open case…" opens an existing case file; recent cases are listed for one-click
   reopening.
3. The open case is shown at the top of the window. Several cases can be open at
   once; tools record their activity in whichever case is selected there.

## Limits
- Evidence is not copied into the case. The case records which files were examined
  and what was found, so keep the evidence where the paths in the log still lead.
- Without an open case nothing is logged and rows cannot be bookmarked.
""";
}

public sealed partial class ReportsTool
{
    public override string HelpMarkdown => """
## What this is
A report builder: you write the sections, Cinder adds your bookmarked findings as
numbered exhibits and exports the result as Markdown, HTML, PDF, Word or JSON.

## When you'd use it
At the end of an examination, to produce the document you hand over.

## How to use it in Cinder
1. Pick a template. It sets the section headings; add or remove sections freely.
   Changing the template keeps the text you have already written.
2. Write each section. Plain text works; Markdown is supported.
3. With "Include in the report" ticked, the open case's bookmarks become an Exhibits
   section: one numbered exhibit per bookmark, with your note, every column of the
   row, who bookmarked it and when. The bookmarks are read from the open case when
   you open this tool, when you switch case, and again immediately before each
   export.
4. "Update preview" shows the report as Markdown. Export writes the file.

## Limits
- The custody log is not included in the report. Each export is recorded in the
  log; view and check the log in Chain of custody.
- The text you write is not saved in the case. Export before closing Cinder.
- Whether a report is fit for a court or a client is your judgement, not Cinder's:
  read the exported document before you sign it.
""";
}

public sealed partial class CustodyTool
{
    public override string HelpMarkdown => """
## What this is
The chain of custody log is the case's activity record. Each entry is hashed and
chained to the one before it, so an entry that is edited, deleted, reordered or
inserted breaks the chain and the check says so.

## What gets recorded
- Case created and case opened (with computer name and Cinder version).
- Every parser run: which tool, which evidence file, how many rows came back, and
  whether the grid was cut short at its limit.
- Every image verification, imaging run and conversion, with hashes and verdict.
- Every mount, every data export (which tool, format, path, row count) and every
  report export (title, template, format, sections).
- Bookmarks added or deleted, and manual hashes from the Hash dialog.

Nothing is recorded while no case is open.

## How to use it in Cinder
1. "Check the open case" (or "Check a case file…") recomputes the chain and lists
   every entry.
2. "Sign the log" at milestones: the end of a session, before handing the case
   over, before writing the report. Cinder signs the latest entry — its sequence
   number, hash and the time — with an ECDSA P-256 key created on first use in your
   user profile. The signature, public key included, is stored in the case file.
3. Optional but recommended: set a Time-Stamp Authority in Settings. Signing then
   also sends a SHA-256 of the signature to that RFC 3161 service and stores its
   countersigned token in the case file. Your key says who; the authority says no
   later than when, from a clock you do not control.
4. "Export signature…" writes the signature to a file. Send it somewhere you cannot
   edit afterwards, such as an email to a supervisor or the case ticket.

## What it proves
The chain alone catches accidental corruption and naive tampering. It does not
resist a deliberate rewrite: the hash is unkeyed and lives in the same file as the
entries, so anyone who can write the case file can recompute the whole chain and
the check will still say "intact".

A signature changes that. After a rewrite the entry at the signed sequence no
longer carries the signed hash, and re-signing needs the private key — so nobody
without your key can alter the log up to that point without the signature failing.
The remaining gap is you, the key holder; that is what sending the exported
signature out of your own reach is for. SECURITY.md has the full reasoning.
""";
}

public sealed partial class WorkflowsTool
{
    public override string HelpMarkdown => """
## What this is
A workflow is a saved list of steps that Cinder runs in order, so the same routine
can be repeated on every case. Workflows are JSON files.

## When you'd use it
For work you do the same way every time — hash the image, list its files, carve it,
write a summary — and want to be able to show was done the same way.

## How to use it in Cinder
1. "Open workflow…" loads a workflow file and lists its steps in the order they
   will run.
2. "Run" executes them. Each step that completes adds a line with what it did.

## Steps available
- open-image — checks that a file exists and passes it to later steps.
- hash — SHA-256 of a file.
- registry — opens a registry hive and counts the keys under its root.
- fs-enumerate — counts the files and folders in a disk image.
- carve — recovers files by signature into a folder.
- report — writes a short Markdown file listing what each step returned.
- index — adds each step's result to a search index.
- ai-summary — not available yet; it is reported as skipped.

## Limits
- There is no editor: workflow files are written by hand.
- If a step cannot do its work the run stops there, and the message names the step
  and the reason. Later steps do not run.
- Results are not written to the case's custody log.
""";
}

public sealed partial class PluginsTool
{
    public override string HelpMarkdown => """
## What this is
A loader for add-ons: program files (.dll) written against Cinder's plugin interface.

## When you'd use it
When someone has written an add-on for a format Cinder does not read.

## How to use it in Cinder
1. Click "Load plugins from folder…" and choose the folder that holds them.
2. Each file is listed with its SHA-256, who signed it, and whether it was loaded,
   refused as untrusted, or failed, with the reason.

## Limits
- A plugin runs with the same access to this computer as Cinder itself. Load only
  plugins from people you trust.
- Loading a plugin does not add a tool to the rail in this version.
- Python scripts are not loaded.
""";
}

public sealed partial class SettingsTool
{
    public override string HelpMarkdown => """
## What this is
Where Cinder's preferences are kept: appearance, where Python is, which language
model the AI Copilot uses, the client IDs for cloud sign-in, the hash-set database
and file-hashing options used by the Filesystem tool, and the time-stamp service
used when signing the custody log.

## When you'd use it
Once on first use, and whenever you add something Cinder needs to know about.

## Good to know
Ctrl+, opens Settings from anywhere. On Windows, API keys saved in Settings are
stored encrypted for your user account.
""";
}
