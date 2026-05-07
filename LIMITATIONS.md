# LIMITATIONS

Things that exist as code in this repo but **do not work end-to-end without external work
that cannot happen inside an AI coding session**. Each entry has a stable anchor so other docs
can link to it.

---

## <a id="write-blocker-windows"></a> Windows software write-blocker

- **Where:** [`drivers/cinder-wb-windows/`](drivers/cinder-wb-windows/)
- **State:** C / WDK source written; produces no `.sys` from a normal `dotnet build`.
- **Blockers:**
  - Microsoft Windows Driver Kit (WDK 10+) installed on a Windows build agent
  - Code-signing certificate (EV) cross-signed by a Microsoft-trusted CA
  - Microsoft attestation signing portal account (free) OR full WHQL submission ($$ + time)
  - HVCI compatibility audit
- **Workaround until then:** `WindowsWriteBlocker.TryEngage()` returns `false`. UI prompts the
  user to use a hardware write-blocker (Tableau, WiebeTech) instead — Cinder detects those by
  vendor/product ID and badges the status bar accordingly.

## <a id="ram-acquisition-windows"></a> Windows RAM acquisition driver

- **Where:** [`drivers/cinder-ram-windows/`](drivers/cinder-ram-windows/)
- **State:** README-only (interface, layout, build plan).
- **Blockers:** identical to the write-blocker — WDK + signing infra.
- **Workaround:** Cinder shells out to `winpmem.exe` (Velocidex) when present on PATH. If
  it isn't, the imager UI surfaces an actionable error pointing users at the winpmem download.

## <a id="ram-acquisition-linux"></a> Linux RAM acquisition

- **Where:** [`drivers/cinder-ram-linux/`](drivers/cinder-ram-linux/)
- **State:** README-only. Cinder does NOT author its own kernel module — uses LiME upstream.
- **Blockers:**
  - Pre-built LiME `.ko` for the user's kernel (kernel modules are not portable across versions)
- **Workaround:** Cinder detects `uname -r`, looks for `lime-<kver>.ko` next to the binary,
  prompts the user to build LiME if absent. CI step in `release.yml` should pre-build for
  popular kernels (Ubuntu LTS, Debian stable, Arch -lts).

## <a id="windows-e01-mount"></a> Windows E01 image mounting

- **Where:** [`Cinder.Imaging.WindowsImageMounter`](src/Cinder.Imaging/IImageMounter.cs)
- **State:** VHD/VHDX/ISO mounting works via `Mount-DiskImage`. E01 / AFF4 throw
  `NotSupportedException`.
- **Blockers:** Microsoft Windows ships no native E01 mounter. Options:
  1. Wrap [Arsenal Image Mounter](https://arsenalrecon.com/products/arsenal-image-mounter)
     CLI when installed — license is friendly to wrappers but requires the user-mode binary
  2. Build a userland virtual-disk shim using the libewf Windows DLL + a custom storage
     port (significant project of its own; not started)
- **Workaround:** the imager UI prompts the user to install Arsenal Image Mounter the first
  time they try to mount an E01 on Windows.

## <a id="tantivy-ffi"></a> Tantivy FFI

- **Where:** [`Cinder.Search`](src/Cinder.Search/)
- **State:** Cinder uses Lucene.NET 4.8.0-beta instead of Tantivy. Per `docs/plan.md` §13.3,
  the choice was open; this session locked it to Lucene.NET because Tantivy would have required:
  - A separate Rust crate authored from scratch
  - cdylib builds for win-x64 and linux-x64 shipped as NuGet runtime assets
  - A `cbindgen`-generated C header and matching C# P/Invoke layer
  - Roughly equivalent feature surface but in roughly one more engineer-month
- **Re-visit:** if benchmarks show Lucene.NET indexing falls behind on 1M+ artifact cases.

## <a id="cloud-oauth-clients"></a> Cloud connector OAuth client IDs

- **Where:** [`Cinder.Cloud.GoogleDriveConnector`](src/Cinder.Cloud/GoogleDriveConnector.cs),
  [`OneDriveConnector`](src/Cinder.Cloud/OneDriveConnector.cs),
  [`DropboxConnector`](src/Cinder.Cloud/DropboxConnector.cs)
- **State:** code is complete; all three need a registered OAuth application with a loopback
  redirect URI.
- **Blockers:**
  - Google Cloud Console: register a Desktop OAuth client → get `client_id`
  - Microsoft Entra ID: register an App Registration → public-client redirect → `client_id`
  - Dropbox App Console: register an app → "PKCE only" redirect → `client_id`
- **Workaround:** `ClientId` properties are init-only and read from `SettingsStore.CloudClientIds`.
  Distributors can ship Cinder with their own client IDs filled in, OR the user enters them
  in Settings on first cloud-connect.

## <a id="parser-libraries"></a> Python parser libraries

The sidecars under [`parsers/`](parsers/) wrap real libraries. Cinder ships no library code
itself for filesystems, registries, EVTX, mobile backups, or memory dumps. The bundled
installer (planned for `release.yml`) creates a per-user venv with these installed:

| Sidecar | Required pip packages |
|---|---|
| `parsers/imager` | `libewf-python`, optionally `pyaff4` |
| `parsers/filesystem` | `pytsk3`, `libewf-python` |
| `parsers/windows` | `regipy`, `python-evtx`, `pylnk3`, `libesedb-python` |
| `parsers/linux` | (none — pure-Python; `journalctl` binary on PATH for systemd journals) |
| `parsers/memory` | `volatility3` |
| `parsers/network` | `dpkt`, `scapy` |
| `parsers/mobile` | `iphone-backup-decrypt` (iOS), no extra deps for Android |

Until the bundled venv ships, users see a clean `RuntimeError` when triggering a sidecar that
needs a missing lib. The C# layer translates these to actionable UI prompts.

## <a id="parser-validation"></a> Parser validation against fixtures

`docs/plan.md` §10 requires every parser to have a parity test against a reference tool. None
of those tests exist yet because the fixture corpora (NIST CFReDS, Brian Carrier tsk suite,
AboutDFIR images) are gigabytes of binary data not in the repo.

This means **Cinder's parsers compile and run, but their correctness against real evidence is
unverified**. Treat results from a parser-driven Phase 4–7 view as preliminary until the
fixture-vs-reference diff is in CI.

## <a id="docx-pdfa"></a> DOCX export and PDF/A export

- **Where:** [`Cinder.Reports.ReportExporter`](src/Cinder.Reports/ReportExporter.cs)
- **State:**
  - PDF: works when `wkhtmltopdf`, `chrome`, `msedge`, or `chromium` is on PATH; otherwise
    surfaces a clear error and writes the HTML next to the requested PDF.
  - DOCX: writes Markdown with a `.md` suffix — full DOCX via DocumentFormat.OpenXml lands
    in 8.1.
- **Blockers:** none for PDF; for DOCX, a DocumentFormat.OpenXml dependency in
  `Directory.Packages.props`.

## <a id="trademark-domains"></a> Trademark and domains

Per plan §13.8 — file USPTO trademark on "Cinder" + Cinder logo before v0.1.0. Per §13.9 —
secure `cinder.dev` and `cinderforensics.com`. Both are out-of-scope for code automation.

## <a id="signpath-account"></a> SignPath.io account

The plan repeatedly references SignPath.io for OSS Windows code signing. The OSS tier is
free but requires a manual application and approval from SignPath. Until that's set up, no
Windows artifact (App, Reader, drivers) is signed.
