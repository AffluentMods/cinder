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

If you find anything that isn't listed here, report it through GitHub Security Advisories.
