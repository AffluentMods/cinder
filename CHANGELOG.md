# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.2] — 2026-09-09

The "cross-artifact + memory + PST + updates" release. v0.2.1 shipped
the evidence pipeline (E01 chains, EvidenceOpener, entropy heuristic);
v0.2.2 uses that pipeline to close the last shipping gaps in the
super-timeline, memory forensics, PST/OST corporate email, self-update,
and installer packaging.

### Added — analysis pipeline

- **Super-timeline merge** — `Cinder.App.Services.TimelineIngester`
  walks a triage folder and feeds every parseable artifact into the
  existing SuperTimeline backend. Sources: `.evtx` (per record),
  `.pf` prefetch (8 LastRunTimes per program), `.lnk` (Created /
  Modified / Accessed of the target), `NTUSER.DAT` UserAssist
  last-execution (ROT13-decoded), Chromium `History` +
  Firefox `places.sqlite` per-URL last-visit, `.eml` / `.msg` Date
  headers, and `$I` recycle-bin deletions with owning SID. Per-source
  row counts + sort + UI refresh. Timeline tool gains an
  **Ingest folder…** command next to the existing demo-seed / refresh
  controls.
- **Memory forensics via Volatility 3** — `Vol3Runner` shells out to
  `python -m volatility3 -f <image> -r json <plugin>` and parses the
  JSON renderer output. Ships the standard plugin catalogue: pstree,
  psscan, netscan, dlllist, malfind, hashdump, lsadump. Availability
  is probed once and cached; missing python or missing module surfaces
  a clear install-hint row instead of silent failure. `MemoryTool`
  now runs `windows.pstree.PsTree` by default and returns
  PID / PPID / ImageFileName / Threads / Handles / CreateTime /
  ExitTime for every process.
- **PST / OST email** — `WindowsParserTools.ParsePstOst` inlines a
  short Python script via `python -c` that imports `pypff`
  (libpff-python), walks every folder, and emits one JSON-line per
  message. C# reads the stream and rows up folder / from / subject /
  delivery-time / attachment-count. Missing `pypff` returns a clear
  `pip install libpff-python` hint. Replaces the v0.1 "PST/OST not
  supported" stub.

### Added — governance & signing

- **`.signpath/` configuration** — `artifact-configuration.xml` +
  `signpath-manifest.yml` + `README.md`. The GitHub Actions release
  workflow already engages the SignPath submit-action when
  `vars.SIGNPATH_ENABLED == 'true'`; approval is pending with the
  SignPath Foundation OSS program.
- **README signature-status section** — explains the expected Windows
  SmartScreen warning until SignPath approval, and documents the
  PowerShell / `sha256sum` recipes for verifying `Cinder.exe` against
  `SHA256SUMS.txt` in the release page.
- **CODEOWNERS** — every path routes to the primary maintainer for
  auto-review-request; security-sensitive dirs (`SECURITY.md`,
  `.github/workflows/`, `.signpath/`, `Signatures/`, `Ewf/`, `Plugins/`)
  get an explicit callout.

### Added — self-update + installer packaging

- **`UpdateChecker`** — non-intrusive check against the public
  `/releases/latest` GitHub endpoint, semver compare vs the running
  assembly. Returns `UpdateInfo` for a dashboard banner with a link to
  the release page — no auto-download. Documented as the project's
  one phone-home; opt-out toggle lives in Settings.
- **Installer manifests** — `packaging/winget/` (three yaml files for
  `winget install AffluentLabs.Cinder`, portable installer kind),
  `packaging/linux/debian/control` + `postinst` (icon-cache refresh +
  `setcap cap_sys_rawio,cap_sys_admin+ep` so users don't need sudo
  per launch), `packaging/linux/rpm/cinder.spec` (Fedora / RHEL) and
  `packaging/linux/appimage/build.sh` (AppDir assembly from a published
  linux-x64 tarball).

### Fixed — dependencies

- **NU1903 SQLitePCLRaw.lib.e_sqlite3** — the 2.1.11 bundled libsqlite
  had GHSA-2m69-gcr7-jv3q. Pinned the whole `SQLitePCLRaw.*` suite to
  2.1.12 in `Directory.Packages.props`, matching the `Tmds.DBus.Protocol`
  fix pattern. Release builds pass the vuln audit clean.
