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
  Double-click a row → jump to the byte in the Hex viewer.
- ✅ **Documents tool real extraction** — DOCX/DOCM, XLSX/XLSM, PPTX,
  ODT/ODS/ODP, EPUB, RTF, PDF (via PdfPig), HTML/XML, and 20+
  plain-text/code formats. ZIP-based formats parse the inner XML
  in-process; PDF uses PdfPig; RTF strips control words; HTML drops
  tags. Hard caps at 50 MB input / 2 MB output to keep the UI fast.

## Phase 2 — Imaging & verification 🟡

UI surfaces exist for every tool. Image acquisition / mounting / shadow
copies are placeholders until the platform-specific drivers ship.

- 🟡 Disk imager (E01 / AFF4 / raw) — UI shell; native acquisition pending
  per-platform raw-device access work.
- 🟡 Image verify — UI shell; bit-compare logic implemented but unsignaled.
- 🟡 Mount image — VHD/VHDX/ISO via PowerShell `Mount-DiskImage` on Windows
  works; E01 mount requires Arsenal Image Mounter (free, external).
- 🟡 Convert format — UI shell; conversion service pending.
- 🟡 Write-blocker (Windows) — placeholder, real version blocked on a
  signed kernel driver in `drivers/cinder-wb-windows`.
- 🟡 Write-blocker (Linux) — `blockdev --setro` wrapper, works.

## Phase 3 — Filesystem & carving 🟡

- 🟡 Filesystem browser (NTFS / FAT / ext / APFS / HFS+ / Btrfs / XFS /
  ISO) — UI shell; pytsk3 sidecar pending.
- 🟡 File carver (header+footer, 30 default signatures) — UI shell;
  carving engine pending.

## Phase 4 — Windows artifacts 🟡

- 🟡 Registry (regipy sidecar — schema in place, loader stub).
- 🟡 Event Log (python-evtx) — schema in place, loader stub.
- 🟡 Prefetch — UI shell.
- 🟡 Shellbags — UI shell.
- 🟡 Jumplists — UI shell.
- 🟡 LNK shortcuts (pylnk3) — UI shell.
- 🟡 Browser history (Chrome / Edge / Firefox / Brave / Opera) — UI shell.
- 🟡 USB history (USBSTOR + MountedDevices + SetupAPI) — UI shell.
- 🟡 Wi-Fi history — UI shell.
- 🟡 SRUM (libesedb-python) — UI shell.
- 🟡 Amcache (regipy) — UI shell.
- 🟡 ShimCache (regipy) — UI shell.
- 🟡 Email PST/MBOX (libpff-python) — UI shell.

## Phase 5 — Linux artifacts 🟡

- 🟡 shell history · auth log · journalctl · cron · SSH · trash · packages
  · systemd — UI shell, parsers in `Cinder.Artifacts.Linux`.

## Phase 6 — Search, timeline, hash sets, YARA, VirusTotal 🟡

- 🟡 Lucene.NET case-wide index — wired, awaiting artifact ingestion.
- 🟡 Super-timeline — view exists, merge logic pending.
- 🟡 Map (Mapsui.Avalonia) — UI shell.
- 🟡 Comm graph (LiveCharts2) — UI shell.
- 🟡 Hash sets (NSRL bulk import) — UI shell.
- 🟡 YARA (dnYara.NetStandard) — UI shell.
- 🟡 VirusTotal hash-only lookup — UI shell.

## Phase 7 — Memory forensics ⬜

- ⬜ RAM capture (signed driver Windows / LiME Linux) — needs signed kernel
  driver.
- 🟡 Volatility 3 wrapper — UI shell.

## Phase 8 — Reporting & case management 🟡

- 🟡 Court / IR / audit report templates (Markdown / HTML / PDF / DOCX) —
  UI shell, template engine pending.
- ✅ Custody chain view — fully working.
- ✅ Case create / open / branch — working.
- 🟡 Workflows (visual node-graph automation) — UI shell, runtime pending.
- 🟡 Plugins (C# SDK + Python scripting host) — UI shell.

## Phase 9 — AI copilot ⬜

- 🟡 BYOM provider selection (Ollama / LM Studio / OpenAI-compatible) —
  UI shell.
- ⬜ Structured artifact prompts.

## Phase 10 — Network, mobile, cloud ⬜

- 🟡 Network (PCAP / PCAPNG via dpkt + scapy) — UI shell.
- 🟡 Mobile backup (iOS / Android) — UI shell.
- 🟡 Cloud pull (Google Drive / OneDrive / Dropbox via OAuth/PKCE) — UI
  shell.

---

## Cross-cutting

- 🟡 Win/Mac/Linux installers — release workflow produces self-contained
  single-file binaries; .msix, .deb, .rpm, AppImage packaging pending.
- ⬜ **Code signing** — Windows SignPath Foundation application in flight.
- ⬜ Self-update channel.

## How to influence the roadmap

- Open an issue with the `roadmap` label.
- Vote with reactions on existing roadmap issues.
- Submit a PR — the fastest way to ship something.
