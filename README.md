<div align="center">

<img src="assets/branding/cinder-logo.svg" alt="Cinder" width="120" />

# Cinder

**The open-source forensics toolkit that's both genuinely powerful and genuinely beautiful.**

*What remains tells the story.*

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-FF7A1A.svg)](LICENSE)
[![CI](https://github.com/AffluentMods/cinder/actions/workflows/ci.yml/badge.svg)](https://github.com/AffluentMods/cinder/actions/workflows/ci.yml)
[![GitHub release](https://img.shields.io/github/v/release/AffluentMods/cinder?color=FF7A1A)](https://github.com/AffluentMods/cinder/releases)
[![Sponsor](https://img.shields.io/badge/sponsor-%E2%9D%A4-FF7A1A)](https://github.com/sponsors/AffluentMods)

[**Download**](https://github.com/AffluentMods/cinder/releases) ·
[**Documentation**](https://github.com/AffluentMods/cinder/tree/main/docs) ·
[**Roadmap**](ROADMAP.md) ·
[**Contributing**](CONTRIBUTING.md)

</div>

---

## What is Cinder?

Cinder consolidates what currently requires 8+ separate tools — Autopsy, FTK Imager, the Eric Zimmerman suite, Volatility, Hindsight, ExifTool, Plaso, WinHex — into one unified, modern, cross-platform forensics workstation.

Built for digital forensics examiners, incident responders, security students, and homelab tinkerers who deserve better than 1995-era UIs and five-figure price tags.

## Status

**Pre-alpha.** Cinder is in active early development. Phase 0 (foundation) is the current focus. See [ROADMAP.md](ROADMAP.md) for what's shipping when.

## Features (planned)

- Hex viewer/editor with structure overlays and multi-encoding search
- Disk imager (E01, AFF4, raw .dd) with hash verification
- Filesystem parsing for NTFS, FAT, ext4, APFS, HFS+, Btrfs, ZFS, and more
- File carving with smart validation
- Windows artifact parsing — registry, prefetch, shellbags, jumplists, LNK, event logs, browser history, USB/Wi-Fi history, and the rest
- Linux artifact parsing — shell history, journalctl, auth logs, SSH artifacts, package manager logs
- Memory forensics via embedded Volatility 3
- Super-timeline across every artifact, with map and graph visualizations
- Hash sets (NSRL), YARA scanning, fuzzy matching
- Bring-your-own-model AI copilot — Ollama, LM Studio, OpenAI-compatible
- Court-ready reports with auto exhibit numbering and chain-of-custody verification

See [docs/plan.md](docs/plan.md) for the full feature inventory and architecture.

## Why open source

When defense counsel can audit the tool that produced your evidence, methodology challenges are easier to defeat. Open source forensic tools (Sleuth Kit, Volatility, Plaso) hold their ground in courtrooms against five-figure commercial alternatives for exactly this reason. Cinder follows that posture.

## Install

> Pre-alpha — no stable release yet. The instructions below describe how install will work; today, build from source. See [CONTRIBUTING.md](CONTRIBUTING.md).

### Windows

```powershell
winget install AffluentLabs.Cinder
```

Or download the signed `.exe` from [Releases](https://github.com/AffluentMods/cinder/releases).

Windows builds of Cinder are code-signed by [SignPath.io](https://signpath.io), with a certificate issued by the [SignPath Foundation](https://signpath.org)'s free signing program for open-source projects. The certificate's name and authority are visible in the executable's digital signature properties on every signed release. If you encounter an unsigned Windows build labelled as a Cinder release, treat it as untrusted.

### Linux

**AppImage** (any distro):
```bash
curl -LO https://github.com/AffluentMods/cinder/releases/latest/download/Cinder.AppImage
chmod +x Cinder.AppImage
./Cinder.AppImage
```

**Debian/Ubuntu**:
```bash
sudo dpkg -i cinder_x.y.z_amd64.deb
```

**Fedora/RHEL**:
```bash
sudo rpm -i cinder-x.y.z.x86_64.rpm
```

**Arch (AUR)**:
```bash
yay -S cinder
```

## Build from source

You'll need .NET 10 SDK, Python 3.12, and Git. Full setup in [CONTRIBUTING.md](CONTRIBUTING.md).

```bash
git clone https://github.com/AffluentMods/cinder.git
cd cinder
dotnet build
dotnet run --project src/Cinder.App
```

## Permissions

Forensic tools need raw disk access.

- **Windows**: launch with administrator privileges
- **Linux**: `sudo cinder`, or grant capabilities once: `sudo setcap cap_sys_rawio,cap_sys_admin+ep $(which cinder)`

## Architecture

```
Avalonia UI 11 (cross-platform shell)
        ↓
C# .NET 10 core ─── platform abstraction ─── Win/Linux native bits
        ↓                                          ↓
   SQLite case DB                          Python 3.12 sidecars
                                           (pytsk3, regipy, vol3, …)
```

[Full architecture document →](docs/architecture.md)

## Contributing

Contributions welcome and encouraged. Cinder is an open project — every parser, every artifact, every UI improvement helps. See [CONTRIBUTING.md](CONTRIBUTING.md) for setup, conventions, and how to submit a PR.

Looking for a place to start? Issues tagged [`good first issue`](https://github.com/AffluentMods/cinder/labels/good%20first%20issue) are intentionally scoped for newcomers.

## Telemetry

**None.** Cinder does not phone home. Ever. No analytics, no crash reporting that uploads to a server, no update pings beyond a public GitHub API call you can disable.

## Code signing

Windows builds of Cinder are signed under the [SignPath Foundation](https://signpath.org)'s free code-signing program for open-source projects. The signing certificate is issued in the SignPath Foundation's name, with Cinder as the subject; the actual signing is performed by [SignPath.io](https://signpath.io) as part of the release pipeline. The SignPath Foundation may revoke the certificate at any time for violations of its [Code of Conduct](https://signpath.org/policies/code-of-conduct).

Releases prior to SignPath Foundation approval are distributed unsigned and clearly marked as such in their release notes. Build provenance for every signed release is published via GitHub Actions and is independently verifiable.

## Sponsor

Cinder is free and will stay free. If it saves you time, [sponsor on GitHub](https://github.com/sponsors/AffluentMods) or contribute a parser. Funds go toward code-signing certificates, test hardware, and domain renewals.

## License

[Apache License 2.0](LICENSE) — use it commercially, fork it, embed it, modify it. Just keep the copyright notice and don't sue us over patents.

## Acknowledgments

Cinder stands on the shoulders of:

- [The Sleuth Kit](https://www.sleuthkit.org/) and Brian Carrier
- [Autopsy](https://www.autopsy.com/)
- [Volatility Framework](https://volatilityfoundation.org/)
- [Eric Zimmerman's tools](https://ericzimmerman.github.io/)
- [Plaso / log2timeline](https://plaso.readthedocs.io/)
- [regipy](https://github.com/mkorman90/regipy), [libewf](https://github.com/libyal/libewf), [libpff](https://github.com/libyal/libpff)
- And every contributor to open digital forensics.

Built by [Affluent Labs](https://github.com/AffluentMods).