- **NU1902 Microsoft.Build.Tasks.Git** — GHSA-23fw-v26w-5fgq applied
  through Microsoft.SourceLink.GitHub. Pinned to 10.0.401 and bumped
  `System.IO.Hashing` to 10.0.12 to match its transitive requirement.

### Changed

- README rewrites: signature-status section replaces the earlier
  code-signing paragraph; the coming-package-managers list moved into
  it so users see verification + install stories in one place.
- ROADMAP flips Phase 6 super-timeline, Phase 7 memory (via vol3),
  Phase 8 self-update, and Phase 10 PST/OST from 🟡 to ✅. Remaining
  🟡s honestly annotate the "needs SignPath approval" gate.

[0.2.2]: https://github.com/AffluentMods/cinder/releases/tag/v0.2.2

## [0.2.1] — 2026-06-25

The "real evidence end-to-end" release. v0.2.0 worked against every common
artifact format individually; v0.2.1 closes the loop so dropping a real
EnCase .E01 onto Cinder yields a browsable filesystem + parseable
artifacts + verifiable hashes — without leaving the app and without a
Python sidecar.

### Added — evidence pipeline

- **In-process EWF (.E01) reader** — `Cinder.Imaging.Ewf.EwfReader` parses
  the EVF magic + section chain (header2 / volume / table / sectors /
  done) and exposes the underlying raw disk through a seekable
  `EwfStream` with on-demand ZLib chunk decompression. Verified end-to-end
  against a 295 MB EnCase image (Jimmy Wilson case study): 891 MB raw disk
  read through 27,200 compressed chunks, computed SHA1 **byte-for-byte**
  matches the recorded acquisition hash.
- **Multi-segment .E02 / .E03 chain support** — sibling segments are
  auto-discovered in EnCase naming order (.E01 → .E99 → .EAA → .EZZ),
  parsed together, and stitched into one continuous virtual disk.
- **`Cinder.Imaging.EvidenceOpener.Open(path)`** — single entry point
  that auto-sniffs the EVF magic and returns either an EWF-backed Stream
  or a plain FileStream. Wired into:
  - **Filesystem tool** — drop an .E01, get the same NTFS / FAT / exFAT /
    VHD partition browser as a raw .dd.
  - **Carver tool** — header+footer signature carve runs straight off an
    .E01 chain.
  - **YARA tool** — Aho-Corasick scan walks the EWF-backed stream a
    chunk at a time; 100 GB images stay within bounded memory.
  - **Hash dialog** — drag an .E01 in and get MD5 / SHA-1 / SHA-256 /
    BLAKE3 of the **raw disk**, not the EWF container bytes — the values
    line up with the acquisition-recorded hash so chain of custody is
    preserved.

### Added — tools & viewers

- **Image viewer (Gallery)** — click any thumbnail to open a full-size
  preview alongside the grid. Side panel shows dimensions, file size,
  modified-UTC, GPS lat/lon (when EXIF GPS present), and a scrollable
  EXIF row list (Make / Model / Software / DateTimeOriginal /
  ExposureTime / FNumber / ISO / FocalLength / LensModel) via
  MetadataExtractor.
- **Recycle Bin tool** (new, Phase 4) — decodes Windows `$I` metadata
  files (Vista/7 v1 fixed 520-byte path; Win10+ v2 variable name_len).
  Output grid: owning SID, original full path, original size, deletion
  timestamp, whether the companion `$R` file is still on disk
  (recoverable). Accepts a `$Recycle.Bin` root (walks every `S-1-5-…`
  subdirectory) or a flat folder of `$I` files. Per-tool F1 help
  explains the format and how to map SID → username via SAM\\Users\\Names.

### Added — signature & analysis

- **TrueCrypt / VeraCrypt detection heuristic**
  (`Cinder.Core.Signatures.EncryptedContainerHeuristic`). These formats
  ship no magic header (by design — plausible deniability). The heuristic
  combines four shape conditions: no known signature match + sector-aligned
  size + above 19 KB minimum + Shannon entropy ≥ 7.95 on the header sample.
  None alone is diagnostic; the combination is. Wired into the Hex
  viewer's "Detected format" badge so an unrecognised high-entropy
  sector-aligned blob now surfaces as
  `Probable encrypted container (entropy 7.99)` with an actionable hint.
