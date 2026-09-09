# Cinder — feature reference

A single-document map of what Cinder is, how it is built, and what every tool actually does
today. Written so that someone (or some other AI session) picking up the codebase can
orient without reading the source first. Status words are used precisely: **works** means
it runs end-to-end against real input in-process; **shell** means the UI exists and the
implementation is a placeholder or depends on an external component that is not shipped.

Companion documents: [README](../README.md) (positioning), [ROADMAP](../ROADMAP.md)
(phase status), [LIMITATIONS](../LIMITATIONS.md) (what is blocked on external work),
[SECURITY](../SECURITY.md) (threat model, audits, what the custody chain proves),
[CHANGELOG](../CHANGELOG.md) (every change by release), [TESTING](../TESTING.md).

---

## 1. What it is

Cinder is a cross-platform (Windows 11 / Linux) desktop digital-forensics workstation:
one application, one case format, one UI over the work that otherwise spans Autopsy,
FTK Imager, the Eric Zimmerman suite, Volatility, Hindsight, ExifTool, Plaso and a hex
editor. C# / .NET 10, Avalonia 11 UI, Apache-2.0, no telemetry.

Honest positioning: the durable value is **one modern shell over the best .NET forensics
libraries** — Eric Zimmerman's parsers, DiscUtils, Lucene.NET, QuestPDF, SharpPcap,
MetadataExtractor, MsgReader — plus the parts Cinder builds itself: the case store with a
hash-chained custody log, the EWF (E01) reader with verification, the hex viewer, the
carver, the super-timeline with ATT&CK tagging and Timesketch export, and a BYOM AI
copilot. Every Windows-artifact parser runs in-process; Python sidecars remain only for the
long tail (PST via pypff, Volatility 3, pytsk for non-DiscUtils filesystems).

## 2. Architecture

### Projects (`src/`)

| Project | Role | Notes |
|---|---|---|
| `Cinder.App` | Avalonia shell, every view-model, every tool implementation, app services | ~55% of all code. **Parsing logic lives here**, in `ViewModels/Tools/*.cs`, not in the library projects — the biggest structural debt (see §9). |
| `Cinder.Core` | Case store (SQLite + Dapper + migrations), custody log, hash service (MD5/SHA-1/SHA-256/BLAKE3 streaming), signature scanner (60+ magics), encrypted-container heuristic, `ExecutableResolver`, `TabularExporter`, `FeatureExtractor` | The genuinely reusable core. |
| `Cinder.Imaging` | `EwfReader`/`EwfStream` (in-process E01, multi-segment, verification), `EvidenceOpener` (E01-or-raw → `Stream`), mounters (VHD/VHDX/ISO via PowerShell; Linux loop), shadow-copy enumeration, write-blocker wrappers, sidecar imager/verifier | EWF reader is hardened against malformed input; damaged chunks are zero-filled and reported, never silently truncated. |
| `Cinder.Carving` | `FileCarver` (header/footer, vectorised, 30+ signatures), `SlackUnallocCarver` | |
| `Cinder.Hex` | `HexViewer` control (virtualised, `ILogicalScrollable`), `MmapHexBuffer`, `HexSearch` (streaming find), bookmarks, overlays | |
| `Cinder.Search` | Lucene.NET `CaseIndex`, `SuperTimeline` + `TimelineExporter` + `MitreTagger`, `HashSetService` (NSRL, SQLite), `CommunicationGraph`, `GeoPoint` index, `VirusTotalClient`, `YaraScanner` (sidecar stub — real scanning is `Cinder.App/Services/YaraLite`) | |
| `Cinder.Reports` | `ReportBuilder` (Markdown model), `ReportExporter` (MD/HTML/PDF/DOCX/JSON), `QuestPdfDocument`, `DocxReportWriter` (OpenXml) | PDF and DOCX are real, in-process. |
| `Cinder.Cases` | `Workspace` (recent cases), `EncryptedBundle` (AES-256-GCM case bundles, PBKDF2 600k), `CaseBranching` (multi-examiner branches/commits) | |
| `Cinder.AI` | `IAiProvider`, Ollama / LM Studio / OpenAI-compatible providers, `PromptBuilder`, `AnomalyDetector` (statistical, no LLM), `NaturalLanguageQuery` (scaffold) | |
| `Cinder.Workflow` | JSON DAG model + topological executor | Handlers live in `Cinder.App/ViewModels/Tools/WorkflowHandlers.cs`. |
| `Cinder.Plugins` | `IPlugin` contract, `PluginLoader` (trust sentinel + SHA-256 manifest gate), `PythonScriptingHost` | Plugins run in-process; isolation is planned. |
| `Cinder.Artifacts`, `.Windows`, `.Linux` | `IArtifact` contract; sidecar clients for the Python workers | The Windows/Linux projects are thin — the real parsers are in `Cinder.App`. |
| `Cinder.Filesystems`, `.Memory`, `.Mobile`, `.Network`, `.Cloud` | Sidecar clients (pytsk, vol3, backup, pcap) and cloud OAuth/PKCE connectors | Cloud token exchange is unfinished. |
| `Cinder.Sidecar` | JSON-RPC 2.0 over NDJSON stdio, `SidecarClient` | |
| `Cinder.Native`, `.Windows`, `.Linux` | `IPlatform` abstraction; raw-device IO is stubbed | |
| `Cinder.Cli` | Every GUI action as a verb (`case`, `custody verify`, `hash`, `sig identify`, `image`, `carve`, `parse fs/registry/evtx/linux`, `index`, `timeline`) | |
| `Cinder.Reader` | Free read-only viewer for shared encrypted bundles; verifies the custody chain on open | |

