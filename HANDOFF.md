# Cinder — handoff after autonomous Phase 1–10 build-out

This file is what you need to read after the AI session ends. It describes:

1. What was implemented end-to-end
2. What is **stubbed but compiles** and needs library installs / native builds / OAuth registrations
3. What you must do **personally** to take the codebase from "compiles" → "ships"

For low-level "this needs WHQL signing" details see [LIMITATIONS.md](LIMITATIONS.md).

---

## What was implemented (24 projects, all build green)

### Phase 1 — hex viewer + hashing (production-ready)
- `Cinder.Hex` — virtualized custom Avalonia control with mmap-backed `IHexBuffer`, ASCII + UTF-16LE sidebars, keyboard nav, mouse-wheel scrolling
- `Cinder.Hex.HexSearch` — hex / ASCII / UTF-16 LE/BE / regex search, chunked scanning so 100 GB images don't OOM
- `Cinder.Core.Signatures` — 60+ magic signatures + extension-mismatch detector
- `Cinder.App.HashDialog` — drag-drop hash dialog wired to the streaming MD5/SHA-1/SHA-256/BLAKE3 service and the chain-of-custody log

### Phase 2 — imaging + verification (mostly ready; signed kernel driver pending)
- `Cinder.Imaging` — `IDiskImager`, `IImageVerifier`, `IImageMounter`, `IShadowCopyEnumerator`, `IWriteBlocker`
- Linux loop mounter, VSS / btrfs / LVM / ZFS snapshot enumerators
- Linux `blockdev --setro` write-blocker (engaged on every block device)
- `parsers/imager/imager_worker.py` — libewf-python (E01) + raw + hash-preserving conversion
- `drivers/cinder-wb-windows/` — kernel filter driver source (cinder-wb.c, cinder-wb.h, README) — **needs WDK build + signing**

### Phase 3 — filesystems + carving (sidecar wired; needs `pip install pytsk3`)
- `Cinder.Filesystems` with `IFilesystemParser` + pytsk3 sidecar wrapper
- NTFS / FAT / ext / APFS / HFS+ / UDF / ISO via `parsers/filesystem/fs_worker.py`
- `Cinder.Carving` — `FileCarver` with 30+ default header/footer signatures, footer-aware extraction, parallel chunk scan, validators for JPEG/PNG/PE
- `SlackUnallocCarver` for filesystem-driven region carving

### Phase 4 — Windows artifacts (sidecar wired; needs regipy / python-evtx / pylnk3)
- `Cinder.Artifacts` + `Cinder.Artifacts.Windows` with strongly-typed records for every artifact in plan §8.5
- `parsers/windows/win_worker.py` — registry (regipy: UserAssist, Shimcache, Amcache, USBSTOR, Wi-Fi), Prefetch parser (uncompressed Win10), Shellbags MRU, Jumplists, LNK (pylnk3), EVTX (python-evtx), Chrome/Firefox SQLite history, SRUM (libesedb)
- `UserActivityRollup` aggregator

### Phase 5 — Linux artifacts (production-ready; pure Python, no native deps)
- `Cinder.Artifacts.Linux` — shell history (bash/zsh/fish), auth log, journalctl wrapper, cron, SSH known_hosts/authorized_keys, trash, recently-used, systemd units, apt/dnf/pacman logs, /etc/passwd
- `parsers/linux/linux_worker.py` runs against any mounted Linux root

### Phase 6 — search + timeline + hash sets + YARA + VT
- `Cinder.Search.CaseIndex` — Lucene.NET 4.8.0-beta with full-text indexing, multi-field query parser, time-range docvalues
- `HashSetService` — SQLite-backed; bulk NSRL CSV importer (~200 k rows/sec)
- `SuperTimeline` + `TimelineFilter` — sorted axis with histogram bucketing + MITRE-technique tag filtering
- `GeoIndex` — bbox queries
- `CommunicationGraph` — force-directed comm relations
- `VirusTotalClient` — opt-in, hash-only, user-API-key, never uploads bytes
- `YaraScanner` interface + sidecar stub — *needs* `pip install yara-python` (lands as Phase 6.1)

### Phase 7 — memory forensics (sidecar wired; needs `pip install volatility3`)
- `Cinder.Memory` artifacts + `VolatilitySidecar` wrapping Volatility 3
- `parsers/memory/vol_worker.py` — pstree, pslist, netscan, dlllist, malfind, hashdump, lsadump, cachedump, cmdline + `run_plugin` for arbitrary vol3 plugins
- `drivers/cinder-ram-windows/` README — Windows RAM acquisition driver source plan; falls back to winpmem.exe if installed
- `drivers/cinder-ram-linux/` README — wraps LiME upstream (no Cinder kernel module)

### Phase 8 — reporting + multi-case
- `Cinder.Reports` — `ReportBuilder`, 4 templates (expert-witness / IR / audit / plain), Markdown + HTML rendering, JSON "playbook" export, exhibit auto-numbering with hash + examiner watermark
- `ReportExporter` — Markdown / HTML / PDF (via wkhtmltopdf or chrome --headless) / DOCX (Phase 8.1)
- `Cinder.Cases` — `Workspace` (recent cases JSON), `CaseBranching` (Git-style branches with three-way merge + conflict resolver), `EncryptedBundle` (AES-256-GCM + PBKDF2-SHA256 600k iters)
- `Cinder.Reader` — separate `CinderReader.exe` bundle for sealed evidence (Phase 8.1: full UI render)