- **BCTextEncoder armored output** added as a normal `MagicSignature`
  (`-----BEGIN ENCODED MESSAGE-----` marker).

### Fixed

- C# `\x` escape greediness — the EWF magic constant was originally
  `"EVF\x09\x0d\x0a\xFF\x00"u8`; the compiler reads up to 4 hex digits per
  escape so `\x09\x0d` parsed as `\x090d` (Devanagari U+090D). Replaced
  with an explicit `byte[]` literal. Same trap applies to any other
  string-literal magic constants in the codebase.
- MetadataExtractor's `GeoLocation` is a struct returned as
  `GeoLocation?`; the GPS readout in the Gallery EXIF panel was
  dereferencing it as a class. Routed through `.Value.Latitude` /
  `.Value.Longitude`.

### Changed

- README rewrite to professional standard — phase-by-phase status matrix
  matching ROADMAP, Cinder-vs-Autopsy-vs-FTK-vs-EZ-Tools-vs-Volatility
  comparison table, honest install section, "your first case in 5
  minutes" quickstart, updated architecture diagram.
- TESTING.md (new) — end-to-end pre-release runbook covering build/test
  gates, smoke launch, per-tool functional walkthrough with pass criteria
  for all 36 tools, public license-clean test-data sources, security +
  supply-chain checks, release-pipeline dry run, screenshot capture
  protocol.
- Release workflow allow-list — `.github/workflows/release.yml` now ships
  exactly `Cinder.exe`, `cinder-linux-x64.tar.gz`, `SHA256SUMS.txt`
  instead of flattening the entire publish directory (v0.2.0 leaked 20
  stray Lato font files; `Directory.Build.targets` strips them at the
  source too).

### Acknowledgments

End-to-end verification of the EWF reader was performed against a real
forensic image distributed for educational case-study use. The image
itself is not redistributed.

[0.2.1]: https://github.com/AffluentMods/cinder/releases/tag/v0.2.1

## [0.2.0] — 2026-05-14

The "every tool actually works" release. v0.1.0 shipped with real Hex/Strings/
custody/case/hash plumbing but most parser surfaces (Registry, EVTX, Prefetch,
LNK, browser history, Filesystem, Carver, Network, Mobile, SRUM, Shellbags,
ShimCache, Email, Linux artifacts, YARA, Lucene, Map, Graph, Reports PDF/DOCX,
Workflows) were UI shells over Python sidecar stubs. This release replaces
every one of those stubs with an in-process C# implementation.

### Added — parsers (all in-process, no Python sidecar required for these)

- **Filesystem** — DiscUtils-backed browser for NTFS / FAT / ext2/3/4 /
  ISO9660 / VHD / VHDX. Whole-disk images route through VolumeManager to
  enumerate per-partition filesystems.
- **Registry** — Eric Zimmerman's `Registry` lib. Walks every key + value
  of an NTUSER / SYSTEM / SOFTWARE / SAM / Amcache hive.
- **Event Log (.evtx)** — `evtx` lib. Streams every record with TimeCreated,
  Channel, Provider, EventId, Level, Computer, User, MapDescription.
- **Prefetch** — `Prefetch` lib for every Windows version XP→11.
- **LNK shortcuts** — `Lnk` lib. Target, args, MAC times, volume serial.
- **Jumplists** — `JumpList` lib. Both automatic and custom destinations.
- **Shellbags** — Registry walk + shell-item decode via `Lnk.ShellItems`.
  Reconstructs full traversal paths like `My Computer\C:\Users\…`.
- **USB / Wi-Fi / Amcache / ShimCache history** — Registry-driven across
  every ControlSet, with version-aware decoders.
- **SRUM** — Microsoft.Database.Isam opens SRUDB.dat read-only with
  staged-file copy and log-replay. Per-row decoders for application
  resource usage, network data usage, and energy estimation.
- **Email** — `.msg` / `.eml` / `.mbox` via MsgReader + in-house MBOX
  scanner. PST/OST still needs libpff sidecar (tracked).
- **Linux artifacts** — auth.log, syslog, crontab, passwd, shadow,
  ssh_known_hosts, plus per-user shell histories.
- **Browser history** — direct SQLite reads of Chromium / Edge / Brave /
  Opera / Vivaldi / Firefox History databases, with file staging so a
  running browser doesn't block.