`parsers/` holds the Python workers (`windows`, `linux`, `filesystem`, `imager`, `memory`,
`scripting`, `echo`) and `PythonBootstrap` creates a per-user venv at
`%LOCALAPPDATA%\Cinder\venv` on demand. `drivers/` holds WDK/LiME sources that do not build in
CI (see LIMITATIONS).

### Shell composition

`MainWindowViewModel` owns the command palette, the hex buffer tabs, the open-case tab strip
(`CaseSession` records) and `WorkspaceViewModel`, which builds the five rail sections and every
tool view-model. `ToolHost.axaml` picks a view per tool `Kind`; most parser tools share
`SidecarRunnerView` (pick evidence → grid → status bar) bound to `SidecarToolViewModel`.

Key services (`Cinder.App/Services`): `CommandRegistry`/`CommandRegistration` (Ctrl+K),
`SettingsStore` (settings.json, API keys DPAPI-encrypted on Windows, 0600 on Unix),
`RecentsStore`, `CrashHandler`/`CrashRecovery`, `UpdateChecker` (GitHub releases, HTTPS,
opt-out, no auto-download), `DocumentReader`, `TimelineIngester`, `YaraLite`, `Vol3Runner`,
`PythonBootstrap`, `ActiveCaseContext` (the active case for custody logging).

### Case format

One SQLite file per case (`CaseStore`, migrations embedded). Tables include the case row
and `custody_entries` (sequence, timestamp, examiner, action, details JSON, prev/entry hash).
Chain: `SHA-256(prev ‖ US ‖ seq ‖ US ‖ ts ‖ US ‖ examiner ‖ US ‖ action ‖ US ‖ details)`
with U+001F rejected from every field. **Tamper-evident, not tamper-proof** — the hash is
unkeyed and stored beside the data; SECURITY.md explains.

## 3. Tools — Examine

