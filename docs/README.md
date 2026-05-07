# Cinder docs

- **[plan.md](plan.md)** — full project plan v0.2: architecture, feature inventory, phased roadmap, design system. **Start here.**
- **architecture.md** — deep dive on the C# core + Python sidecar architecture (TODO)
- **ai-providers.md** — how to configure local AI backends (TODO)
- **sidecar-protocol.md** — JSON-RPC contract for Python workers (TODO)
- **adr/** — architecture decision records

## For Claude Code

When picking up a phase, read `plan.md` sections 1–7 (foundational context) plus the specific Phase N you're building from section 9.

Example prompt:

> Read docs/plan.md sections 1-7 and Phase 1. Build the hex viewer and hashing module per the deliverables and acceptance criteria.
