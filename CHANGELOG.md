# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Correctness and evidence-integrity pass, followed by the features a competitive
gap analysis showed every established tool has and Cinder lacked.

### Added — imaging, journal, custody anchor

- **Raw imaging in-process.** `RawImager` reads a file, a block device
  (`\\.\PhysicalDriveN`, `/dev/sdX`) or an E01 chain decoded on the fly, hashes
  as it streams (MD5 / SHA-1 / SHA-256), writes a flat `.dd`, and on a read error
  retries then drops to sector granularity so one bad sector costs 512 zero bytes,
  counted and listed with offsets in `<image>.log.json`. `<image>.sha256` is in
  `sha256sum` format so the Verify tool reads it. The Imager tool uses it for Raw
  output; EWF/AFF4 output still routes to the sidecar. Logged to custody as
  `evidence.imaged`.
- **E01 → raw conversion.** `ImageConverter.EwfToRawAsync` decodes every chunk,
  hashes, writes flat, and compares the result with the container's recorded
  digests — the conversion doubles as a verification and says so (match /
  mismatch / damaged chunks / no recorded hash). The Convert tool is real now.
- **`$UsnJrnl:$J` parser and tool.** `UsnJournal` reads USN_RECORD_V2 and V3 from
  an NTFS volume in any image (per partition) or an extracted `$J`, resynchronises
  on page padding and garbage, and renders reason flags the way MFTECmd and Plaso
  do. New USN journal tool in Examine (200k-row budget, banner when hit). The
  timeline ingester picks up `$J` / `$UsnJrnl$J` files from triage folders.
- **Custody chain tip signing.** `CustodySigner` signs `(case_id, sequence,
  entry_hash, signed_utc)` with an ECDSA P-256 examiner key created on first use
  in the user's profile; attestations live in the case file (schema v3) with the
  public key embedded, so verification needs nothing else. A consistent rewrite of
  the log — which the unkeyed chain cannot see — now fails attestation. Custody
  tool: Sign chain tip, attestation verdict, key fingerprint, export as JSON to
  publish out of the examiner's reach. SECURITY.md rewritten accordingly.
- **Bookmarks tool.** Review, delete (recorded in custody) and export the case's
  bookmarks; Reports still consumes them as exhibits.
- **OAuth loopback hardened.** `state` nonce issued and required back with a
  constant-time compare, provider `error` surfaced, listener refuses non-loopback
  prefixes, Dropbox keeps its PKCE verifier. Closes the audit's remaining Medium.
- **Supply chain.** Every GitHub Action pinned by commit SHA; releases ship a
  CycloneDX SBOM (`cinder-sbom.cdx.json`) beside the binaries.

### Added — analysis & workflow

- **Export from every grid.** Every parser tool gains Export CSV / Export JSON
  (`TabularExporter`): exactly the columns shown, string cells guarded against
  spreadsheet formula injection (a filename in evidence starting with `=` is a
  realistic thing to find), numbers left raw. The Strings tool exports too.
- **Timeline export in ecosystem formats.** Timesketch JSONL and CSV
  (`message` / `datetime` / `timestamp_desc` plus source, user, tags) and the
  Sleuth Kit bodyfile that `mactime`, Autopsy and Plaso read. Writes every event
  matching the current filter, not just the 5,000 the grid shows.
- **ATT&CK auto-tagging on the timeline.** `MitreTagger` tags events where the
  mapping is defensible: Security event ids (4624 → T1078, 4625 → T1078 + T1110,
  4698 → T1053.005, 7045 → T1543.003, 4720 → T1136.001, 1102 → T1070.001,
  4719 → T1562.002, 5145 → T1021.002, 1149 → T1021.001 …), Sysmon
  (8 → T1055, 10 on lsass → T1003.001, 12/13/14 → T1112), PowerShell
  script-block logging (4104 → T1059.001), Prefetch/UserAssist → TA0002,
  Recycle Bin → T1070.004. A generic 4688 is deliberately untagged. New
  ATT&CK filter box with id suggestions, and an ATT&CK column in the grid.
- **Strings feature presets.** Filter modes: substring, regex (with match
  timeout), or a bulk_extractor-style preset — email, URL, IPv4/IPv6, domain,
  Luhn-checked payment cards, phone, Bitcoin/Ethereum, MD5/SHA-1/SHA-256,
  Windows/UNC paths, MAC, JWT, AWS key id, private-key block, Base64 (must
  decode). Preset + text narrows within the preset. Invalid regexes are shown,
  not swallowed.