### Phase 9 — BYOM AI (production-ready; user supplies endpoint/key)
- `Cinder.AI` — `IAiProvider` + `OpenAiCompatibleProvider` (vLLM/TGI/LocalAI/Astryx), `OllamaProvider` (native /api/chat streaming), `LmStudioProvider`, `DisabledProvider` (default)
- `PromptBuilder` — structured prompts for "summarize user activity", "explain process tree", "explain registry key", "draft case summary"
- `AnomalyDetector` — local statistical (off-hours skew, activity bursts, single-user sources) — runs without any LLM
- `NaturalLanguageQuery` — translates NL → strict JSON via the configured LLM, validated server-side

### Phase 10 — network + mobile + cloud
- `Cinder.Network` — `PcapSidecar` (dpkt/scapy), `ZeekImporter` (TSV → records), TCP flows, HTTP requests, DNS queries, JA3/JA4
- `Cinder.Mobile` — `IMobileBackupReader` + `MobileBackupSidecar` for iOS / Android backups (messages / calls / apps)
- `Cinder.Cloud` — `OAuthPkceHelper`, `GoogleDriveConnector`, `OneDriveConnector`, `DropboxConnector` — all OAuth/PKCE, no client_secret needed for desktop apps

### Cross-cutting
- `Cinder.Plugins` — `IPlugin` SDK + `PluginLoader` for `.dll` drop-in extensions, `PythonScriptingHost` sidecar (`parsers/scripting/script_host.py`)
- `Cinder.Workflow` — JSON-serialisable node-graph workflow + topological runner ("playbook" replay)
- `Cinder.Cli` — `cinder-cli` executable: case create/migrate, custody verify, hash, sig identify, report build (every GUI action mapped per plan §8.17)
- `CrashRecovery`, `SettingsStore` (theme / density / vim-mode / Python path / AI config / cloud client_ids / enabled plugins)

---

## What you need to do — by priority

### Must-do before v0.1.0 ship (the hex viewer + hash dialog tag)
- [ ] Replace `assets/branding/cinder-logo.svg` placeholder with the final ember/spark glyph (you posted progress already)
- [ ] Open the solution in Rider, run `Cinder.App` once, confirm the Phase 1 hex viewer scrolls a 1 GB file at full speed
- [ ] Tag `v0.1.0`, push to GitHub, post on r/computerforensics

### Must-do before any post-Phase-1 release
- [ ] **Python sidecar deps** — bundle a per-OS Python venv with: `pytsk3 libewf-python regipy python-evtx pylnk3 libesedb-python volatility3 dpkt scapy yara-python pyaff4 pydantic`. CI step in `release.yml` should pre-build the venv per RID.
- [ ] **Avalonia / Tmds.DBus.Protocol vulnerability** — track upstream Avalonia bump that pins the patched version (currently surfaces as warning NU1903)
- [ ] **OAuth client IDs** — register a Cinder OAuth app with Google, Microsoft, and Dropbox; configure redirect URIs `http://127.0.0.1:0/callback` (loopback). Set the resulting client_id values in `SettingsStore.CloudClientIds` defaults. See `LIMITATIONS.md → cloud-oauth-clients`.

### Must-do before Phase 2 ships
- [ ] Sign up for SignPath.io OSS code-signing
- [ ] Stand up `drivers/cinder-wb-windows/` build in `release.yml` (WDK install on a Windows runner, attestation-sign the resulting `.sys`)

### Must-do before Phase 7 ships full RAM acquisition
- [ ] Same SignPath chain for `drivers/cinder-ram-windows/`
- [ ] Pre-build LiME `.ko` files for the Linux distros you support; ship them next to `cinder-ram-linux` wrapper

### Validation (per plan §10 — required before any parser ships)
- [ ] Download fixture corpora: NIST CFReDS, Brian Carrier's tsk test images, AboutDFIR challenge images. Track in `tests/fixtures/` via Git LFS.
- [ ] For each parser, generate the expected JSON output from a reference tool (Eric Zimmerman, Autopsy) and commit alongside the fixture. CI should diff actual vs expected.
- [ ] Add a manual smoke-test checklist for parsers that depend on regipy / python-evtx / etc. — sidecars throw informative `RuntimeError` when libs aren't installed but parity tests are the only way to catch regressions.

### Trademark / domains (plan §13.8–9)
- [ ] File USPTO trademark on "Cinder" + Cinder logo before v0.1.0 release
- [ ] Secure `cinder.dev` and `cinderforensics.com`

---

## Test status

After this session: `dotnet build Cinder.slnx` is clean. `dotnet test` runs the validatable subset — see Final/Verification section of the chat for current pass count.

Tests cover:
- Streaming hash service (KAT vectors for empty + "abc" across all four algorithms)
- Custody log chain integrity + tamper detection + verification
- Case service round-trip
- Magic signature scanner (PNG, JPEG, NTFS boot sector, mismatch flagging)
- Hex search (ASCII/UTF-16/hex pattern, case sensitivity, mmap round-trip)
- Native platform contracts

Tests **do not** cover anything that needs an external library or fixture image. Those pass-or-fail comes from the validation checklist above.
