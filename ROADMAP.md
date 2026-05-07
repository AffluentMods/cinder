# Roadmap

See [docs/plan.md §9](docs/plan.md#9-phased-roadmap) for the canonical phased roadmap with deliverables and acceptance criteria.

This file is a tracker for in-flight work.

## Current phase

**Phase 0 — Foundation** (in progress)

Goal: project skeleton you can demo to a contributor in 5 minutes.

- [ ] `Cinder.sln` with all project skeletons
- [ ] Avalonia 11 shell with three-pane layout
- [ ] FluentAvalonia theme + Cinder palette
- [ ] Command palette (⌘K) with empty action registry
- [ ] SQLite case schema + migrations
- [ ] Hash-chained chain-of-custody log
- [ ] Sidecar protocol + Python echo worker
- [ ] Streaming hash service (MD5/SHA-1/SHA-256/BLAKE3)
- [ ] Serilog structured logging
- [ ] Local crash bundle handler
- [ ] App icon + branding
- [ ] CI workflow green on Windows + Linux
- [ ] Documentation polish

## Upcoming

- **Phase 1** — Hex viewer & hashing (4 weeks) — ships as v0.1.0
- **Phase 2** — Imaging & verification (6 weeks)
- **Phase 3** — Filesystem + carving (8 weeks)
- **Phase 4** — Windows artifacts (10 weeks)
- **Phase 5** — Linux artifacts (4 weeks)
- **Phase 6** — Search, indexing, timeline (8 weeks)
- **Phase 7** — Memory forensics (6 weeks)
- **Phase 8** — Reporting & case management (8 weeks)
- **Phase 9** — AI copilot (year 2)
- **Phase 10** — Network, mobile, cloud (year 2+)

## How to influence the roadmap

- Open an issue with the `roadmap` label
- Vote with reactions on existing roadmap issues
- Submit a PR — the fastest way to ship something