- **Deleted-file recovery on NTFS.** The Filesystem tool walks the $MFT for
  records no longer in use and appends them with `IsDeleted = true` — name,
  size, all four timestamps, MFT index and sequence — under a `[deleted]/`
  path. Names and times only; contents are the carver's job, and the help
  text says so.
- **The custody log now records examiner actions.** Previously only case
  creation and manual hashing were logged. Now: case opened (machine, Cinder
  version), every parser run (tool, evidence, row count, whether truncated),
  every image verification (recorded and computed digests, verdict), mounts,
  data exports and report exports. `ActiveCaseContext.LogAsync` from anywhere;
  no-op without a case, never throws.
- **Filesystem walker moved into `Cinder.Filesystems` and tested against a real
  volume.** `DiscUtilsWalker` now owns detection (ISO / NTFS / FAT / ext / whole
  disk by partition), live enumeration with all timestamps, NTFS deleted-entry
  recovery, and optional hashing. The Filesystem tool is a thin mapper over it.
  Tests walk a checked-in 8 MiB NTFS image (`tests/fixtures/`, built by
  `tools/ntfs-fixture-gen` on Windows — DiscUtils can only *format* NTFS where
  `SecurityIdentifier` exists) — the first parser in Cinder with a
  deterministic fixture, and the read path is verified on Linux as well as
  Windows. The whole suite was run under WSL Ubuntu; settings 0600, executable
  resolution and snapshot enumeration were probed there too.
- **Known-good filtering.** Settings ▸ Hash sets: point at the NSRL database from
  the Hash sets tool and turn on "Hash files during filesystem enumeration". Every
  file gets a SHA-1 and a Verdict column (Known / Notable / Unknown); type
  `Unknown` in the new row filter and operating-system noise drops out. Size cap
  and total-bytes budget, skipped files say why. The Hash sets tool remembers
  its database across launches.
- **Row filter on every grid.** Substring across all columns; export writes what
  the filter shows.
- **Bookmarks → exhibits.** "Bookmark selected" on any grid and on the timeline
  stores the row (as JSON, so it survives column changes), the tool, the evidence
  path and a note in the case file (`bookmarks` table, schema v2, migrated on
  first use) and writes a custody annotation. Reports ▸ "Load bookmarks" turns
  them into a numbered Exhibits section with an index, in PDF / DOCX / HTML / MD.
- **IOC match tool.** Pick an indicator list (one per line; hashes, IPs, domains,
  URLs, emails, free text — classified by shape, CSV rows tolerated) and a
  folder. Matches four ways at once: file hashes (MD5/SHA-1/SHA-256), file paths,
  file contents through the YARA-lite Aho-Corasick scanner in both ASCII and
  UTF-16LE, and every timeline event the ingester can pull from the folder.
  Bounded (256 MB hash / 512 MB content / 200k files / 50k hits) and says so.

### Fixed — critical

- **Hex search never terminated.** `HexSearch.Search` relied on getting a short
  read to exit its window loop, but no `IHexBuffer` implementation ever returns
  one. At the tail of every buffer the window shrank to the overlap size, the
  advance went to zero, and the loop spun forever. Any search finding fewer than
  the caller's hit cap — the ordinary case — pinned a threadpool thread at 100%
  and never produced a result, so find-in-hex-viewer did not work at all. The
  loop now terminates on scanning through to the end offset, refuses a
  non-positive advance, and fills its window across short reads. Boundary-
  spanning matches are reported exactly once.
- **`dotnet test` aborted instead of running.** The above hung the test host
  ("Test host process crashed"), so the whole solution's test run aborted and CI
  had no usable signal. The suite now completes; every `HexSearch` test carries
  a timeout so a regression of that shape fails red rather than wedging the run.
- **A damaged E01 chunk silently truncated the image.** `EwfReader.ReadChunk`
  returned a short buffer when a chunk failed to inflate; `EwfStream` turned
  that into a read of 0, which every caller — the hasher, the carver, the
  signature scanner — correctly read as end-of-media. A partially-read image
  therefore produced a clean-looking result over a fraction of the evidence.
  Damaged chunks are now zero-filled to their declared length and reported via
  `EwfReader.DamagedChunks`; the stream always yields the full media size.

