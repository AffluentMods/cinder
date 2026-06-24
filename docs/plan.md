# Cinder — Project Plan v0.2

> **The open-source forensics toolkit that's both genuinely powerful and genuinely beautiful.**
> Every great feature from X-Ways, FTK, EnCase, Autopsy, Magnet AXIOM, Belkasoft X, Volatility, and the Eric Zimmerman suite — in one modern, fast, native cross-platform app.

---

## Meta

| Field | Value |
|---|---|
| **Name** | Cinder |
| **Studio** | Affluent Labs |
| **License** | Apache License 2.0 |
| **Repo** | `github.com/AffluentMods/cinder` (proposed) |
| **Platforms** | Windows 11 (x64) + Linux (x64). macOS deferred to v2+. |
| **Stack** | C# .NET 10 + Avalonia UI 11 + Python 3.12 sidecars + SQLite |
| **Pricing** | Free, forever. GitHub Sponsors / Open Collective for donations. |
| **Code signing** | SignPath.io (Windows). GPG-signed releases (Linux). |
| **Telemetry** | None. Ever. Zero phone-home. |

### Locked decisions

- **Open source from day one** — Apache 2.0 chosen for patent grant + permissive integration, matches Autopsy/Plaso licensing posture
- **Windows + Linux, both first-class** — DFIR community lives on both, cross-platform doubles addressable users and contributors
- **Avalonia UI over WinUI 3** — single codebase, native rendering on both platforms, JetBrains-grade maturity (Rider's UI uses it)
- **C# core + Python sidecars** — C# for the shell, IO, OS interop; Python for the entire forensic library ecosystem (pytsk3, libewf, regipy, volatility3, plaso, etc.)
- **BYOM AI architecture** — no bundled model, no hard Astryx dependency. Users plug in Ollama / LM Studio / OpenAI-compatible endpoint of choice
- **No secrets in repo** — all API keys are user-supplied via DPAPI/libsecret-protected settings store

---

## How to use this plan

Sections 1–7 are foundational context — mission, architecture, library
choices, design system, design rationale. Read them before picking up any
phase. Section 9 is the phased roadmap; each phase has explicit
**deliverables**, **acceptance criteria**, and **dependencies** so a
contributor can ship a phase end-to-end without ambiguity. The
[top-level ROADMAP](../ROADMAP.md) is the live status tracker that mirrors
section 9 with ✅ / 🟡 / ⬜ marks for what's actually landed.

---

## 1. Mission & positioning

### What Cinder is

A unified, cross-platform digital forensics workstation. One app that consolidates what currently requires 8+ separate tools (Autopsy + FTK Imager + Eric Zimmerman tool suite + Volatility + Hindsight + ExifTool + Plaso + WinHex). Modern UI, real performance, free and open.

### Why it exists

The forensics tool market in 2026 is broken in three ways:

1. **The free tools are slow and ugly.** Autopsy takes minutes to load a case. The Eric Zimmerman tools are CLI islands. WinHex hasn't been redesigned since 1995.
2. **The good tools cost five figures.** EnCase, X-Ways, FTK, Magnet AXIOM are gatekept behind enterprise pricing.
3. **Nothing is both modern and powerful.** Belkasoft and AXIOM look nice but lack X-Ways's depth. X-Ways has the depth but looks like Windows 95.

### Court admissibility argument

Open source is a **feature**, not a tradeoff. When defense counsel can audit the tool that produced evidence, methodology challenges become easier to defeat. This is exactly why the OSS forensic stack (Sleuth Kit, Volatility, Plaso) keeps holding ground in courtrooms against five-figure commercial alternatives.

### Tagline candidates

- *What remains tells the story.*
- *Pull the truth from the ashes.*
- *Forensics, illuminated.*

---

## 2. Stack architecture

### The stack

```
┌─────────────────────────────────────────────────────────────┐
│  Avalonia UI 11 (FluentAvalonia theme)                      │
│  ↕ CommunityToolkit.Mvvm bindings                           │
├─────────────────────────────────────────────────────────────┤
│  Cinder.App (entry point, view models, services)            │
├─────────────────────────────────────────────────────────────┤
│  Cinder.Core (domain model, case mgmt, chain of custody)    │
│  Cinder.Native (P/Invoke + platform abstraction)            │
│  Cinder.Sidecar (sidecar protocol + worker pool)            │
│  Cinder.Search (Tantivy / Lucene.NET FFI)                   │
│  Cinder.AI (provider plugin interface)                      │
├─────────────────────────────────────────────────────────────┤
│  SQLite case DB │ Sidecar workers (Python 3.12)            │
│                 │   ├─ pytsk3 (filesystems)                 │
│                 │   ├─ regipy (registry)                    │
│                 │   ├─ volatility3 (RAM)                    │
│                 │   ├─ libpff (PST/OST)                     │
│                 │   ├─ python-evtx (event logs)             │
│                 │   ├─ libewf-python (E01 imaging)          │
│                 │   └─ plaso (super-timeline)               │
└─────────────────────────────────────────────────────────────┘
```

### Library choices (best-of-everything)

| Concern | Choice | Why |
|---|---|---|
| UI framework | **Avalonia 11** | Cross-platform native, mature, JetBrains-validated |
| UI theme | **FluentAvalonia** | Fluent 2 design system, cross-platform, looks great |
| MVVM | **CommunityToolkit.Mvvm** | Source-generated, zero boilerplate |
| DI | **Microsoft.Extensions.DependencyInjection** | Standard, ubiquitous |
| Logging | **Serilog** + Sinks.File + Sinks.Async | Structured, fast, well-maintained |
| Database | **SQLite** via `Microsoft.Data.Sqlite` + Dapper | Embedded, fast, single-file cases |
| Full-text search | **Tantivy** (Rust, via FFI) | Faster than Lucene, smaller footprint |
| Hashing | **BLAKE3.NET** + native BCrypt | BLAKE3 for fast modern hashing, BCrypt for SHA-family |
| Hex viewer | **Custom Avalonia control** | No third-party hex grid is good enough |
| Charts | **LiveCharts2** | Cross-platform, modern, animatable |
| Maps | **MapsUI** | Avalonia-compatible, offline-capable, OSM/Mapbox tiles |
| Markdown | **Markdig** | The .NET markdown standard |
| JSON | **System.Text.Json** | Built-in, source-gen serialization |
| HTTP | **HttpClient** + **Polly** | Standard with retry/circuit breaker |
| Crypto | **BouncyCastle.Cryptography** | For non-standard primitives |
| Sidecar IPC | **JSON-RPC over stdio** | Simple, language-agnostic, no port conflicts |
| Python runtime | **Python 3.12 embedded venv** | Isolated, deterministic, shipped with installer |
| YARA | **dnYara** + native libyara | YARA scanning for malware indicators |
| Testing | **xUnit** + **FluentAssertions** + **Bogus** | Standard .NET test stack |
| Mocking | **NSubstitute** | Cleaner than Moq |
| Code coverage | **Coverlet** + Codecov | Integrated with CI |
| Static analysis | **Roslyn analyzers** + **SonarCloud** (free for OSS) | Catches issues at compile time |
| Documentation | **DocFX** | Generates API docs from XMLDoc |

### Platform abstraction

Cinder.Native exposes a single `IPlatform` interface; concrete implementations live in `Cinder.Native.Windows` and `Cinder.Native.Linux`:

```csharp
public interface IPlatform {
    IRawDevice OpenDevice(string identifier);     // \\.\PhysicalDrive0 vs /dev/sda
    IWriteBlocker GetWriteBlocker();               // kernel filter vs blockdev --setro
    IShadowCopyEnumerator GetSnapshots();          // VSS vs btrfs/LVM/ZFS snapshots
    IList<MountedVolume> EnumerateVolumes();
    string GetSecureCredentialStore();             // DPAPI vs libsecret/kwallet
    PlatformInfo Info { get; }
}
```

`#if WINDOWS` / `#if LINUX` is used sparingly — almost everything is dispatched through the `IPlatform` interface so the rest of the app is platform-agnostic.

### Performance principles (non-negotiable)

- **Memory-mapped I/O** for all evidence reads — never `File.ReadAllBytes`
- **Streaming hashing** — hash while reading, never re-read
- **Parallel ingestion** — every parser/carver on its own thread pool, throttled by `IngestScheduler`
- **Lazy parsing** — parse on demand, cache results in case DB
- **Background indexing** — Tantivy indexes while user works, never blocks UI
- **No UI blocking** — every operation is `async`/cancellable, no operation > 100ms on UI thread
- **AOT-compile** the shell binary on Windows for sub-second cold start

---

## 3. Repository structure

```
cinder/
├── .github/
│   ├── workflows/
│   │   ├── ci.yml                # build + test on PR
│   │   ├── release.yml           # tag → SignPath → GitHub Releases
│   │   ├── codeql.yml            # security scanning
│   │   └── docs.yml              # docs build + deploy
│   ├── ISSUE_TEMPLATE/
│   │   ├── bug_report.yml
│   │   ├── feature_request.yml
│   │   └── parser_request.yml    # "I need Cinder to parse X format"
│   └── pull_request_template.md
├── docs/
│   ├── plan.md                   # this file
│   ├── architecture.md
│   ├── contributing.md
│   ├── ai-providers.md
│   ├── sidecar-protocol.md
│   └── adr/                      # architecture decision records
├── src/
│   ├── Cinder.App/               # Avalonia entry point, views, viewmodels
│   ├── Cinder.Core/              # domain model, case management, chain of custody
│   ├── Cinder.Native/            # platform abstraction interface
│   ├── Cinder.Native.Windows/    # Windows-specific impl
│   ├── Cinder.Native.Linux/      # Linux-specific impl
│   ├── Cinder.Sidecar/           # sidecar protocol + worker pool
│   ├── Cinder.Search/            # Tantivy FFI bindings + index management
│   ├── Cinder.AI/                # AI provider plugin interface + adapters
│   ├── Cinder.Reports/           # report templating + export
│   └── Cinder.Hex/               # hex viewer Avalonia control
├── parsers/                      # Python sidecar workers
│   ├── filesystem/               # pytsk3 wrappers
│   ├── registry/                 # regipy wrappers
│   ├── memory/                   # volatility3 wrappers
│   ├── email/                    # libpff wrappers
│   ├── eventlog/                 # python-evtx wrappers
│   ├── timeline/                 # plaso wrappers
│   ├── browser/                  # hindsight + custom parsers
│   ├── pyproject.toml
│   └── shared/                   # shared protocol code
├── drivers/                      # Windows kernel write-blocker (separate sub-project)
│   └── cinder-wb/
├── tests/
│   ├── Cinder.Core.Tests/
│   ├── Cinder.Native.Tests/
│   ├── parsers/                  # pytest for Python sidecars
│   └── integration/              # end-to-end against fixture images
├── fixtures/                     # forensic test images (LFS-tracked)
│   ├── ntfs-basic.dd
│   ├── ext4-basic.dd
│   └── README.md                 # provenance + license per fixture
├── installers/
│   ├── windows/                  # MSIX manifest + WiX fallback
│   ├── linux/
│   │   ├── appimage/
│   │   ├── debian/               # .deb packaging
│   │   └── rpm/                  # .rpm packaging
│   └── flatpak/
├── assets/
│   ├── branding/                 # logo, icons, color tokens
│   └── screenshots/              # for README + website
├── LICENSE                       # Apache 2.0
├── README.md
├── CONTRIBUTING.md
├── CODE_OF_CONDUCT.md            # Contributor Covenant 2.1
├── SECURITY.md                   # responsible disclosure policy
├── ROADMAP.md                    # phase tracker
├── CHANGELOG.md                  # Keep a Changelog format
├── .editorconfig
├── .gitignore
├── .gitattributes                # LFS for fixtures
├── Directory.Build.props         # solution-wide MSBuild config
├── Directory.Packages.props      # central package management
├── global.json                   # SDK pinning
└── Cinder.sln
```

---

## 4. Coding conventions

### C# style

- **Target**: .NET 10, C# 14, nullable reference types **enabled** project-wide
- **Style**: enforce via `.editorconfig` + Roslyn analyzers; CI fails on warnings
- **Async**: every IO method is `async`/`Task`-returning; no `.Result` or `.Wait()` ever
- **Cancellation**: `CancellationToken` parameter on every public async method, no exceptions
- **Records over classes** for DTOs and value objects
- **Source generators** for MVVM (CommunityToolkit) and JSON serialization
- **No `var` for primitives** when type isn't obvious from RHS; otherwise prefer `var`
- **One type per file**, file name matches type name
- **XML doc comments** required on every public API surface

### Python style

- **Black** formatting, **ruff** linting, **mypy** strict mode
- **Type hints** required on every function signature
- **Pure functions** preferred — sidecar workers are stateless request/response
- **Pydantic v2** models for all sidecar protocol messages

### Commits & branching

- **Branching**: `main` (always shippable) + `dev` (integration); feature branches off `dev`
- **Commits**: [Conventional Commits](https://www.conventionalcommits.org/) — `feat:`, `fix:`, `chore:`, `docs:`, `refactor:`, `test:`, `perf:`
- **PR titles** follow conventional commit format
- **Squash merge** to main; merge commits to dev

### Versioning

- **SemVer 2.0.0** — MAJOR.MINOR.PATCH
- **Changelog**: [Keep a Changelog](https://keepachangelog.com/) format
- **Pre-1.0**: minor bumps may break things (clearly noted)

### Testing minimums

- Every parser has fixture-based regression tests with expected JSON output
- Every public domain method has a unit test
- Coverage minimum: 70% line coverage on `Cinder.Core` (relax for UI projects)
- Integration tests run against real test images on every commit

---

## 5. Design system

### Brand

- **Name**: Cinder
- **Logo**: stylized ember/spark glyph, geometric vector, single-color works
- **Wordmark**: "Cinder" in a modern geometric sans (Geist / General Sans)

### Color palette

```css
/* Dark theme (default) */
--bg-0:           #0E0F12;  /* deepest background */
--bg-1:           #16181D;  /* surface */
--bg-2:           #1E2128;  /* surface raised (panels, dialogs) */
--border:         #2A2D35;
--border-strong:  #3A3D47;

--fg:             #ECEDEE;
--fg-muted:       #9CA0AB;
--fg-disabled:    #5A5E6A;

/* Brand */
--accent:         #FF7A1A;  /* cinder-orange, primary brand */
--accent-hover:   #FF8E3D;
--accent-dim:     #B4540F;

/* Semantic */
--ok:             #3DD68C;  /* hash verified, signed */
--warn:           #FFB347;  /* mismatch warning */
--danger:         #E5484D;  /* tampered, hash failure */
--info:           #5EAEFF;
```

Light theme mirrors with `#FAFAFA` background. Color choices are tested for **WCAG AAA contrast** and **color-blind safe** distinguishability.

### Typography

- **UI sans**: Inter Variable (fallback: system-ui)
- **Mono**: JetBrains Mono Variable (fallback: monospace)
- **Type scale**: 11 / 13 / 15 / 18 / 24 / 32 px
- **Line height**: 1.4–1.5
- **Default weight**: 450; headers 600; labels 500

### Layout (IDE pattern)

```
┌─ Title bar (Mica/transparent, draggable) ──────────────────────────┐
│ Cinder · Case: Acme-2026-04 · 🛡 WriteBlock ON · ✓ Verified  ⌘K   │
├──────┬──────────────────────────────────────────────┬──────────────┤
│      │ ┌─ Tabs ─────────────────────────────────┐  │              │
│ Case │ │ Hex │ Gallery │ Timeline │ Reports     │  │ Properties   │
│ Tree │ ├────────────────────────────────────────┤  │ + Notes      │
│      │ │                                        │  │ + Tags       │
│      │ │     Active analyzer pane               │  │ + Bookmarks  │
│      │ │                                        │  │              │
│      │ └────────────────────────────────────────┘  │              │
├──────┴──────────────────────────────────────────────┴──────────────┤
│ Activity log / chain-of-custody scroll  •  Background tasks: 3    │
└────────────────────────────────────────────────────────────────────┘
```

### Motion

- **Panel transitions**: 180ms ease-out
- **List loads**: stagger-fade 30ms per row, max 600ms total
- **Hover states**: 100ms tint shift
- **No bouncy / overshoot** — Cinder is a serious tool

### Density modes

- **Comfortable** (default) — modern crowd
- **Compact** — X-Ways crowd; max info density

### Accessibility

- Full keyboard nav, no mouse-required actions
- Screen reader labels on every interactive control
- Focus indicators with 2px accent outline
- High-contrast theme variant
- Respects OS animation-reduction setting

---

## 6. AI plugin architecture (BYOM)

### Principle

Cinder ships with **zero bundled models**. Users plug in their own AI backend. This keeps Cinder small, license-clean, and respects the forensics norm of evidence never leaving the workstation by default.

### Provider interface

```csharp
namespace Cinder.AI;

public interface IAiProvider {
    string Id { get; }                   // "ollama", "openai-compatible", etc.
    string DisplayName { get; }
    AiProviderCapabilities Capabilities { get; }

    Task<bool> HealthCheckAsync(CancellationToken ct);
    IAsyncEnumerable<string> StreamCompletionAsync(
        AiPrompt prompt,
        CancellationToken ct);
    Task<string> CompleteAsync(AiPrompt prompt, CancellationToken ct);
}

public record AiProviderCapabilities(
    bool SupportsStreaming,
    bool SupportsToolCalling,
    bool SupportsVision,
    int MaxContextTokens,
    bool LocalOnly);

public record AiPrompt(
    string SystemMessage,
    IReadOnlyList<AiMessage> Messages,
    AiPromptOptions Options);
```

### Built-in adapters

| Adapter | Auto-detect | Use case |
|---|---|---|
| **Ollama** | `localhost:11434` | Most popular local runner. Auto-lists models. |
| **LM Studio** | `localhost:1234` | GUI-driven local inference. |
| **llama.cpp server** | user URL | Direct llama.cpp deployments. |
| **OpenAI-compatible** | user URL + key | Catches vLLM, TGI, KoboldCpp, future Astryx — anything speaking the OpenAI `/v1/chat/completions` schema. |
| **OpenAI / Anthropic / Gemini** | user API key | Cloud option. **Big red warning**: evidence text leaves the device. Off by default. |
| **Disabled** | — | Default. AI panel says "Configure in Settings." |

### Context construction

The AI never sees raw evidence bytes. The Cinder core builds **structured prompts** from parsed artifacts:

```csharp
// User asks: "Summarize this user's activity in the past week."
var artifacts = await caseStore.QueryAsync(new TimelineQuery {
    UserSid = currentUser.Sid,
    Range = DateRange.LastDays(7),
    Sources = TimelineSource.All
});

var prompt = new AiPrompt(
    SystemMessage: "You are a forensic analyst assistant. Answer based ONLY on the provided artifacts.",
    Messages: [
        new("user", $"""
            Question: Summarize this user's activity in the past week.
            Artifacts (JSON): {JsonSerializer.Serialize(artifacts.Summarize())}
            """)
    ],
    Options: new(MaxTokens: 1500, Temperature: 0.2));
```

This pattern keeps prompts small, model-agnostic, and means even a 7B local model handles structured questions well.

### AI feature surface (v1)

- "What's anomalous about this process tree?" → feed parsed Volatility output
- "Explain this registry key in plain English" → feed key + value
- "Summarize user's activity for time range X" → feed timeline JSON
- Natural language search → AI translates to structured query, Cinder executes
- "Generate a draft case summary for this report" → feed bookmarks + findings

### Astryx integration path

When you eventually run Astryx on the 4090 (or 6090), it speaks OpenAI-compatible → user picks "OpenAI-compatible" adapter, points at Astryx URL → done. **Zero Cinder code changes.**

---

## 7. Distribution & signing

### Windows

- **Code signing**: SignPath.io (free for OSS, established sponsor of Windows OSS projects)
- **Primary distribution**: GitHub Releases (signed `.msix` + signed `.exe` portable)
- **Package managers**: WinGet manifest (official Microsoft), Scoop bucket (community)
- **Skip**: Microsoft Store (sandbox breaks raw disk access)
- **Auto-update**: Velopack (modern Squirrel successor, supports MSIX)

### Linux

- **Primary**: GitHub Releases — AppImage (single-file, runs anywhere), `.deb`, `.rpm`
- **Secondary**: Flatpak/Flathub long-term (sandboxing complicates raw disk access — gated behind `--filesystem=host` permission)
- **Tertiary**: AUR package for Arch (community-maintained)
- **Signing**: GPG-sign release tarballs and AppImages, publish key fingerprint in repo
- **Future**: pre-bundled in SIFT, Tsurugi, Kali repos once mature

### Permissions

- **Windows**: needs Administrator for raw disk reads. Manifest declares this; Cinder degrades gracefully if not elevated (read-only access to mounted volumes still works).
- **Linux**: needs `CAP_SYS_RAWIO` + `CAP_SYS_ADMIN` for raw disk access. Three patterns documented:
  1. `sudo cinder` (simplest)
  2. `setcap` on the binary (one-time setup, then unprivileged launch)
  3. Polkit rule for proper desktop integration

### Auto-update

- Settings → "Check for updates" toggle (default: on for stable, off for nightly)
- Update check: `GET https://api.github.com/repos/AffluentMods/cinder/releases/latest` — 100% public API, no Cinder backend service
- Updates are user-confirmed, never silent

---

## 8. Full feature inventory (cross-platform notes)

Each feature tagged: 🪟 Windows | 🐧 Linux | 🌐 Both

### 8.1 Core viewers & editors

1. **Hex viewer / editor** 🌐 — variable byte-width, ASCII sidebar, structure overlay, bookmarks, find-by-hex/regex/text/UTF-16, Goto offset, multi-cursor edit
2. **Disk editor** 🌐 — interpret raw disk regions: MBR, GPT, boot sectors, FAT tables, MFT records, ext4 superblocks, APFS containers
3. **Text viewer** 🌐 — multi-encoding, mmap-backed for huge files
4. **Image gallery** 🌐 — thumbnails, EXIF overlay, GPS map, local face/object grouping
5. **Video preview** 🌐 — keyframe scrubber, metadata, frame export
6. **Document preview** 🌐 — PDF, DOCX, XLSX, RTF, ODF
7. **SQLite browser** 🌐 — query support, freelist parser for deleted rows
8. **Plist viewer** 🌐 — for iOS/macOS artifacts encountered in cases
9. **Structured-data viewer** 🌐 — JSON, XML, ASN.1, ProtoBuf

### 8.2 Acquisition & imaging

10. **Disk imager** 🌐 — E01, EX01, AFF4, AFF4-L, raw .dd, VHD/VHDX
11. **Logical imager** 🌐 — selective file-tree imaging into evidence containers
12. **Live RAM acquisition** 🪟🐧 — Windows: signed kernel driver. Linux: `/dev/mem` + LiME kernel module support.
13. **Software write-blocker** 🪟🐧 — Windows: kernel filter driver (long-term). Linux: `blockdev --setro` + `dm-readonly` device mapper. Hardware blocker detection on both.
14. **Image verification** 🌐 — re-hash and bit-compare any image, hash chain in case DB
15. **Image format conversion** 🌐 — E01 ↔ raw ↔ VHD ↔ AFF4, hash-preserving
16. **Image mounting** 🌐 — read-only, multi-partition. Windows: arsenal-style virtual disk. Linux: loop devices + libfuse
17. **Remote acquisition** 🌐 — agent-based, encrypted/signed channel
18. **Cloud acquisition** 🌐 — OAuth/PKCE flows for OneDrive, Google Drive, Dropbox, iCloud (no client secret needed)

### 8.3 Filesystem parsing (via pytsk3 sidecar — works everywhere)

19. **NTFS** 🌐 — full $MFT, $LogFile, $UsnJrnl/$J, $Secure, ADS, EFS detection
20. **FAT12 / FAT16 / FAT32 / exFAT** 🌐
21. **ext2/3/4** 🌐 — inodes, extents, journal replay
22. **APFS** 🌐 — read-only, container/volume/snapshot enumeration
23. **HFS+** 🌐
24. **ReFS** 🌐 — Microsoft's resilient FS
25. **UDF / ISO9660 / Joliet / Rock Ridge** 🌐 — optical media
26. **F2FS, XFS, Btrfs, ZFS, ReiserFS, Squashfs** 🌐
27. **VHD/VHDX, VMDK, VDI, QCOW2** 🌐 — virtual disk formats
28. **LVM, LUKS, BitLocker, FileVault** 🌐 — volume manager + crypto with key/passphrase
29. **RAID reconstruction** 🌐 — JBOD, RAID 0/1/5/6, mdadm, dynamic disks, LVM2

### 8.4 Carving & recovery

30. **File carver** 🌐 — header/footer + fragment-aware, 200+ default signatures
31. **Slack-space carver** 🌐
32. **Unallocated-space carver** 🌐 — multi-threaded, runs concurrent with ingest
33. **Smart carving** 🌐 — validates carved files actually open
34. **Deleted file recovery** 🌐 — directory-aware
35. **Volume Shadow Copy / snapshot enumeration** 🪟🐧 — Windows: VSS. Linux: btrfs/LVM/ZFS snapshots.

### 8.5 Windows artifacts 🪟 (most also useful when imaging Win drives from Linux)

36. **Registry hive parser** — NTUSER.DAT, UsrClass.dat, SYSTEM, SOFTWARE, SAM, SECURITY, BCD, Amcache.hve, transaction log replay
37. **Prefetch (.pf)**
38. **Shellbags**
39. **Jumplists** — automaticDestinations + customDestinations
40. **LNK files**
41. **Event Log (.evtx)** — rule-based highlighting
42. **Windows Timeline / ActivitiesCache**
43. **SRUM**
44. **BAM/DAM**
45. **UserAssist, MUICache, RecentDocs, RunMRU, TypedURLs, WordWheelQuery**
46. **USB device history** — USBSTOR, MountedDevices, Setup API logs
47. **Wi-Fi history**
48. **RDP cache (BMC)**
49. **Thumbcache**
50. **Page file / hibernation file**

### 8.6 Linux artifacts 🐧 (also useful when imaging Linux drives from Windows)

51. **Shell history** — `.bash_history`, `.zsh_history`, `.fish_history`
52. **Auth log / journalctl** — `/var/log/auth.log`, systemd journal
53. **Syslog** — `/var/log/syslog`, `/var/log/messages`
54. **Cron / at jobs** — `/etc/crontab`, user crontabs, `at` queue
55. **User config** — `/etc/passwd`, `/etc/shadow`, `/etc/group`
56. **SSH artifacts** — `~/.ssh/known_hosts`, `authorized_keys`, host keys
57. **Recently used files** — `.local/share/recently-used.xbel`
58. **Trash** — `~/.local/share/Trash` with original-path metadata
59. **Systemd unit files** — installed, enabled, masked services
60. **Package manager logs** — apt history, dnf log, pacman log

### 8.7 Application artifacts

61. **Browser history** — Chrome, Edge, Firefox, Brave, Opera, Safari, Tor (cache state)
62. **Email parsers** — PST, OST, MBOX, EML, Apple Mail
63. **Chat artifacts** — Discord cache, Slack desktop logs, Teams, Signal/Wickr metadata, WhatsApp Desktop, Telegram Desktop
64. **Cloud sync logs** — OneDrive, Dropbox, Google Drive, Box client databases
65. **Crypto wallet detection** — wallet.dat, MetaMask, Electrum
66. **Steam / gaming** — installed, playtime, screenshot cache
67. **VPN client artifacts**

### 8.8 Memory forensics (volatility3 sidecar)

68. **Process tree** — visual tree, anomaly flags
69. **Network connections** — netscan + geo-IP overlay
70. **Loaded DLLs / SOs** — per process, signing/hash status
71. **Code injection detection** — malfind, hollowfind
72. **Credential extraction** — hashdump, lsadump, cachedump
73. **Registry-from-RAM**
74. **Plugin runner** — spawn arbitrary `vol3` plugins from GUI

### 8.9 Network forensics

75. **PCAP analysis** — TCP reassembly, HTTP/2/3, TLS metadata, JA3/JA4
76. **NetFlow / Zeek log import**
77. **DNS query reconstruction**

### 8.10 Search, hashing, matching

78. **Multi-encoding string search** — UTF-8/16/32, code pages, Unicode normalization
79. **Regex search** — including binary regex
80. **Hash calculation** — MD5, SHA-1, SHA-256, SHA-512, BLAKE3, ssdeep, TLSH, sdhash
81. **Hash sets** — NSRL, ProjectVic, custom whitelist/blocklist
82. **YARA scanning** — files, RAM, unallocated, with rule manager
83. **VirusTotal lookup** — opt-in, hash-only, user-supplied API key (never bundled)
84. **Fuzzy file matching** — find altered duplicates
85. **PhotoDNA** — gated to LE-licensed users only

### 8.11 Timeline & visualization

86. **Super-timeline** — every artifact with a timestamp on one navigable axis
87. **Timeline filtering** — by user, source, range, MITRE ATT&CK technique
88. **GPS map view** — EXIF + Wi-Fi + browser geo
89. **Communication graph** — force-directed, who-talked-to-whom
90. **File-relationship graph** — what referenced what

### 8.12 Anti-forensics detection

91. **Wipe-tool detection** — CCleaner, BleachBit, Eraser, sdelete, shred, wipe
92. **Timestomp detection** — $STANDARD_INFORMATION vs $FILE_NAME mismatch
93. **Hidden volume detection** — VeraCrypt/TrueCrypt entropy analysis
94. **Steganography indicators** — entropy + metadata anomaly flags

### 8.13 Reporting

95. **Live report builder** — every bookmark flows into a draft as you work
96. **Templates** — court-ready, IR, internal audit
97. **Export formats** — PDF/A, DOCX, HTML (searchable evidence index), JSON, Markdown
98. **Exhibit numbering** — automatic, with hash + examiner + timestamp on every page
99. **Repeatability scripts** — every report exports a JSON "playbook" another investigator can re-run

### 8.14 Case management & collaboration

100. **Multi-case workspace**
101. **Multi-examiner support** — Git-style branches, surfaced merge conflicts
102. **Tagging / bookmarking / notes** — per file, per offset, per artifact, all searchable
103. **Chain of custody** — append-only hash-chained log, tamper-evident
104. **Encrypted case storage** — AES-256-GCM at rest; key in DPAPI 🪟 / libsecret 🐧 / hardware token
105. **Case sharing** — encrypted bundle with separate free Evidence Reader mode

### 8.15 Automation & extensibility

106. **Python scripting host** — embedded Python with case API
107. **C# / .NET plugin SDK** — for performance-critical extensions
108. **Workflow builder** — visual node graph
109. **Watch folders** — drop image in, get triage report out
110. **CLI mode** — every GUI action mapped to a CLI command

### 8.16 AI assist (BYOM — see §6)

111. **Local LLM "case copilot"** — pluggable, no bundled model
112. **Anomaly highlighting** — local ML model flags statistical outliers
113. **Natural language queries**
114. **Auto-generated case summaries**
115. **Strict offline mode** — default; AI never phones home unless explicitly enabled

### 8.17 QoL (the things existing tools get wrong)

- Command palette (Ctrl+K) for everything
- Multi-monitor pop-out panels
- Browser-style tabs, not windows
- Saved layouts per case type
- Keyboard-first nav, optional vim mode in hex view
- Instant launch — no splash screen
- Searchable preferences
- Honest progress bars with real ETAs
- Always-cancellable operations
- Crash recovery — case auto-saved on every action

---

## 9. Phased roadmap

### Phase 0 — Foundation (4 weeks)

**Goal**: skeleton you can demo to a contributor in 5 minutes.

**Deliverables**
- `Cinder.sln` with all project skeletons created
- Avalonia 11 shell with three-pane layout, FluentAvalonia theme, dark/light toggle
- Cinder color palette + typography wired into theme resources
- Command palette (⌘K) with empty action registry
- SQLite case schema + migrations (EF Core)
- Append-only hash-chained chain-of-custody log
- Sidecar protocol contract (JSON-RPC over stdio) with first echo-worker in Python
- Hash service: streaming MD5/SHA-1/SHA-256/BLAKE3
- Serilog structured logging
- Crash handler that writes a local crash bundle (no upload)
- App icon, branding assets in `assets/branding/`
- GitHub Actions: build + test on Windows + Linux runners
- README with screenshots, install instructions, contributing CTA
- LICENSE (Apache 2.0), CODE_OF_CONDUCT, CONTRIBUTING, SECURITY

**Acceptance**
- `dotnet run` opens the shell on Windows + Linux
- Creating a case writes a row to SQLite with hash-chained custody entry
- Echo sidecar round-trips a JSON-RPC call from C# in < 50ms
- CI green on PR

### Phase 1 — Hex viewer & hashing (4 weeks)

**Goal**: ship Cinder v0.1 as a *standalone* hex editor with forensic flavor. Build credibility before tackling imaging.

**Deliverables**
- Custom Avalonia hex viewer control: virtualized rendering, mmap-backed reads
- Variable byte-width (8/16/32 bytes per row), ASCII + UTF-16 sidebars
- Structure overlay framework (color regions by interpretation — even if no parsers wired yet)
- Find: hex pattern, regex, text (multi-encoding), UTF-16, with highlight
- Goto offset (decimal/hex), bookmark + jump-back
- Multi-cursor edit + undo/redo
- File header analyzer with magic-number library (libmagic-equivalent)
- Hash dialog: drag any file → instant streaming MD5/SHA-1/SHA-256/BLAKE3
- Chain-of-custody integration: every hash auto-logged

**Acceptance**
- Open 100GB raw image without OOM, scroll smoothly to any offset
- Hash a 10GB file in < 30s on commodity SSD
- Find regex pattern in a 1GB file in < 5s
- Extension/magic-number mismatch detected and flagged in UI

**Ship**: tag `v0.1.0`, GitHub Release, WinGet + AUR submission, "Show HN" / r/computerforensics post

### Phase 2 — Imaging & verification (6 weeks)

**Deliverables**
- Disk imager (libewf-python sidecar): E01, EX01, raw .dd, AFF4 output
- Multi-pass with bad-sector handling, parallel chunked hashing
- Image verifier: re-hash and bit-compare
- Read-only image mounter: Windows arsenal-style virtual disk; Linux loop+FUSE
- VSS / btrfs-snapshot enumeration
- Image format conversion E01 ↔ raw ↔ VHD with hash preservation
- Software write-blocker: Windows kernel filter driver (signed via SignPath); Linux `blockdev --setro` + `dm-readonly` wrapper
- Hardware write-blocker detection (Tableau, WiebeTech) shown in status bar

**Acceptance**
- Image a 500GB drive at full read speed (no overhead vs `dd`)
- Mount E01 produces faithful virtual disk (fix the FTK Imager 32MB EFI bug)
- Write-blocker on prevents all writes (validated with negative test)

### Phase 3 — Filesystem + carving (8 weeks)

**Deliverables**
- pytsk3 sidecar wrapping NTFS, FAT, ext4 (Phase 3a); APFS, HFS+ (3b)
- $MFT parser with $LogFile + $J/$UsnJrnl
- File carver with smart validation (carved files must actually open)
- Slack + unallocated carving, multi-threaded
- Deleted file recovery, directory-aware
- File browser tree view with filter/sort/timeline columns

**Acceptance**
- NTFS test image: parity with MFTECmd output (regression-tested)
- ext4 test image: parity with `debugfs` recovery
- Carve 1M JPEG headers from 100GB unallocated in < 10 min

### Phase 4 — Windows artifacts (10 weeks)

**Deliverables**
- regipy sidecar: full registry hive parsing, transaction log replay
- Prefetch, Shellbags, Jumplists, LNK parsers
- python-evtx sidecar for .evtx with rule-based highlighting
- Browser history (Chrome/Edge/Firefox via direct SQLite parsing — no sidecar needed)
- USB history, Wi-Fi history, SRUM, Amcache, ShimCache parsers
- Per-artifact viewer with sort/filter/search
- "User activity" rollup — single view of LNK + Jumplists + RecentDocs + browser by user

**Acceptance**
- Parity with Eric Zimmerman tools on every parser (CSV diff against ZTools output)
- All parsers complete on a typical Win10 image in < 10 min total

### Phase 5 — Linux artifacts (4 weeks)

**Deliverables**
- Shell history, auth log, journalctl, cron parsers
- SSH known_hosts, authorized_keys analysis
- Trash + recently-used parsers
- Package manager log parsers (apt, dnf, pacman)
- systemd unit enumeration

**Acceptance**
- Parses cleanly on SIFT, Tsurugi, Kali images
- "User activity" rollup includes Linux artifacts when present

### Phase 6 — Search, indexing, timeline (8 weeks)

**Deliverables**
- Tantivy FFI bindings, full-text indexing (background, non-blocking)
- Multi-encoding string search across whole case
- Hash set matching: NSRL bulk import (RDS modern minimal)
- YARA scanning with rule manager
- Super-timeline: every parsed artifact on one navigable visual axis
- Timeline filtering by user/source/range/MITRE technique
- GPS map view (MapsUI + offline OSM tiles)

**Acceptance**
- Index a 100K-file case in < 5 min
- Sub-second full-text query against indexed case
- Timeline renders 1M events smoothly with virtualized scrolling

### Phase 7 — Memory forensics (6 weeks)

**Deliverables**
- volatility3 sidecar with all standard plugins
- Process tree visualization with anomaly flags (parent mismatch, unsigned in system path, hollowed)
- Network connections with geo-IP
- DLL/SO listing per process
- malfind / hollowfind UI
- Plugin runner for arbitrary vol3 plugins
- RAM acquisition: Windows kernel driver; Linux LiME integration

**Acceptance**
- Volatility CTF challenge image: solve from GUI in < 1 hour with no CLI

### Phase 8 — Reporting & case management (8 weeks)

**Deliverables**
- Report builder: bookmarks → draft, templates (expert witness, IR, internal audit)
- Export: PDF/A, DOCX, HTML with searchable evidence index, Markdown, JSON playbook
- Auto exhibit numbering with hash/examiner/timestamp watermark
- Multi-case workspace
- Multi-examiner Git-style branching
- Encrypted case bundles + free Evidence Reader mode

**Acceptance**
- Generated report passes typical court-template checklist
- Two examiners on same case branch, then merge with conflict resolution

### Phase 9 — AI copilot (year 2)

**Deliverables**
- BYOM provider system per §6
- Built-in adapters: Ollama, LM Studio, OpenAI-compatible
- Context construction from parsed artifacts
- Anomaly highlighting (local ONNX model)
- Natural language → structured query translation
- Auto-summary draft for reports

### Phase 10 — Network, mobile, cloud (year 2+)

PCAP analyzer, Android/iOS backup parsing, cloud connector kit, eventually macOS support.

---

## 10. Validation strategy

Every parser validated against known-good fixtures **before** merge. Sources:

- **NIST CFReDS** — Computer Forensic Reference Data Sets
- **DFIR.training** image library
- **CFTT** test images
- **Brian Carrier's tsk test suite**
- **AboutDFIR** challenge images
- **TINFO 443** course images

For each parser:
- Fixture in `tests/fixtures/` (LFS-tracked)
- Expected JSON output committed alongside fixture
- Regression test diffs actual vs expected on every CI run
- No parser ships without parity test vs. an existing reference tool (ZTools, Autopsy, X-Ways trial)

---

## 11. Contributor experience

### README must include

- Animated demo gif at the top (the hex viewer in action looks great)
- Install commands for both platforms
- Quick-start: open a sample image in 60s
- Architecture diagram
- Link to CONTRIBUTING.md
- Sponsors block (GitHub Sponsors)
- Star history chart

### CONTRIBUTING.md must include

- Local dev setup (one command per platform)
- Running tests
- Conventional Commits guide
- PR review SLA: response within 7 days
- "Good first issues" labeled in tracker
- Roadmap link

### Issue templates

- Bug report — with image format / OS / version fields
- Feature request — with use case prompt
- Parser request — "I need Cinder to parse format X" with sample data field

### SECURITY.md

- Use GitHub Security Advisories for private disclosure
- 90-day disclosure timeline
- No bug bounty (it's a free OSS tool) but credit in CHANGELOG + Hall of Fame

### Donations

- GitHub Sponsors button
- Open Collective (transparent finances)
- Ko-fi for one-off tips
- **Never** in-app prompts. Link only from About dialog and README.

---

## 12. CI/CD

### `.github/workflows/ci.yml`

- Triggers: PR to `dev` or `main`
- Matrix: Windows + Ubuntu, .NET 10, Python 3.12
- Steps: restore → build → test (xUnit + pytest) → coverage → SonarCloud scan

### `.github/workflows/release.yml`

- Trigger: tag matching `v*.*.*`
- Steps:
  1. Build all artifacts on matrix runners
  2. Sign Windows MSIX via SignPath GitHub Action
  3. GPG-sign Linux AppImage / .deb / .rpm
  4. Generate SBOM (SPDX format)
  5. Upload all artifacts to GitHub Release with auto-generated changelog
  6. Trigger WinGet manifest update via PR
  7. Trigger AUR push (community-maintained, optional)

### `.github/workflows/codeql.yml`

- Weekly + on push to main
- C# + Python analysis
- Findings posted to Security tab

---

## 13. Open questions / decisions to revisit

1. **Logo**: confirm direction once Gemini/Ideogram outputs land
2. **Phase 1 scope ship**: hex viewer alone, or hex+hash+signature bundled?
3. **Tantivy vs Lucene.NET**: benchmark before locking
4. **Embedded Python distribution size**: target < 200MB total installer; if Python venv blows that, evaluate IronPython 3 or Python.NET
5. **Driver signing strategy**: WHQL is expensive — is SignPath enough for kernel driver, or wait for WHQL until Phase 2 ships?
6. **Documentation site**: DocFX vs mkdocs-material — pick before Phase 1 ends
7. **Discord vs Discussions**: where does the community live? Lean toward GitHub Discussions for everything indexable, Discord for real-time later
8. **Trademark**: file USPTO trademark on "Cinder" + Cinder logo before v0.1.0 release
9. **Domain**: secure `cinder.dev` and `cinderforensics.com` early

---

## 14. Glossary (for new contributors)

- **DFIR** — Digital Forensics and Incident Response
- **Artifact** — any digital evidence (a file, a registry key, a log entry, a process in RAM)
- **Image** — bit-stream copy of a storage device (`.dd`, `.E01`, `.AFF4`)
- **Hash chain** — sequence of cryptographic hashes that prove tamper-evidence
- **Chain of custody** — auditable record of every action taken on evidence
- **MFT** — Master File Table, NTFS's index of every file
- **VSS** — Volume Shadow Copy Service, Windows's snapshot mechanism
- **Carving** — recovering files from raw bytes without filesystem metadata
- **Slack space** — unused bytes between a file's actual end and the cluster end
- **Unallocated** — disk space not currently assigned to any file
- **NSRL** — NIST National Software Reference Library, hash sets of known software
- **Sidecar** — separate worker process Cinder spawns for specific tasks (e.g., the Python parsing workers)

---

*Plan version: v0.2*
*Last updated: 2026-05-07*
*Next review: after Phase 0 ships*