| Tool | Status | What it does | Where |
|---|---|---|---|
| **Hex Viewer** | works | Memory-mapped viewer opens any size instantly; 14-type inspector at caret (ints LE/BE, floats, GUID, epoch, FILETIME); selection, copy-as-hex, bookmarks, nav history, goto, multi-tab; **Find** (ASCII/UTF-16/hex/regex, streaming with window overlap); signature badge + extension-mismatch flag; one-click route to the right tool | `Cinder.Hex`, `HexViewModel`, `Inspector` |
| **Strings** | works | ASCII + UTF-16LE extraction with min-length; live filter with **modes**: substring, regex, or a **feature preset** (email, URL, IPv4/6, domain, Luhn-checked card numbers, phone, BTC/ETH, MD5/SHA-1/SHA-256, Windows/UNC paths, MAC, JWT, AWS key, private key, Base64); gibberish suppression; container-format banner; double-click → hex offset; CSV/JSON export | `ToolImplementations.cs` (StringsTool), `Core/Analysis/FeatureExtractor` |
| **Gallery** | works | Image viewer with EXIF panel (MetadataExtractor); GPS → Map | `GalleryTool.cs` |
| **Documents** | works | Text extraction for DOCX/DOCM, XLSX/XLSM, PPTX, ODT/ODS/ODP, EPUB, RTF, PDF (PdfPig), HTML/XML, 20+ text/code formats; 50 MB in / 2 MB out caps; XXE closed | `Services/DocumentReader` |
| **Filesystem** | works | `DiscUtilsWalker` (in `Cinder.Filesystems`, tested against an in-memory NTFS volume): NTFS / FAT / ext2-4 / ISO9660, whole-disk images per partition, VHD/VHDX, E01 via `EwfReader`; all timestamps; **deleted-file recovery** from the NTFS $MFT (`IsDeleted = true`); **optional hashing + hash-set verdict** per file (Settings ▸ Hash sets) so `Unknown` in the row filter is the known-good filter; metadata row shows recorded E01 hashes labelled UNVERIFIED | `Cinder.Filesystems/DiscUtilsWalker.cs`, `Phase3To10ParserTools.cs` |
| **Registry** | works | Eric Zimmerman `Registry` lib walk of any hive (NTUSER/SYSTEM/SOFTWARE/SAM/Amcache) with transaction-log replay; row and depth budgets with truncation banner | `WindowsParserTools.cs` |
| **Event Log** | works | `evtx` lib; every record with time, channel, provider, id, level, user, computer, mapped description | " |
| **Prefetch** | works | `Prefetch` lib; all 8 run times, run count, loaded files/dirs; folder or single file | " |
| **Shellbags** | works | BagMRU walk via `Lnk.ShellItems` decoders, reconstructed paths | `Phase3To10ParserTools.cs` |
| **Jumplists** | works | Automatic + custom destinations, AppId resolution | `WindowsParserTools.cs` |
| **LNK** | works | `Lnk` lib; target, args, working dir, MAC times, volume serial, machine id | " |
| **Browser history** | works | Chromium family + Firefox SQLite read (staged copy so locked DBs parse) | " |
| **USB history** | works | Registry-driven USBSTOR across ControlSets | " |
| **Wi-Fi history** | works | `NetworkList\Profiles` SSIDs + timestamps | " |
| **SRUM** | works | ESE via `Microsoft.Database.Isam`; app resource usage, network usage, energy; SID + AppId resolution | `Phase3To10ParserTools.cs` |
| **Amcache** | works | `InventoryApplicationFile` / legacy `File` | `WindowsParserTools.cs` |
| **ShimCache** | works | Win8/10/11 AppCompatCache decoder | " |
| **Recycle Bin** | works | `$I` decode: original path, size, deletion time, owning SID | `Phase3To10ParserTools.cs` |
| **Email** | works / partial | `.msg` (MsgReader), `.eml`, `.mbox` in-process; `.pst`/`.ost` via inline pypff script (needs Python + libpff-python) | `WindowsParserTools.cs` |
| **Linux artifacts** | works | shell history, auth.log, syslog, cron, passwd/shadow, SSH known_hosts from a mounted root or triage folder; binary journal pending | `Phase3To10ParserTools.cs` |
| **Memory** | works (external) | `Vol3Runner` shells to `python -m volatility3 … -r json`; pstree/psscan/netscan/dlllist/malfind/hashdump/lsadump | `Services/Vol3Runner` |
| **Network (PCAP)** | works | SharpPcap + PacketDotNet; per-packet time/proto/endpoints/flags; 50k cap; terminates on corrupt captures | `Phase3To10ParserTools.cs` |
| **Mobile backup** | works | iOS `Manifest.db` enumeration; Android `.ab` header + TAR walk; encrypted backups surfaced, not decrypted | " |

Every grid tool inherits `SidecarToolViewModel`: pick evidence → `LoadAsync` → `Rows`;
`AddRows(rows, budget)` flags truncation (status line + banner); **row filter** across all
columns (`VisibleRows`); **Export CSV / JSON** (formula-injection safe, exports what the
filter shows); **Bookmark selected** with a note → case `bookmarks` table + custody
annotation; every run is written to the custody log.

## 4. Tools — Analyze

| Tool | Status | What it does |
|---|---|---|
| **Super-timeline** | works | `TimelineIngester` walks a triage folder: EVTX records, Prefetch run times, LNK MAC times, NTUSER UserAssist (ROT13), Chromium/Firefox history, `.eml/.msg` dates, `$I` deletions. Merged, sorted, histogram, filters (from/to, user, source, text, **ATT&CK**). **ATT&CK auto-tagging** on ingest (`MitreTagger`: Security/Sysmon/PowerShell event ids, execution evidence, deletions). **Export** to Timesketch JSONL, Timesketch CSV, and Sleuth Kit bodyfile — every matching event, not just the visible 5,000. |
| **Map** | works | EXIF GPS auto-ingest from a folder of images; manual points |
| **Comm graph** | works | `.eml/.msg/.mbox` From/To → directed who-talked-to-whom, deduped identities, degrees |
| **Full-text search** | works | Lucene.NET index built from a folder (DocumentReader for structured formats, strings fallback for binaries); standard query syntax |
| **Hash sets** | works | NSRL minimal-CSV import into SQLite (~200k rows/s), lookup by algorithm; database path remembered in Settings and consumed by the Filesystem walk for per-file verdicts |
| **YARA** | works (subset) | `YaraLite`: rule parser + Aho-Corasick for literal/hex/nocase strings and `any/all of them`; regex strings and modules unsupported |
| **IOC match** | works | Indicator list (`IocList`: hashes / IPv4 / IPv6 / domain / URL / email / text, CSV-tolerant) × folder. `IocScanner` matches file hashes, paths, contents (YARA-lite, ASCII + UTF-16LE) and timeline events. Bounded and reports its limits |
| **VirusTotal** | shell | Hash-only lookup client; UI shell |
| **AI Copilot** | works (BYOM) | Ollama / LM Studio / OpenAI-compatible; structured prompts from parsed artifacts (never raw bytes); disabled by default; help text warns about cloud egress; API key encrypted at rest |