### Fixed — evidence integrity

- **E01 hashes were displayed but never verified.** The Filesystem tool rendered
  the MD5 / SHA-1 recorded *inside* an E01 into its metadata row, where it reads
  as a verification result. It is not — it is an assertion made by whatever
  wrote the container. The row is now labelled `recorded (UNVERIFIED)` and points
  at the Verify tool.
- **Image verify now works without Python.** `VerifyTool` routed through a
  sidecar requiring `libewf-python`, which is not shipped, so it failed for
  everyone. Replaced with in-process verification: `EwfReader.VerifyAsync`
  re-reads the decoded media and compares against the container's recorded
  digests; raw images compare against a `.sha256` / `.sha1` / `.md5` companion
  or a `SHA256SUMS` entry. "No reference digest available" is now reported as
  **unverifiable** rather than collapsing into a boolean — a container that
  records no hash must not render the same as one that failed, or as one that
  passed.
- **Truncated artifact views announced themselves.** Parsers cap how many rows
  they materialize (5k–100k depending on the artifact). Hitting that cap was
  silent, so a grid showing 25,000 of 200,000 registry values looked identical
  to a complete one. Tools now set `IsTruncated`, the status line says so, and
  the grid carries a banner. The registry walker also reports truncation caused
  by its key-depth limit.
- **Slack / unallocated carving produced nothing.** `SlackUnallocCarver`'s slice
  reported `CanSeek == false`, and the carver's extraction path returned an
  empty blob for non-seekable input — so every hit in a slack region was
  reported with length 0 and never written. The slice is now seekable when the
  underlying image is, and the carver carves from its window rather than
  returning nothing when it isn't.

### Security

Findings from the pre-release audit. Full write-up in SECURITY.md.

- **Helper binaries were resolved by bare name (High).** ~20 `Process.Start` sites passed
  `python.exe`, `powershell.exe`, `vssadmin.exe`, `lsblk`, `blockdev`, `zfs` and the eight
  Python sidecar factories as bare names. Windows resolves those against the directory the
  running executable was loaded from, ahead of the system directory and ahead of PATH. Cinder
  ships as a portable single-file exe that examiners keep next to their case files and it
  processes adversary-authored data, so a `python.exe` dropped beside `Cinder.exe` ran instead
  of the real interpreter — as Administrator, per the install instructions. Verified with a
  probe executable, not inferred: the planted binary ran. All sites now route through
  `Cinder.Core.Diagnostics.ExecutableResolver`, which never searches the application or working
  directory. (The current directory turned out *not* to be searched — safe process search mode
  is on — so only the application-directory half of the documented order was exploitable.)
- **Case-bundle extraction cap counted attacker-declared bytes (Medium).** `EncryptedBundle`
  summed `ZipArchiveEntry.Length` against its 32 GB ceiling, then extracted unbounded. A
  crafted bundle could declare almost nothing per entry and inflate arbitrarily. Extraction is
  now a counting copy that aborts on real bytes and deletes the partial file.
- **settings.json was world-readable on Linux/macOS (Medium).** It holds the AI provider API
  key, and the non-Windows key derivation uses machine + user name — so anyone who could read
  the file could also reproduce the key. Now created 0600 on Unix.
- **OAuth loopback flow has no `state` parameter (Medium, not fixed).** Documented in
  SECURITY.md rather than patched: the connectors' token exchange is unfinished and
  unreachable, and this belongs with the work that completes it.

Verified clean during the audit: no secrets in the working tree or git history; zero vulnerable
NuGet packages across 29 projects; XXE closed on every `XmlReader`; no `BinaryFormatter`;
zip-slip guard correct; `ArgumentList` used throughout with no shell string building.

### Fixed — hostile input

`EwfReader` parses attacker-controlled data by definition. Each of these was
reachable by opening a crafted `.E01`:

- Table sections declaring more entries than the section can hold caused either
  a multi-gigabyte allocation or an out-of-range read. Entry counts are now
  checked against the section that declares them, and capped.
- Section sizes were used unvalidated: negative, int-truncating, and
  past-end-of-file values all got through. Sizes are now bounded and checked
  against the file.
