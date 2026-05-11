# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/AffluentMods/cinder/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/AffluentMods/cinder/releases/tag/v0.1.0