## 5. Tools — Acquire

| Tool | Status | What it does |
|---|---|---|
| **Disk imager** | shell | UI; acquisition needs the Python imager sidecar (libewf) or the unshipped driver |
| **Image verify** | works | In-process. E01: `EwfReader.VerifyAsync` re-reads the decoded media and compares to the recorded MD5/SHA-1. Raw: hashes and compares to a `.sha256/.sha1/.md5` companion or a `SHA256SUMS` line. Three distinct outcomes — verified / failed / **unverifiable** — and the result is written to custody with digests |
| **Mount image** | partial | VHD/VHDX/ISO via `Mount-DiskImage` (Windows); Linux `losetup`+`mount` read-only; E01 needs Arsenal Image Mounter |
| **Convert format** | shell | |
| **Shadow copies** | works | `vssadmin` (Windows), btrfs/LVM/ZFS snapshots (Linux) |
| **RAM capture** | shell | winpmem / LiME fallbacks when present; no bundled driver |
| **File carver** | works | Vectorised header/footer carving, 30+ signatures, validators for JPEG/PNG/PE, exact window ownership (no duplicate hits), slack/unallocated regions |
| **Cloud pull** | shell | Google Drive / OneDrive / Dropbox OAuth PKCE scaffolds; token exchange unfinished; user-supplied client ids |

All helper binaries (`python`, `powershell`, `vssadmin`, `lsblk`, `blockdev`, `zfs`,
`wkhtmltopdf`, browsers) are resolved through `ExecutableResolver` — system directory then
PATH, never the application or working directory (a verified planting vector for a
portable exe run as Administrator).

## 6. Tools — Case

| Tool | Status | What it does |
|---|---|---|
| **Cases** | works | Create / open / recents; multi-case tabs; examiner branches (`CaseBranching`) |
| **Reports** | works | Templates: Expert Witness, Incident Response, Internal Audit, Plain. Sections editor, Markdown preview, **bookmarks → numbered Exhibits section** (note + every row column + who/when + index), export to Markdown / HTML / PDF (QuestPDF: cover, sections, exhibit cards, index, header/footer) / DOCX (OpenXml, core properties) / JSON playbook. Exports are logged to custody |
| **Chain of custody** | works | View + verify. Now records: case created/opened, every parser run (tool, evidence, row count, truncation), verifications (digests + verdict), mounts, data exports, report exports, manual hashes |
| **Workflows** | works | JSON DAG, topological run; handlers `open-image`, `hash`, `registry`, `fs-enumerate`, `carve`, `report`, `index`; `ai-summary` degrades without a provider |
| **Plugins** | partial | C# DLL loading gated by `.cinder-trusted` sentinel + `.cinder-plugins.sha256` manifest; Python scripting host; no isolation yet |
| **Settings** | works | Theme, density, Python path, AI provider (key encrypted), cloud client ids, plugins, update check opt-out |

Also: **Home dashboard** (recent cases/evidence, first-run guide), **command palette**
(Ctrl+K), **per-tool help** (`?`/F1, written for every tool in `BuiltInToolsHelp.cs`),
**Hash dialog** (multi-hash any file, logged to custody), **crash bundle** handler.

## 7. Cross-cutting behaviours worth knowing

- **Evidence opening.** `EvidenceOpener.Open(path)` returns a seekable `Stream` for E01
  (multi-segment) or raw. Everything byte-oriented consumes that.
- **Truncation is never silent.** Parsers cap rows (5k–100k); `IsTruncated` drives a banner
  and the status line, and the custody entry records it.
- **Custody logging.** `ActiveCaseContext.LogAsync(CustodyAction.X, details)` from anywhere;
  no-op without an open case; never throws.
- **Export.** `TabularExporter` (CSV/JSON from any row objects, formula-injection guard);
  `TimelineExporter` (Timesketch JSONL/CSV, bodyfile).