- A section chain whose `next` pointers formed a cycle looped the parser
  forever; only the trivial self-loop was caught. The chain must now make
  forward progress, with a hop ceiling as backstop.
- `header2` decompression was an unbounded `CopyTo` — a zlib bomb in the case
  metadata was an OOM before any evidence was read. Now capped.
- Chunk offsets were used verbatim as stream positions without a bounds check.
- Volume geometry (bytes-per-sector, sectors-per-chunk) was adopted unvalidated,
  so a container could dictate a multi-gigabyte chunk buffer or a divide-by-zero.
- A corrupt compressed chunk could inflate past its own extent into the
  following chunk's bytes. Decompression is now bounded to the chunk.

### Fixed — other

- `parsers/requirements.txt` pinned `pyaff4>=1.0`, a version that has never
  existed (PyPI tops out at 0.34). pip stopped at that line, so PythonBootstrap
  could not create the sidecar venv on any machine, and CI's Python step had
  been red for the same reason while the .NET results underneath it passed.
  Removed — `imager_worker.py` lists AFF4 as a TODO and imports nothing from
  it. CI now installs only what the Python tests need (`pydantic` + `pytest`).
- Serilog pinned to 4.3.0, which `Serilog.Extensions.Hosting 10.0.0` requires.
- PCAP parsing looped forever on a truncated or corrupt capture: a non-`PacketRead`
  status other than `NoRemainingPackets` hit `continue` and repeated indefinitely.
- `EwfReader.Open` and the Filesystem tool's E01 path leaked one open file handle
  per segment on every load.
- `HexSearch.DecodeHex` did an unbounded `stackalloc` sized from the user's query
  string — a long pasted query overflowed the stack.
- `EwfReader.DiscoverSegments` had an unreachable-branch `if/else` that always
  broke, and appended `.EAA`-style segments to chains that had not filled all 99
  numeric slots.

### Changed

- **File carver is substantially faster.** Signature matching used a scalar
  byte-by-byte compare across every signature for every offset — roughly
  `window × signatures × headerLength` operations per 4 MiB window, which made a
  whole-disk carve impractical. Now uses vectorized `Span.IndexOf`.
- **Carver no longer emits duplicate hits.** The retained overlap tail was
  re-scanned without tracking which window owned a hit, so any header landing in
  the last `maxHeaderLength` bytes of a window was reported twice.

### Added — tests

110 tests, up from 40; `dotnet test` exits clean.

- `Cinder.Imaging.Tests` (new, 24 tests) — synthetic EWF container builder
  covering round-trip of compressed and uncompressed media, partial final
  chunks, seek/partial reads, verification pass / fail / unverifiable, damaged
  chunk handling, segment discovery, `EvidenceOpener` routing, and every
  hostile-input case listed above.
- `Cinder.Carving.Tests` (new, 18 tests) — window-boundary straddling and
  duplicate suppression, objects extending past their window, footer trimming,
  validator rejection, short-read and non-seekable streams, slack-region offset
  translation.
- `Cinder.Hex.Tests` expanded 6 → 20 — termination, boundary spanning, mmap
  search against a real file, short-read buffers, start/end offsets, oversized
  and malformed queries, cancellation, regex.
- `Cinder.Core.Tests` 33 → 47 — `ExecutableResolverTests` asserts the negative
  property that matters (a binary planted in the application directory is never
  selected); `EncryptedBundleTests` seals deliberately malformed archives through
  the production framing to cover zip-slip and unbounded inflation.

### Documentation

- **SECURITY.md** — new section stating plainly what the chain-of-custody log
  does and does not prove. It is tamper-evident against modification of an
  existing log; it is not tamper-proof, because the hash is unkeyed and stored
  in the same file as the entries it protects, so anyone who can write that file
  can recompute the whole chain. Records the three tracked options for adding an
  external anchor. Same caveat added to `CustodyLog`'s own docs and to
  LIMITATIONS.md.
- **README** — corrected claims that overstated the current state: the Windows
  artifact suite is marked unverified against reference tools (no parity tests
  exist yet), imaging is "read + verify" rather than implying acquisition,
  "court-ready" dropped from report descriptions, and the custody format
  described as tamper-evident.
- **LIMITATIONS.md** — DOCX entry was stale (real OpenXml export has shipped);
  replaced with the actual remaining gap, PDF/A conformance.

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
