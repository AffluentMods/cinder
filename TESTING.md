# Testing Cinder before you publish

This is the runbook for verifying a Cinder build end-to-end before tagging
a release or showing the project off. It's intentionally exhaustive — work
top-to-bottom for a fresh repo or a fresh PR, or jump to a specific section.

> Everything below is **local** verification on your dev machine. The
> release pipeline runs an overlapping but smaller subset in CI; see
> [`.github/workflows/`](.github/workflows/).

## Table of contents

1. [Build & test gates](#1-build--test-gates) — should be all-green before anything else
2. [Smoke launch](#2-smoke-launch) — does the app actually start?
3. [Per-tool functional walkthrough](#3-per-tool-functional-walkthrough) — exercise every tool against known evidence
4. [Where to get sample evidence](#4-where-to-get-sample-evidence) — public, license-clean test data
5. [Security & supply-chain checks](#5-security--supply-chain-checks)
6. [Release-pipeline dry run](#6-release-pipeline-dry-run)
7. [Documentation review](#7-documentation-review)
8. [Visual / screenshot capture](#8-visual--screenshot-capture)

---

## 1. Build & test gates

Everything in this section must pass before anything else matters.

### 1.1 Restore + clean build

```bash
dotnet restore
dotnet build -c Debug   --nologo
dotnet build -c Release --nologo    # repo treats Release warnings as errors
```

**Pass criterion:** zero errors, zero warnings in Release.

### 1.2 Unit test suite

```bash
dotnet test --nologo
```

**Pass criterion:** all tests green. Current count: 18+ across
`Cinder.Core.Tests`, `Cinder.Native.Tests`, plus the v0.2.0 additions
(plugin loader, DOCX report writer, search).

### 1.3 Format / style

```bash
dotnet format --verify-no-changes
```

**Pass criterion:** zero diff. If this fails, run `dotnet format` and
re-commit.

### 1.4 Vulnerable-package audit

```bash
dotnet list package --vulnerable --include-transitive
```

**Pass criterion:** "no vulnerable packages found" — across every
project. If `Tmds.DBus.Protocol`, `System.Text.Json`, or anything else
shows up, pin a patched version in `Directory.Packages.props`.

### 1.5 Python sidecars (if you exercised them)

```bash
cd parsers
python -m venv .venv
source .venv/bin/activate    # or .venv\Scripts\Activate.ps1 on Windows
pip install -e .
pytest
black --check .
ruff check .
mypy .
```

**Pass criterion:** all green. Sidecars are optional for many tasks now —
v0.2.0 moved most Windows artifact parsers in-process — but if you touched
`parsers/`, run this.

---

## 2. Smoke launch

### 2.1 First-launch — fresh user state

Move your existing `%LOCALAPPDATA%\Cinder\` (Windows) or
`~/.config/cinder/` (Linux) aside, then:

```bash
dotnet run --project src/Cinder.App
```

**Expected behaviour:**
- Window opens within ~2 seconds.
- Taskbar / title bar shows the Cinder ember icon (not Avalonia's
  default).
- The **Home dashboard** appears with the "First time?" 5-step guide,
  empty recent-cases list, and empty recent-evidence list.
- Status bar shows the green dot + a friendly idle message.
- No exceptions in stdout / stderr.
- No crash bundles written to `%LOCALAPPDATA%\Cinder\crashes\`.

### 2.2 First-launch — Python bootstrap

The first time a tool that needs a sidecar runs, `PythonBootstrap`
auto-creates `%LOCALAPPDATA%\Cinder\venv` and pip-installs the pinned
forensic dependencies. Trigger it once (the PST/OST email path is a good
trigger) and verify:

- A progress dialog appears explaining what's installing and why.
- The venv lands at the expected path.
- Subsequent launches don't re-run the bootstrap.

### 2.3 Re-launch — state persistence

Quit, relaunch. Verify:

- Recent cases / recent evidence persist (`%LOCALAPPDATA%\Cinder\recents.json`).
- Last window position / size is restored.
- Settings (theme, AI provider, etc.) survive.

---

## 3. Per-tool functional walkthrough

Exercise each tool against the smallest piece of real evidence that
proves it works. Capture a screenshot of each working tool — you'll want
them for the README and release page.

For every tool below: open it, point it at the evidence, confirm rows
populate, confirm the F1 help text matches what the tool actually does,
confirm the "?" help button works.

### 3.1 Examine

| Tool | Test evidence | Pass criterion |
|---|---|---|
| **Hex** | Any binary file — try a 1 GB+ image | Opens instantly; scrolling smooth; inspector decodes integers/floats/GUID/FILETIME at the caret; Ctrl+F finds a string; Ctrl+D bookmarks |
| **Strings** | Same file | "Hide gibberish" toggle works; HEADS-UP banner appears on ZIP/PDF input; double-click row jumps to byte in Hex |
| **Signatures** | A `.docx` renamed to `.txt` | Extension-mismatch flagged; auto-route to Documents works |
| **Hash** | Any file | All four hashes match `certutil -hashfile` (Win) or `sha256sum`/`md5sum` (Linux); progress reports while running |

### 3.2 Acquire

| Tool | Test evidence | Pass criterion |
|---|---|---|
| **Mount** | `.vhd` / `.vhdx` / `.iso` | File system mounts; un-mount cleanly; status-bar evidence chip updates |
| **Verify** | Two copies of the same image | Bit-compare matches; one-byte change reports mismatch with offset |
| **Custody** | New case | Genesis row appears; every action (file open, hash, report export) appends a hash-chained row |
| **WriteBlock** | Linux only | `blockdev --setro` engages on a loopback device; readback rejects writes |

### 3.3 Analyze — Windows

For these you'll want a sample Windows hive set. Eric Zimmerman publishes
clean reference hives ([linked below](#41-windows-forensic-test-data)).

| Tool | Test evidence | Pass criterion |
|---|---|---|
| **Filesystems** | A `.dd` or `.E01` containing NTFS | Tree populates; right-click "Export file" writes a valid copy |
| **Registry** | Any `NTUSER.DAT` / `SYSTEM` | Key/value tree populates; transaction-log replay flag visible |
| **EVTX** | A `.evtx` from `C:\Windows\System32\winevt\Logs\` | Rows stream in; filter by EventId works |
| **Prefetch** | A `.pf` file | Last-run times (×8), run count, loaded-files count populate |
| **LNK** | Any `.lnk` from your Desktop | Target, args, MAC times, machine volume serial populate |
| **Jumplists** | `%APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations\*` | Entries populate; AppId resolved to friendly name where possible |
| **Shellbags** | A user `NTUSER.DAT` | Full traversal paths reconstructed (e.g. `My Computer\C:\Users\…`) |
| **USB history** | `SYSTEM` hive | Vendor/Product/Serial/First-install populate per device |
| **Wi-Fi history** | `SOFTWARE` hive | SSID list populates with last-seen |
| **Amcache** | `Amcache.hve` | Path/hash/publisher/version populate |
| **ShimCache** | `SYSTEM` hive (Win10/11) | Full paths + last-modified populate |
| **SRUM** | `SRUDB.dat` | All three extension tables (app resource, network, energy) decode; SIDs render as `S-1-…` |
| **Browser history** | `History` SQLite from Chromium/Edge/Firefox | URLs + visits populate; locked DB (browser running) still parses via staged copy |
| **Email** | Any `.msg` / `.eml` / `.mbox` | From/To/Subject/Body populate; attachments listed |

### 3.4 Analyze — Linux

| Tool | Test evidence | Pass criterion |
|---|---|---|
| **Linux artifacts** | Any mounted Linux root or triage folder | shell history, auth.log, syslog, crontab, passwd, shadow, SSH known_hosts all categorise and normalise timestamps |
| **Network** | Any `.pcap` / `.pcapng` | First 50k packets populate; protocol/IP/port/bytes/TCP-flags correct |

### 3.5 Mobile

| Tool | Test evidence | Pass criterion |
|---|---|---|
| **Mobile (iOS)** | Any iTunes backup folder | Manifest.db enumerates every file with domain + relativePath + fileID |
| **Mobile (Android)** | Any `.ab` from `adb backup` | Per-package entries populate; encrypted backups surface "need adb password to decrypt" row |

### 3.6 Analyze — search, scan, plot

| Tool | Test evidence | Pass criterion |
|---|---|---|
| **Search** | Any folder of mixed-format evidence | "Build index from folder…" walks recursively, structured formats parsed, binaries get strings extraction; Lucene queries (`source:evtx`, `user:alice`, `text:"exact phrase"`) return correct hits |
| **YARA** | A folder of `.yar` files + a target binary | Rules parse (literal + nocase + hex + boolean conditions); per-rule hit summary + per-pattern offset list correct |
| **Map** | A folder of phone photos with GPS EXIF | One point per geo-tagged image; filename + mtime in popup |
| **Graph** | A folder of `.eml` / `.msg` / `.mbox` | Directed who-talked-to-whom DAG; in/out degree counts correct; deduplicated identities |
| **Carve** | Raw image with deleted JPEGs | Header+footer scan finds them; carved files open in an image viewer |

### 3.7 AI

| Tool | Setup | Pass criterion |
|---|---|---|
| **AI Copilot** | Install Ollama locally, set provider = ollama, model = llama3.2 | "Test connection" reports success; chat round-trips; API key (if used) round-trips DPAPI on Windows |

### 3.8 Case management

| Tool | Test evidence | Pass criterion |
|---|---|---|
| **Cases** | New + open + branch + close | All four flows work; multi-case tabs switch correctly; close-tab confirms unsaved work |
| **Reports** | A case with mixed evidence | Generate PDF, DOCX, Markdown, HTML, JSON — each is structurally valid, opens in its native viewer, exhibit cards + index render |
| **Workflows** | Any of the built-in JSON playbooks | DAG executes in topological order; each step's output flows to the next; failure of one step doesn't crash the rest |
| **Plugins** | A signed test plugin + an unsigned one | Trust gate enforces `.cinder-trusted` sentinel; SHA-256 manifest verified; signed-by column populates with CN; untrusted-chain warning surfaces |

---

## 4. Where to get sample evidence

All public, license-clean. Never test against a real case file — sanitise
or use synthetic data.

### 4.1 Windows forensic test data

- **Eric Zimmerman's reference data** — https://ericzimmerman.github.io/
  → "Misc test files" — clean Registry hives, EVTX samples, Prefetch,
  Jumplists, ShimCache.
- **Digital Corpora** — https://digitalcorpora.org/corpora/scenarios/ —
  full case scenarios (M57-Patents, Nitroba, etc.) with documented chain
  of custody and known answers.
- **NIST CFReDS** — https://cfreds.nist.gov/ — the National Software
  Reference Library and curated test images. Authoritative.

### 4.2 Memory captures

- **Volatility public samples** —
  https://github.com/volatilityfoundation/volatility/wiki/Memory-Samples

### 4.3 Network captures

- **Wireshark sample captures** —
  https://wiki.wireshark.org/SampleCaptures
- **Malware-traffic-analysis.net** — https://www.malware-traffic-analysis.net/
  (treat as malware; isolate the host)

### 4.4 Generate your own quickly

```bash
# Tiny NTFS image for filesystem testing
dd if=/dev/zero of=test.dd bs=1M count=64
mkfs.ntfs -F test.dd

# Tiny EVTX (Windows)
wevtutil epl Security %TEMP%\Security-snapshot.evtx
```

---

## 5. Security & supply-chain checks

### 5.1 No secrets in repo

```bash
git ls-files | xargs grep -lE 'sk-[A-Za-z0-9]{20,}|api[_-]?key.*=.*["'\''][A-Za-z0-9]{20,}' || echo "clean"
```

Spot-check: open `Directory.Packages.props`, every `.csproj`, every YAML
under `.github/workflows/`. There should be **zero** hardcoded tokens.

### 5.2 CodeQL clean

The `codeql.yml` workflow runs automatically. Confirm the latest run is
green at
https://github.com/AffluentMods/cinder/security/code-scanning.

### 5.3 SECURITY.md threat-model walk-through

Open SECURITY.md, read each "v0.1.0 hardened against" bullet, and
confirm the relevant code still has the fix (e.g. `Process.Start` call
sites use `ArgumentList`, `wkhtmltopdf` invocations don't include
`--enable-local-file-access`).

### 5.4 LICENSE + copyright headers

Every `*.cs` file should compile against `Apache-2.0`. If you add a
third-party snippet, document its origin in a code comment and confirm
its licence is compatible.

---

## 6. Release-pipeline dry run

Before tagging the next release:

### 6.1 Publish locally and inspect output

```bash
dotnet publish src/Cinder.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/win-x64
```

Confirm:
- `publish/win-x64/` contains **exactly** `Cinder.exe` + `.pdb` /
  `.xml` companions. **No `Lato-*.ttf`, no `OFL.txt`.** If you see
  them, the `Directory.Build.targets` Lato strip isn't engaging — check
  it lands BEFORE `AssignTargetPaths`.
- `Cinder.exe` is ~150 MB self-contained.
- Run `Cinder.exe` from a directory you've never run from before — verify
  it launches without any external dependency.

### 6.2 Smoke-test on a clean VM

Spin up a fresh Windows Sandbox / clean VM / WSL instance, copy in just
the published binary, and launch it. **No .NET install, no Python, no
Visual C++ redist needed.** If anything's missing, the self-contained
publish flags are wrong.

### 6.3 Workflow dispatch

In GitHub Actions, click **Run workflow** on `release.yml` with a test
version string (e.g. `0.2.0-rc1`). Confirm:
- Both `build-windows` and `build-linux` jobs complete green.
- Artifact list contains ONLY `Cinder.exe`, `cinder-linux-x64.tar.gz`,
  `SHA256SUMS.txt` — no font files, no XML/PDB.
- Auto-generated release notes look reasonable.

### 6.4 Verify SignPath integration (when it lands)

Set `vars.SIGNPATH_ENABLED = true` and re-run dispatch. The signed
artifact should appear as `cinder-windows-signed/Cinder.exe` and ship as
the released binary.

---

## 7. Documentation review

Read top-to-bottom in this order. Confirm every claim is current.

| File | Confirm |
|---|---|
| `README.md` | Status table matches ROADMAP; comparison table is fair; install/quickstart actually work; screenshots are real |
| `ROADMAP.md` | ✅ marks really ship; 🟡 marks really are partial; ⬜ marks are honest |
| `CHANGELOG.md` | Has an `[Unreleased]` section ready for next work; each `[x.y.z]` link resolves |
| `CONTRIBUTING.md` | Prereq versions match what the repo actually targets; setup steps run clean on a fresh Windows + Linux box |
| `SECURITY.md` | Disclosure email + advisory link work; threat model still accurate |
| `LIMITATIONS.md` | Each `<a id=…>` anchor resolves; workarounds described still work |
| `CODE_OF_CONDUCT.md` | Contact email reachable |
| `docs/plan.md` | No remaining "Claude Code" references; phase deliverables match ROADMAP |
| `docs/cloud-setup.md` | OAuth client-ID instructions still match each provider's current console UI |

External link check (optional):
```bash
npx markdown-link-check README.md ROADMAP.md CHANGELOG.md CONTRIBUTING.md SECURITY.md LIMITATIONS.md
```

---

## 8. Visual / screenshot capture

Required for the README + release announcement:

| Filename | Capture |
|---|---|
| `assets/screenshots/home.png` | Home dashboard, fresh state, "First time?" 5-step visible |
| `assets/screenshots/hex.png` | Hex viewer with a 1 GB+ image loaded, inspector populated |
| `assets/screenshots/evtx.png` | Event Log viewer with a real `.evtx` loaded, filter applied |
| `assets/screenshots/report.png` | A generated PDF report — page with exhibit cards and the exhibit index |
| `assets/screenshots/registry.png` | Registry viewer with a SYSTEM hive expanded |
| `assets/screenshots/dark.png` (optional) | Same shot in dark vs light to show theme support |

Capture protocol:
- 1920×1080 native, then crop the relevant region.
- Use synthetic / public test data only — never real case material.
- Light AND dark theme captures for at least one shot (shows theme
  support).
- Save as PNG, < 500 KB each (run them through `pngcrush` / `oxipng` if
  needed).