- **ATT&CK.** `MitreTagger.Tag(source, summary)` is applied by `SuperTimeline.Add` whenever
  a caller supplies no tags; filter by id prefix; `Describe(id)` for names.
- **Hostile input.** `EwfReader` bounds every length/count/offset, requires forward progress
  in the section chain, caps decompression; `HexSearch` and `FeatureExtractor` regexes carry
  match timeouts; `EncryptedBundle` counts real extracted bytes and guards zip-slip; XML
  readers disable DTDs.
- **No telemetry** beyond the opt-out GitHub release check.

## 8. Tests and CI

`tests/`: `Cinder.Core.Tests` (custody, cases, hashing, signatures, search, DOCX, plugin
loader, `ExecutableResolver`, `EncryptedBundle`, `TabularExporter`, `TimelineExporter`,
`MitreTagger`, `FeatureExtractor`), `Cinder.Imaging.Tests` (synthetic EWF containers —
round-trip, verification pass/fail/unverifiable, damage, every hostile-input case),
`Cinder.Carving.Tests` (window boundaries, duplicates, non-seekable sources, slack regions),
`Cinder.Hex.Tests` (termination, boundary spanning, mmap, short reads), `Cinder.Native.Tests`.
180 tests; `dotnet test` exits 0. `DiscUtilsWalkerTests` formats a real NTFS volume in
memory; `BookmarkStoreTests` includes a v1 → v2 schema migration; `IocListTests` covers
classification.

CI (`.github/workflows/ci.yml`): build + test on Windows and Linux with job timeouts,
`dotnet format` gate, dependency-audit gate, Python lint. Release (`release.yml`) refuses to
publish unless the suite passes on both platforms; Windows binary is currently unsigned
(SignPath pending). Dependabot covers NuGet, pip and Actions.

## 9. Known gaps (the honest list)

1. **Parser parity is unverified for the Windows-artifact parsers.** None is diffed against a
   reference tool; needs the CFReDS/tsk corpora. README marks Phase 4 "shipped, unverified".
   The filesystem walker is the exception — it has a real in-memory NTFS fixture.
2. **Most parsing still lives in view-models.** `WindowsParserTools.cs`,
   `Phase3To10ParserTools.cs`, `ToolImplementations.cs` hold the real parsers; the
   `Cinder.Artifacts.*` projects are thin. The filesystem walker has been moved down to
   `Cinder.Filesystems`; the same move for registry/EVTX/etc. is what would make them testable.
3. **Custody chain has no external anchor** (documented; options listed in SECURITY.md).
4. **Deleted-file recovery is names only**; contents via the carver. No $UsnJrnl/$LogFile.
5. **Bookmarks have no browser of their own** — they surface through Reports; a case-wide
   list/delete view is missing.
6. **IOC matching is folder-scoped**, not against the Lucene index or an open case's parsed
   grids; hash indicators require exact digests.
7. Imaging, RAM capture, convert, VirusTotal, cloud pull are shells or externally dependent.
8. Windows binary unsigned; no SBOM in release; Actions pinned by tag not SHA.
9. Linux paths (0600 settings, loop mounts, the CA1416-suppressed NTFS walk) compile but have
   only been exercised on Windows.

## 10. Conventions when adding to Cinder

- New grid tool: subclass `SidecarToolViewModel`, override `LoadAsync`, add rows via
  `AddRows(rows, budget)`; register in `WorkspaceViewModel`; write its `HelpMarkdown` in
  `BuiltInToolsHelp.cs`. Export, truncation banner and custody logging come for free.
- Anything that spawns a process: `ExecutableResolver.ResolveRequired(name)` first.
- Anything evidential (mount, verify, export, parse): `ActiveCaseContext.LogAsync(...)`.
- Anything parsing attacker-controlled bytes: bound every length/count/offset, add a
  timeout to every regex, and write a hostile-input test that would hang or OOM the old code.
- Test names are prose (`Verify_rejects_a_container_whose_recorded_hash_is_wrong`); the
  async-suffix rule is off under `tests/`.
- `dotnet format --verify-no-changes --severity warn` must pass; Release build is
  warnings-as-errors.

## 11. State of the working branch

Branch `fix/correctness-evidence-integrity` (PR #1) carries: the hex-search termination fix,
EWF hardening + verification, carver rewrite, truncation banners, CI/release gates, the
security audit fixes (`ExecutableResolver`, bundle cap, settings mode), and — from the
follow-up pass — grid/timeline/strings export, ATT&CK tagging + filter, Strings feature
presets, NTFS deleted-entry recovery, and custody logging of examiner actions.
