# Roadmap

See [docs/plan.md §9](docs/plan.md#9-phased-roadmap) for the canonical phased
roadmap with full deliverables and acceptance criteria. This file is the
shorter "what's shipped vs. shipped-as-stub vs. coming next" tracker.

Status legend:

- ✅ **shipped** — feature works end-to-end against real evidence.
- 🟡 **stub** — the UI / view-model is wired, the loader runs, but the heavy
  lifting (sidecar, parser, or service) is a placeholder.
- ⬜ **planned** — not started.

---

## Phase 0 — Foundation ✅

Project skeleton, design system, custody log, hash service, plumbing.

- ✅ `Cinder.sln` with 24+ project skeletons across UI, core, native, sidecar,
  search, reports, cases, imaging, carving, workflow, plugins, AI.
- ✅ Avalonia 11 shell with Mica title bar, left activity rail, properties
  pane, multi-case tabs, status bar.
- ✅ FluentAvalonia + the Cinder palette (5-step Surface elevation, tracked
  small-caps section labels, Inter + Cascadia Mono).
- ✅ Command palette (Ctrl+K) with built-in actions: open file, open case,
  create case, find, goto, settings, theme toggle, help.
- ✅ SQLite case schema + embedded migration runner inside `CaseStore`.
- ✅ Hash-chained chain-of-custody log with US-separator entries and
  tamper detection.
- ✅ Sidecar JSON-RPC protocol + Python echo worker.
- ✅ Streaming hash service: MD5 / SHA-1 / SHA-256 / BLAKE3, with progress.
- ✅ Serilog structured logging.
- ✅ Local crash bundle handler (`CrashHandler`, `CrashRecovery`).
- ✅ Branding: ember-glyph wordmark, dark + light theme.
- ✅ CI green on Windows + Linux; CodeQL workflow; release workflow ready
  for SignPath signing on Windows.
- ✅ README, CONTRIBUTING, SECURITY, CHANGELOG, LIMITATIONS.

## Phase 1 — Hex viewer & hashing ✅

Shipped as **v0.1.0** (May 2026).

- ✅ Memory-mapped `IHexBuffer`; opens 100 GB images instantly.
- ✅ `HexViewer` Avalonia control implementing `ILogicalScrollable`, cached
  brushes, ArrayPool row buffers, pixel-precise scrolling, UTF-16 column
  filtered to printable glyphs.
- ✅ Inspector decoding int8/uint8/int16/int32/int64 (LE+BE), float32/64,
  GUID, Unix epoch, FILETIME at the caret.
- ✅ Selection, multi-byte copy as hex, bookmarks (Ctrl+D), nav history
  (Alt+Left/Right), find (Ctrl+F), goto (Ctrl+G), multi-file tabs.
- ✅ Signature scanner (60+ formats), extension-mismatch detection.
- ✅ Auto-route detected formats to their dedicated tool with one click.

## Phase 1.5 — Shell & UX ✅

Not in the original plan; landed alongside Phase 1.

- ✅ **Home dashboard** — first screen on launch. Recent cases, recent
  evidence, "First time?" 5-step guide, prominent CTAs.
- ✅ **Per-tool help system** — `?` button on every tool header + F1 shortcut
  opens a written explanation (what it is, when to use, how, plus a
  tip) for every one of the 36 tools.
- ✅ **Multi-case tabs** — open multiple cases at once, switch with one click.
- ✅ **Persistent recents** — recent cases / evidence survive app restart,
  stored at `%LOCALAPPDATA%\Cinder\recents.json`.
- ✅ **Friendly empty states** — Hex / Gallery / Strings / Documents all
  show a centered "what this is + pick file" widget when nothing's loaded.
- ✅ **PythonBootstrap** — auto-creates a per-user venv at
  `%LOCALAPPDATA%\Cinder\venv` and pip-installs forensic dependencies
  on first run.
- ✅ **Strings tool live-filter + container-format detection** — type to
  filter, "Hide gibberish" toggle suppresses compressed-byte coincidence,
  HEADS-UP banner explains when the file is a ZIP/gzip/PDF/etc.
  Double-click a row → jump to the byte in the Hex viewer. Filter
  modes: substring, regex, or a feature preset (email, URL, IP, Luhn
  card numbers, wallets, hashes, paths, JWT/AWS/private-key tokens,
  Base64). CSV / JSON export.
- ✅ **Documents tool real extraction** — DOCX/DOCM, XLSX/XLSM, PPTX,
  ODT/ODS/ODP, EPUB, RTF, PDF (via PdfPig), HTML/XML, and 20+
  plain-text/code formats. ZIP-based formats parse the inner XML
  in-process; PDF uses PdfPig; RTF strips control words; HTML drops
  tags. Hard caps at 50 MB input / 2 MB output to keep the UI fast.

## Phase 2 — Imaging & verification 🟡

UI surfaces exist for every tool. Image acquisition / mounting / shadow
copies are placeholders until the platform-specific drivers ship.

- ✅ Disk imager (raw, E01) — `InProcessImager`: file / block device / E01
  chain → `.dd` or an EnCase 6 `.E01` chain (`EwfWriter`, libewf-verified)
  with hash-on-read, retry + sector-level fallback with bad-sector offsets
  logged (and in the E01's error section), `.log.json` companion, custody
  entry. Needs Administrator / root for devices. 🟡 AFF4 still via sidecar.
- ✅ Image verify — in-process: E01 against recorded digests, raw against a
  companion digest or SHA256SUMS; verified / failed / unverifiable.
- 🟡 Mount image — VHD/VHDX/ISO via PowerShell `Mount-DiskImage` on Windows
  works; E01 mount requires Arsenal Image Mounter (free, external).
- ✅ Convert format — E01 ↔ raw in-process; E01 → raw compared with the
  container's recorded hash, raw → E01 re-read and checked after writing.
- 🟡 Write-blocker (Windows) — placeholder, real version blocked on a
  signed kernel driver in `drivers/cinder-wb-windows`.
- 🟡 Write-blocker (Linux) — `blockdev --setro` wrapper, works.

## Phase 3 — Filesystem & carving ✅

- ✅ Filesystem browser — NTFS / FAT / ext2/3/4 / ISO9660 / VHD / VHDX
  in-process via DiscUtils. Whole-disk images route through VolumeManager
  to enumerate per-partition filesystems. APFS / HFS+ / Btrfs / XFS still
  need pytsk3 sidecar (tracked).
- ✅ File carver — header+footer scan via `Cinder.Carving.FileCarver`
  with 30+ default signatures.
- ✅ Deleted-file recovery (NTFS) — $MFT walk of not-in-use records
  surfaces name, size, MAC/creation times, MFT index and sequence as
  `IsDeleted` rows. Names and timestamps only; contents via the carver.
- ✅ $UsnJrnl:$J — USN_RECORD_V2/V3 parser + USN journal tool (image or
  extracted `$J`); timeline ingest of `$J` from triage folders. $LogFile
  still tracked.

## Phase 4 — Windows artifacts ✅ (most) / 🟡 (some)

Migrated from Python sidecars to in-process C# parsers — the Phase 4 surface
now works on a fresh install without the Python venv bootstrap for the common
case. Eric Zimmerman's `Registry`, `evtx`, `Lnk`, `Prefetch`, and `JumpList`
libraries do the heavy lifting in-process.

- ✅ Registry — `Registry` (Eric Zimmerman) lib in-process. Walks every key /
  value of an NTUSER / SYSTEM / SOFTWARE / SAM / Amcache hive, including
  transaction-log replay.
- ✅ Event Log (.evtx) — `evtx` lib in-process. Streams every record with
  EventId, TimeCreated, Provider, Channel, Level, User, Computer, and
  MapDescription where available.
- ✅ Prefetch — `Prefetch` lib in-process. Handles every known Windows version
  (XP–11), surfaces all 8 LastRunTimes plus run count and loaded-files count.
- ✅ LNK shortcuts — `Lnk` lib in-process. Decodes target path, working dir,
  arguments, MAC times, machine volume serial.
- ✅ Jumplists — `JumpList` lib in-process. Both automatic and custom
  destinations, with per-entry timestamps and AppId resolution.
- ✅ Browser history (Chromium / Edge / Brave / Opera / Vivaldi / Firefox) —
  direct read of the SQLite History / places.sqlite databases. Auto-stages
  to a temp file so locked-by-browser DBs still parse.
- ✅ USB history — Registry-driven; walks `Enum\USBSTOR` across every
  ControlSet for vendor / product / serial / first-install timestamps.
- ✅ Wi-Fi history — Registry-driven; walks `NetworkList\Profiles` for
  every saved SSID + last-seen.
- ✅ Amcache — Registry-driven; reads `InventoryApplicationFile` (or `File`
  on pre-Win10) for path / hash / publisher / version / last-seen.
- ✅ ShimCache — Win8 / Win10 / Win11 AppCompatCache blob decoder.
  Streams every "10ts"/"00ts" entry: full path + last-modified FILETIME.
- ✅ Shellbags — `Registry` lib walks BagMRU; each numbered value is
  decoded via the `Lnk.ShellItems` shell-item parsers (drives, folders,
  files, network shares, control panel, delegate items) into
  human-readable path components. Reconstructs the full traversal path
  per row.
- ✅ SRUM — Microsoft.Database.Isam opens SRUDB.dat read-only with
  staged-file copy and log-replay. Per-row decoders for the three main
  extension tables: application resource usage (per-app CPU foreground
  + background + face time), network data usage (bytes sent / received),
  energy estimation. Resolves AppId / UserId via SruDbIdMapTable;
  binary SIDs render as canonical S-1-… strings.
- ✅ Email — `.msg` (Outlook) + `.eml` + `.mbox` via `MsgReader` and an
  in-house MBOX scanner.
- ✅ Email — `.pst` / `.ost` via `libpff-python` (pypff) invoked as an
  inline Python script. Enumerates every folder + message with sender,
  subject, delivery time, attachment count. Falls back to a clear
  `pip install libpff-python` hint when pypff isn't installed.

## Phase 5 — Linux artifacts ✅

- ✅ shell history · auth.log · syslog · cron · passwd · shadow · SSH
  known_hosts — in-process plain-text parsers walk a mounted Linux root
  (or triage folder), tag entries by category, normalise classic
  syslog timestamps + ISO-8601 journal timestamps to UTC.
- 🟡 journalctl binary journal files, systemd unit metadata, package
  manager logs — tracked.

## Phase 6 — Search, timeline, hash sets, YARA, VirusTotal ✅ (most)

- ✅ Lucene.NET case-wide index — full-text index with "Build index from
  folder…" ingestion. Recursively walks an evidence folder, routes each
  file through DocumentReader for structured formats and falls back to
  printable-strings extraction for binaries. Standard Lucene query
  syntax (`source:evtx`, `user:alice`, `text:"exact phrase"`, etc.).
- ✅ Super-timeline — `TimelineIngester` walks a triage folder and
  merges every parseable artifact into the SuperTimeline. Sources:
  EVTX records, Prefetch LastRunTimes, LNK Created/Modified/Accessed,
  NTUSER UserAssist last-execution, Chromium + Firefox browser history,
  .eml/.msg Date headers, $I recycle-bin deletions with owning SID.
  Sort + histogram + range filter + user filter + text-contains filter
  all work on the merged view. Events are ATT&CK-tagged on ingest
  (`MitreTagger`) and filterable by technique/tactic id. Export to
  Timesketch JSONL / CSV and Sleuth Kit bodyfile.
- ✅ Map — auto-ingest from a folder of images. MetadataExtractor pulls
  every photo's EXIF GPS, plots one point per geo-tagged image with
  filename + mtime. Manual add still available for non-image evidence.
- ✅ Communication graph — auto-ingest from a folder of `.eml` / `.msg`
  / `.mbox` files. Parses From / To headers, deduplicates identities,
  builds a directed who-talked-to-whom graph with in/out degree counts.
  Manual add still available.
- ✅ Hash sets (NSRL bulk import) — `HashSetService` in
  `src/Cinder.Search/` with `ImportNsrlMinimalCsv` (~200k rows/sec on
  a typical SSD) + `Lookup(algorithm, digest)`. HashSetsTool exposes
  PickDatabase / ImportNsrl / Lookup commands. SQLite-backed so
  100M-row NSRL RDS imports stream in without RAM pressure. Wired into
  the Filesystem walk (Settings ▸ Hash sets): per-file SHA-1 + Known /
  Notable / Unknown verdict, so the row filter is the known-good filter.
- ✅ IOC match — indicator list × folder: file hashes, paths, contents
  (ASCII + UTF-16LE via YARA-lite), timeline events. Bounded, exportable.
- ✅ Bookmarks — flag any grid row or timeline event with a note; stored
  in the case file (schema v2) and echoed to custody; Reports turns them
  into a numbered Exhibits section; Bookmarks tool to review / delete /
  export.
- ✅ Custody attestations — sign the chain tip with an examiner key
  (ECDSA P-256), verify from the file alone, export for out-of-band
  publication. A consistent rewrite now fails attestation.
- ✅ RFC 3161 timestamps — with a TSA configured, attestations are
  countersigned by a third-party clock; verification checks the token
  against the signature and reports chain trust separately.
- ✅ YARA-lite — in-house parser + Aho-Corasick matcher. Loads `.yar`
  files, parses the common `rule { meta: strings: condition: }` grammar
  (literal `"strings"`, `nocase`, hex `{ 4D 5A }` patterns; condition
  variants `any of them` / `all of them` / boolean of string IDs), and
  scans target files via a single Aho-Corasick automaton built across
  every rule. Per-rule hit summary + per-pattern offset list. Regex
  patterns and full libyara feature parity remain 🟡.
- 🟡 VirusTotal hash-only lookup — UI shell.

## Phase 7 — Memory forensics 🟡

- ✅ Volatility 3 wrapper — `Cinder.App.Services.Vol3Runner` shells to
  `python -m volatility3 -f <image> -r json <plugin>` and parses the
  JSON renderer output. Ships pstree, psscan, netscan, dlllist,
  malfind, hashdump, lsadump. MemoryTool routes `.dmp` / `.raw` /
  `.lime` through it; missing vol3 surfaces a clear install hint.
- ⬜ RAM capture (signed driver Windows / LiME Linux) — needs signed kernel
  driver.

## Phase 8 — Reporting & case management ✅ (most)

- ✅ Reports — Markdown / HTML / PDF (QuestPDF, in-process) / DOCX
  (DocumentFormat.OpenXml, in-process) / JSON playbook, all without
  external converters. PDF: cover metadata, per-section bodies with
  embedded exhibit cards, full exhibit index, header + footer on every
  page. DOCX: structurally valid Word document with title page,
  per-section bodies (paragraphs + bullets), exhibit cards, exhibit
  index table, and proper Office core properties.
- ✅ Custody chain view — fully working. The log records case open,
  every parser run (tool / evidence / rows / truncated), verifications
  with digests and verdict, mounts, data and report exports, manual
  hashes. Tamper-evident, not tamper-proof — see SECURITY.md.
- ✅ Export — CSV / JSON from every artifact grid and from Strings.
- ✅ Case create / open / branch — working.
- ✅ Workflows — JSON DAG loader + topological executor. Built-in
  handlers: `open-image`, `hash`, `registry`, `fs-enumerate`, `carve`,
  `report`, `index`. Outputs land in a results pane row-by-row;
  `ai-summary` step degrades gracefully when no AI provider is set.
- 🟡 Plugins (C# SDK + Python scripting host) — UI shell with trust gate.

## Phase 9 — AI copilot ⬜

- 🟡 BYOM provider selection (Ollama / LM Studio / OpenAI-compatible) —
  UI shell.
- ⬜ Structured artifact prompts.

## Phase 10 — Network, mobile, cloud 🟡 (most)

- ✅ Network (PCAP / PCAPNG) — SharpPcap + PacketDotNet in-process.
  Per-packet: timestamp, protocol, source/dest IP + port, bytes,
  TCP flags. Cap 50k packets per load.
- ✅ Mobile backup (iOS) — reads `Manifest.db` directly via
  Microsoft.Data.Sqlite. Enumerates every backed-up file with its
  domain + relativePath + fileID. Encrypted backups still need the
  user's iTunes backup password.
- ✅ Android adb backup (`.ab`) — header parse + deflate-stripped TAR
  walk via SharpCompress. Surfaces per-package entries (app id, file
  path, size, mtime). Encrypted backups surface a clear "need adb
  password to decrypt" row.
- 🟡 Cloud pull (Google Drive / OneDrive / Dropbox via OAuth/PKCE) — UI
  shell.

---

## Cross-cutting

- 🟡 Win/Mac/Linux installers — release workflow ships self-contained
  Windows `Cinder.exe` + Linux `.tar.gz`; `packaging/winget/`,
  `packaging/linux/{debian,rpm,appimage}/` manifests are checked in
  and go live once SignPath approves so signed artifacts flow through
  each channel.
- 🟡 **Code signing** — Windows SignPath Foundation re-application in
  flight; `.signpath/` config + release-workflow submit-action already
  wired for when approval lands.
- ✅ Self-update channel — `UpdateChecker` hits the public
  `/releases/latest` GitHub endpoint, semver-compares against the
  running assembly version, and surfaces `UpdateInfo` for a
  non-blocking dashboard banner with a click-through to the release
  page. No auto-download; documented as the project's one phone-home
  in the README Telemetry section.

## How to influence the roadmap

- Open an issue with the `roadmap` label.
- Vote with reactions on existing roadmap issues.
- Submit a PR — the fastest way to ship something.