- **Network (PCAP / PCAPNG)** — SharpPcap + PacketDotNet for per-packet
  timestamp, protocol, IPs/ports, byte count, TCP flags.
- **Mobile** — iOS backup (Manifest.db) + Android adb backup (.ab via
  SharpCompress).
- **Carver** — header+footer scan via Cinder.Carving.FileCarver.
- **YARA-lite** — pure-managed YARA subset built on AhoCorasick. Parses
  `.yar` files, handles literal strings + `nocase` + hex patterns + the
  common condition expressions, scans large files in a single linear
  pass.
- **Lucene case-wide search** — "Build index from folder…" walks evidence,
  routes through DocumentReader for structured formats, falls back to
  printable-strings for binaries.
- **Documents** — DOCX / DOCM / XLSX / PPTX / ODT / EPUB / PDF (PdfPig) /
  RTF / HTML / 25+ plain-text-and-code formats.

### Added — analysis & reporting

- **Map auto-ingest** — pick a folder of images, MetadataExtractor pulls
  EXIF GPS, one point per geo-tagged photo.
- **Graph auto-ingest** — pick a folder of `.eml` / `.msg` / `.mbox`,
  builds the who-talked-to-whom directed graph from email headers.
- **Reports PDF** — QuestPDF in-process. Cover metadata, per-section
  bodies with embedded exhibit cards, full exhibit index, page numbers
  on every page. No external converter required.
- **Reports DOCX** — DocumentFormat.OpenXml. Structurally valid Word
  document with title page, section bodies (paragraphs + bullets),
  exhibit cards, exhibit index table, Office core properties.
- **Workflows runtime** — topological executor + handlers for
  `open-image`, `hash`, `registry`, `fs-enumerate`, `carve`, `report`,
  `index`. Steps chain by file-path output.

### Added — shell & UX (Phase 1.5)

- Home dashboard as the first screen; recent cases + recent evidence
  persist across restarts at `%LOCALAPPDATA%\Cinder\recents.json`.
- Per-tool `?` help (F1) with written explanations for every one of the
  36 tools — what it is, when to use, how, plus a tip.
- Multi-case tabs.
- Friendly empty states across Hex / Gallery / Strings / Documents.
- AI Copilot — Test-connection button + auto-load API key from settings.
- Cloud OAuth scaffolds — Google Drive / OneDrive / Dropbox. PKCE-based
  authorize URL surfaced to the user; token exchange + file pull pending.

### Added — security

- **DPAPI for secrets at rest** — `apiKey` / `ApiKey` / `api_key` values
  in settings.json are encrypted via Windows DPAPI (CurrentUser scope)
  before serialisation; AES-GCM fallback on Linux/macOS documented as
  obfuscation rather than real protection.
- **Plugin Authenticode verification on Windows** — signed plugins display
  the subject CN in the Plugins UI; chain validation surfaces "untrusted
  chain" warnings. SHA-256 manifest remains the primary trust gate.
- **Per-tool sandboxing groundwork** — `[LoadIsolated]` attribute declared
  for future AssemblyLoadContext / sidecar isolation.

### Changed

- ROADMAP completely refreshed. Phase 3 / 4 / 5 / 6 / 8 / 10 all flipped
  from 🟡 to ✅ for the items C# can do in-process. Remaining 🟡s are
  honestly tracked with reasons (libyara, libpff, Volatility, pytsk3 for
  APFS/HFS+, signed kernel drivers).
- ReportExporter PDF path: QuestPDF in-process by default; wkhtmltopdf /
  headless Chromium remain as fallbacks but are no longer required.

### Fixed

- Strings tool crash when picking a file (`Call from invalid thread` —
  removed `.ConfigureAwait(false)` across every MVVM command path).
- Help flyout body was empty (resource lookup via
  `Application.Current.Resources[…]` failed silently for theme-dictionary
  brushes; rewrote as XAML data-binding against `HelpBlocks`).
- Inspector contrast and rail Phase grouping polish.
- HashServiceTests flake (replaced `Progress<T>` with a synchronous
  IProgress implementation in the test).

### Security

