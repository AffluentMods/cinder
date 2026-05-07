# cinder-ram-windows — Cinder RAM Acquisition Driver (Windows)

Live RAM acquisition kernel driver for Windows 10/11. Maps `\Device\PhysicalMemory` (or
uses `MmMapIoSpaceEx` on the boot pool), reads in `PAGE_SIZE` blocks, and streams them up to
user-mode where Cinder writes a Microsoft Crash-Dump-format (`.dmp`) or raw (`.bin`) file
that volatility3 can ingest.

## Status

**SOURCE-ONLY — same signing chain as cinder-wb-windows applies.**

This driver depends on:
- WDK 10+
- Microsoft attestation signing (free) for unattended deployments
- HVCI compatibility — no PsCreateSystemThread on imported sections, no large allocations on
  paged pool below high IRQL
- `dbghelp.dll` redistributable for converting dumped pages into a `.dmp`

Until signing infra is set up, Cinder shells out to `winpmem.exe` (Velocidex) when present
on PATH; that's the path documented in `LIMITATIONS.md → ram-acquisition-windows`.

## Architecture

- Loads as a non-PnP kernel-mode service
- Exposes `\\.\CinderRamCapture` device, accepts IOCTLs:
  - `IOCTL_CINDER_RAM_QUERY_LAYOUT` — return `MEMORY_BASIC_INFORMATION[]` for physical memory
  - `IOCTL_CINDER_RAM_READ_PAGE` — read one 4 KiB page by PFN; returns 0xFFFF for inaccessible pages
- Emits a `MachineMemoryInformation` block at start so user-mode can drop pages outside the layout
- Suspends crash-dump collection while running (so a kernel BSOD during capture still writes an
  intact memory dump)

## Files (planned)

```
cinder-ram-windows/
├── cinder-ram.inf
├── cinder-ram.c
├── cinder-ram.h
├── cinder-ram.vcxproj
└── README.md
```

## TODO before this driver can ship

- [ ] Build with WDK in `release.yml`
- [ ] Submit to attestation portal
- [ ] Add user-mode acquirer that wraps the IOCTL stream into a valid Microsoft Crash Dump
- [ ] Implement winpmem fallback path in C# imager when driver is unavailable
