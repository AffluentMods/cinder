# Security policy

## Reporting a vulnerability

**Do not open a public GitHub issue for security vulnerabilities.**

Use [GitHub Security Advisories](https://github.com/AffluentMods/cinder/security/advisories/new) to report privately. This goes to maintainers only and supports a coordinated disclosure timeline.

Alternatively, email **security@cinder.dev** with PGP encryption (key on the website).

## What to include

- Description of the vulnerability
- Steps to reproduce
- Affected versions
- Potential impact (data exposure, code execution, evidence integrity, etc.)
- Suggested mitigation if you have one

## Disclosure timeline

- **Acknowledgment**: within 72 hours
- **Initial assessment**: within 7 days
- **Patch development**: timeline communicated based on severity
- **Public disclosure**: 90 days after report, or earlier if a patch ships sooner

We coordinate disclosure with reporters and credit them in the CHANGELOG and security advisory unless they request anonymity.

## Scope

In scope:
- The Cinder application binaries
- The Cinder.* C# libraries
- Python sidecar workers in `parsers/`
- The Windows kernel write-blocker driver
- GitHub Actions CI/CD configuration
- Build/release pipeline integrity

Out of scope:
- Vulnerabilities in upstream dependencies (report those upstream; we will update once patched)
- Vulnerabilities in third-party AI providers (Ollama, OpenAI, etc.)
- Social engineering of maintainers
- Denial of service via resource exhaustion on intentionally malformed evidence (forensic tools must handle untrusted input by definition; we'll fix crashes but won't treat them as security issues unless they enable code execution)

## Bug bounty

Cinder is a free OSS project with no funding for bounties. Reporters are credited in the CHANGELOG, the GitHub Security Advisory, and a Hall of Fame on the website. We're sorry we can't pay you — and very grateful you reported.

## Threat model

Cinder reads evidence created or controlled by an attacker (disk images, registry hives, EVTX,
malware samples) and parses it on a workstation that may be running with elevated privileges.
The threat actors we defend against:

- **Malicious evidence files.** An examiner opens a `*.E01` / `*.evtx` / `*.docx` / `*.lnk` that
  was crafted to exploit a parser. Cinder treats every byte of evidence as untrusted.
- **Malicious case bundles.** A `*.cinder` or encrypted bundle sent between examiners is
  attacker-controlled. We defend against zip-slip, decompression bombs, and tampered chain
  hashes.
- **Malicious plugins.** A `.dll` or `.py` dropped into the user's plugin folder by malware
  must not auto-load on next launch.
- **Memory-resident secrets.** AES keys, PBKDF2 outputs, and decrypted bundle plaintext are
  zeroed from process memory as soon as their useful lifetime ends.

We do **not** defend against:

- A privileged-local-user attacker who has already compromised the box. They can replace
  Cinder.exe itself.
- An examiner who deliberately writes malicious tooling and shares it with peers. Code-signing
  on Windows raises the bar but is not a complete defense.

## What v0.1.0 hardened against (security pass landed May 2026)

The first public release was preceded by a security audit. The fixes that landed in code:

- **Command injection.** Every `Process.Start` call site that runs a system tool (`losetup`,
  `mount`, `umount`, `vssadmin`, `blockdev`, `lsblk`, `btrfs`, `lvs`, `zfs`, `powershell.exe`,
  `wkhtmltopdf`, headless Chromium) now uses `ProcessStartInfo.ArgumentList` to pass each
  argument as its own slot. No more string interpolation into command lines.
- **PowerShell single-quote injection.** The Windows VHD/ISO mount path now passes the image
  path via `$args[0]` instead of interpolating into the script body, so a filename containing
  `'` can't break out of the PS quoting.
- **wkhtmltopdf SSRF / local-file-read.** Dropped the `--enable-local-file-access` flag from
  the PDF exporter. Without it, a malicious report template can no longer reach
  `file:///etc/shadow` (or equivalent) via embedded `<iframe>` / `<img>` and bake it into the
  PDF.
- **Plugin loader.** `Cinder.Plugins.PluginLoader` now refuses to load anything unless the
  user has placed a `.cinder-trusted` sentinel file in the plugin directory AND has listed
  each plugin's SHA-256 in a `.cinder-plugins.sha256` manifest. Untrusted plugins are surfaced
  in the Plugins tool with their hash for the user to approve.
- **EncryptedBundle.** Hardened against zip-slip (manual entry walk + destination clamp),
  decompression bombs (8 GB ciphertext / 32 GB extracted caps), and post-decrypt memory
  hygiene (`CryptographicOperations.ZeroMemory` on the derived key, plaintext, and ciphertext
  buffers).
- **Custody log separator injection.** `CustodyLog.AppendAsync` now rejects U+001F (the chain
  hash field separator) and other C0 control characters in `examiner` / `action` / `detailsJson`.
  Closes a theoretical preimage attack against the court-defensible custody chain.
- **Crypto choices.** AES-256-GCM with random 12-byte nonce per encryption; PBKDF2-SHA256 at
  600,000 iterations (OWASP 2024 minimum); cryptographically random salt per encryption.
- **NuGet transitive vulnerability.** Pinned `Tmds.DBus.Protocol 0.21.3` to override
  Avalonia.FreeDesktop's transitively-vulnerable `0.20.0` (GHSA-xrw6-gwf8-vvr9).
- **Dependency audit.** `dotnet list package --vulnerable --include-transitive` reports zero
  vulnerabilities across all 24 projects.

## What the pre-release audit hardened (September 2026)

A second audit ahead of the public release. The findings that produced code changes:

- **Helper binaries were resolved by bare name.** ~20 call sites passed `python.exe`,
  `powershell.exe`, `vssadmin.exe`, `lsblk`, `blockdev`, `zfs` and friends straight to
  `Process.Start`. Windows resolves a bare name against *the directory the running executable
  was loaded from* before the system directory and before PATH. Cinder ships as a portable
  single-file executable that examiners keep alongside their case files, and it processes
  adversary-authored data — so a `python.exe` dropped next to `Cinder.exe` was executed in
  preference to the real interpreter, at whatever privilege Cinder held. The documentation asks
  for Administrator.

  Verified by experiment, not by reading: a probe executable with `cmd.exe` renamed to
  `python.exe` beside it ran the planted binary. (The *current* directory turned out not to be
  searched — safe process search mode is active — so only the application-directory half of the
  documented search order was actually exploitable.) All call sites now go through
  `Cinder.Core.Diagnostics.ExecutableResolver`, which searches the Windows system directory and
  PATH and never the application or working directory. Re-verified with the same probe.

- **The case-bundle extraction cap counted attacker-declared bytes.** `EncryptedBundle` summed
  `ZipArchiveEntry.Length` — the uncompressed size the archive *claims* — against its 32 GB
  ceiling, then extracted without bound. A crafted bundle could declare a few hundred bytes per
  entry and inflate arbitrarily. Extraction is now a counting copy that aborts on the real byte
  total and deletes the partial file. (Bundles are AES-GCM authenticated, so this needed a
  bundle from someone you accepted one from — which is the normal way bundles move.)

- **settings.json was world-readable on Linux/macOS.** It holds the AI provider API key, and
  the non-Windows encryption fallback derives its key from the machine and user name — so any
  local account that could read the file could also reproduce the key. Two documented-but-weak
  controls cancelling each other out. The file is now created 0600 on Unix; Windows
  `%LOCALAPPDATA%` was already ACL-scoped.

Checked and found already correct: XXE closed on all five `XmlReader` sites; no
`BinaryFormatter` anywhere; the zip-slip guard; `ArgumentList` used throughout with no shell
string interpolation; PowerShell arguments passed via `$args[]`; the absent
`--enable-local-file-access` on the PDF path; the plugin trust sentinel plus SHA-256 manifest;
PKCE S256; no secrets in the working tree or in git history; zero vulnerable NuGet packages.

Still open from this audit, tracked below: the OAuth loopback flow carries no `state`
parameter, and the Dropbox connector discards its PKCE verifier. Both sit in scaffolding whose
token exchange is not yet wired, and both are fixed before the cloud connectors ship.

## Review of the format and journal work (September 2026, post-audit)

The EWF writer, AFF4 reader / writer, `$LogFile` parser, RFC 3161 client and in-process
imager landed after the pre-release audit. They were reviewed against the same threat model —
evidence files are attacker-controlled bytes; network peers are hostile — with these results:

- **Fixed — `Rfc3161Timestamper` unbounded response buffering** (Medium). Headers-first read,
  1 MiB declared-length ceiling, streamed body under a running cap.
- **Fixed — `Aff4Reader` bevy ceiling** (Low). 2 GiB → 512 MiB; a container can no longer ask
  for a 2 GiB allocation per stream. Chunk indices are 64-bit so a multi-TiB image with small
  chunks cannot wrap into the wrong chunk.
- **Fixed — Verify tool false negatives on VHD / VHDX** (correctness, not security, but a
  false "verification failed" against evidence is a finding in its own right).
- **Reviewed, no change:** `NtfsLogFile` (1 MiB client-data ceiling, continuation bounded by
  page count, attribute walk capped, every payload decoder length-checked, torn pages left
  unfixed rather than guessed); snappy and LZ4 block decoders (every copy bounds-checked
  against both buffers; negative tests for literal and match overruns); `EwfWriter` (no
  untrusted input; metadata sanitised before the tab-separated header); `InProcessImager`
  (devices opened read-only and shared; VHD / VHDX refuse an unknown-length source instead of
  guessing a capacity); `EvidenceOpener` magic detection (fixed-size reads at two offsets, no
  parsing before the reader's own bounds checks).
- **Tested adversarially:** scrambled AFF4 bevy, implausible AFF4 geometry, unterminated turtle,
  torn `$LogFile` page, random bytes as `$LogFile`, hostile snappy / LZ4 streams.

## Known limitations (tracked, fix planned)

These items came out of the audit and are tracked but not yet fixed. We documented them in
the open rather than leaving them implicit.

- **API keys at rest**. Settings.json values containing `apiKey` / `ApiKey` / `api_key`
  are now encrypted before write and decrypted on load. On Windows we use DPAPI
  (`CurrentUser` scope) — only the same user on the same machine can decrypt. On Linux /
  macOS we fall back to an AES-GCM scheme with the key derived from
  `MachineName + UserName + "cinder.v1"`; that's obfuscation rather than real protection
  (anyone with read access to your home directory and machine ID can decrypt). True
  libsecret / KeyChain integration on Linux / macOS remains tracked.
- **Passphrases are passed as `string`.** Garbage-collected, immutable, not zeroable. Argon2id
  + `SecureString` / `byte[]` adapters are tracked for the Phase 8 case-management deepening.
- **No Authenticode signature check on plugins.** The SHA-256 manifest is the current gate.
  Authenticode verification on Windows + GPG signature verification on Linux are tracked.
- **Plugins run in the host process.** The `[LoadIsolated]` attribute is declared but not yet
  enforced; isolation via `AssemblyLoadContext` / sidecar process is on the Phase 9 roadmap.
- **Self-update.** Not implemented. Users are responsible for downloading new releases and
  verifying SHA-256 against `SHA256SUMS.txt` in the GitHub Release.
- **OAuth loopback flow** — fixed: every connector now issues a `state` nonce,
  `AwaitRedirectCodeAsync` requires it back (constant-time compare), surfaces provider
  `error` responses, and refuses to bind anything but a loopback prefix. The Dropbox
  connector keeps its PKCE verifier. Covered by `OAuthPkceHelperTests` against a live
  loopback listener. The connectors' end-to-end token exchange is still unfinished
  (Phase 10.1).

## What the chain-of-custody log does and does not prove

This deserves to be stated plainly, because the phrase "hash-chained custody log" invites a
stronger reading than the implementation supports.

`Cinder.Core.Custody.CustodyLog` chains each entry to its predecessor with
`SHA-256(prev_hash ‖ sequence ‖ timestamp ‖ examiner ‖ action ‖ details)` and
`VerifyAsync` recomputes the whole chain. That reliably detects **accidental** corruption and
**naive** tampering: editing one row's text, deleting a row, reordering rows, or splicing a row
in all break the chain and are reported.

It does **not** resist a deliberate rewrite. The hash is unkeyed and every entry — including
every stored hash — lives in the same SQLite file as the data it protects. Anyone who can write
that file can recompute the entire chain from the genesis entry forward and produce a log that
verifies cleanly. There is no secret an attacker lacks and no external anchor to check against.

So the correct claim is **tamper-evident against modification of an existing log**, not
tamper-proof, and not "court-defensible" on its own. Its evidentiary value comes from the same
place it does for any examiner's notes: the surrounding process — who held the file, on what
media, under what access controls.

### Attestations — the anchor (shipped)

`CustodySigner` signs the chain's tip — `(case_id, sequence, entry_hash, signed_utc)` — with an
ECDSA P-256 key generated in the examiner's own profile
(`<LocalAppData>/Cinder/examiner-signing-key.p8`, mode 0600 on Unix). The attestation, public
key included, is stored in the case file (`custody_attestations`, schema v3) and can be
exported as a self-contained JSON document. **Verification needs only the case file.**

What that changes: a rewrite of the log after signing re-hashes the chain consistently, and
`VerifyAsync` on the chain says "intact" — but the entry at the attested sequence no longer
carries the attested hash, and re-signing needs the private key. So the guarantee becomes:
**nobody who lacks the examiner's key can alter the log up to the attested point without it
showing.** `CustodySignerTests` demonstrates exactly this: full rewrite, chain re-verifies,
attestation fails.

What it still does not do on its own: protect against the examiner themself, whose key it
is. Two things close that, one in code and one in process:

- **RFC 3161 trusted timestamps** (`Rfc3161Timestamper`). With a Time-Stamp Authority
  configured, signing also sends the SHA-256 of the attestation signature to the TSA and
  stores its countersigned token (DER, self-contained) beside the attestation. The examiner
  can still sign a rewritten log, but cannot make the new attestation look older than it is —
  the time comes from the TSA's clock and signature. Verification checks the token's imprint
  against the signature and the token's CMS signature under the certificate it carries; whether
  that certificate chains to a root the machine trusts is reported separately, because many
  TSAs run roots that are not in system stores. `Rfc3161TimestamperTests` runs the exchange
  against a BouncyCastle TSA rather than a mock, and includes grafting a token onto a different
  signature (rejected).
- **Publishing the export** somewhere the examiner cannot edit — a supervisor's inbox, a
  ticket, a WORM share — which remains a process step, not code.

Present a Cinder custody log with attestations as: a self-checking activity record whose
state at each attested point is signed by the examiner's key. Without attestations, present
it as a self-checking record only.

If you find anything that isn't listed here, report it through GitHub Security Advisories.