- Security audit complete; eight findings closed in code:
  command injection across every Process.Start (mounters, shadow copies,
  write blocker, ReportExporter), PowerShell `'` injection in the
  WindowsImageMounter, wkhtmltopdf `--enable-local-file-access` removed,
  EncryptedBundle hardened with zip-slip + zip-bomb guards + key
  zeroization, CustodyLog rejects U+001F in input fields, plugin trust
  gate with SHA-256 manifest, NU1903 fixed (`Tmds.DBus.Protocol 0.21.3`
  pinned).
- One previously-open finding closed: API keys at rest are now DPAPI-
  encrypted on Windows.

[0.2.0]: https://github.com/AffluentMods/cinder/releases/tag/v0.2.0

## [0.1.0] — 2026-05-11

First public pre-alpha. The application launches, every tool surface is wired
up, the hex viewer ships with production-grade ergonomics, and the case
infrastructure (custody log, hash service, signature scanner) is implemented
end-to-end. Most parser sidecars are stubs awaiting the Python venv bootstrap
on first run.

### Added

#### Phase 0 — Foundation
- Cross-platform Avalonia 11.2 shell on .NET 10 with FluentAvalonia, MVVM, and
  community toolkit source generators.
- Five-step Surface elevation token system with theme-aware brushes (dark and
  light variants) and tracked small-caps typography.
- Embedded SQL migration runner backing the SQLite case store; positional
  records replaced with settable-property classes for Dapper compatibility.
- Blake3 + SHA-256 + SHA-1 + MD5 hash service with progress reporting.
- Chain-hashed custody log with US-separator-delimited entries, payload
  tampering detection, and genesis-from-zero anchoring.
- Signature scanner with 60+ magic-number signatures and extension-mismatch
  detection.

#### Phase 1 — Hex viewer
- Memory-mapped `IHexBuffer` for evidence-scale random-access reads.
- `HexViewer` Avalonia control implementing `ILogicalScrollable`, with cached
  brushes, `ArrayPool` row buffers, and pixel-precise thumb-drag scrolling.
- Inspector pane decoding int8/uint8/int16/int32/int64 (LE+BE), float32/64,
  GUID, Unix epoch, and FILETIME at the caret.
- Selection, multi-byte copy as hex, bookmarks (Ctrl+D), navigation history
  (Alt+Left/Right), goto (Ctrl+G), find (Ctrl+F), multi-file tabs, and
  per-case auto-routing.
- UTF-16 column filtered to genuinely useful glyphs (basic Latin, Latin-1,
  Latin-Extended-A/B); CJK and control characters collapse to a middle dot.

#### Shell
- Left activity rail with four sections (Examine / Analyze / Acquire / Case)
  ordered by Phase ascending; selection highlighted with accent stripe.
- Mica title bar with ember-glyph wordmark, CASE / EVIDENCE chips, WriteBlock
  status dot, PRE-ALPHA tag, and Ctrl+K command palette trigger.
- Status bar with selection summary chip, mono caret, and keyboard hints.
- 36 tool view-models covering hex, filesystems, registry, EVTX, prefetch,
  shellbags, jumplists, LNK, browser/USB/Wi-Fi history, SRUM, Amcache,
  Shimcache, email, Linux artifacts, memory, network, mobile, timeline, map,
  graph, search, hash sets, YARA, VirusTotal, AI copilot, imager, verify,
  mount, convert, shadow copy, RAM capture, carver, cloud pull, cases,
  reports, custody, workflows, plugins, and settings.

#### Python sidecar plumbing
- JSON-RPC over NDJSON stdio protocol with pydantic v2 message schemas.
- `PythonBootstrap` service auto-creates a per-user venv at
  `%LOCALAPPDATA%\Cinder\venv` and pip-installs pinned forensic dependencies
  on first run.
- Seven sidecar shells: registry, EVTX, prefetch, shellbags, LNK, browser
  history, and email.

#### Tests
- 18 passing tests across `Cinder.Core.Tests` and `Cinder.Native.Tests`
  covering hash service, custody log, signature scanner, case service, and
  platform contract round-trip.

### Code signing

This release is **unsigned**. Cinder's application to the SignPath Foundation
open-source signing program is in flight; future releases will be signed under
that program. Verify SHA-256 hashes against `SHA256SUMS.txt` in the release
assets.

[Unreleased]: https://github.com/AffluentMods/cinder/compare/v0.2.2...HEAD
[0.1.0]: https://github.com/AffluentMods/cinder/releases/tag/v0.1.0
