# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/AffluentMods/cinder/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/AffluentMods/cinder/releases/tag/v0.1.0
