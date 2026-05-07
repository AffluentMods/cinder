# cinder-wb-windows — Cinder Software Write-Blocker (Windows)

Kernel filter driver that blocks writes to attached storage devices below the volume manager.
This is the production write-blocker referenced in `docs/plan.md` §8.2 (Phase 2).

## Status

**SOURCE-ONLY in this repo.** A signed `.sys` file does NOT ship from a normal `dotnet build` —
kernel drivers need:

1. Microsoft Windows Driver Kit (WDK) installed.
2. A code-signing certificate for kernel-mode (EV cert from a Microsoft-trusted CA).
3. Microsoft attestation signing (free) or full WHQL submission (preferred for distribution).
4. Optionally test-signing enabled on the developer machine via `bcdedit /set testsigning on`.

Until those are in place, Cinder's `WindowsWriteBlocker` returns `false` from `TryEngage()`
and the UI prompts the user to use a hardware write-blocker (Tableau, WiebeTech) instead.

## Architecture

- Class filter on the `DiskClass` GUID (`{4D36E967-E325-11CE-BFC1-08002BE10318}`)
- Hooks `IRP_MJ_WRITE` and `IRP_MJ_DEVICE_CONTROL` (FSCTL_DISMOUNT_VOLUME, etc.)
- Soft-fails with `STATUS_MEDIA_WRITE_PROTECTED` when active
- IOCTL `IOCTL_CINDER_WB_ENGAGE` / `IOCTL_CINDER_WB_DISENGAGE` toggle from user-mode

## TODO before this driver can ship

- [ ] Set up SignPath.io OSS account (free for Cinder)
- [ ] Generate EV code-signing CSR; submit to a Microsoft-trusted CA
- [ ] Cross-sign with Microsoft attestation portal
- [ ] Add Microsoft DevCenter publisher account ($99/yr, used for WHQL)
- [ ] Build WDK + KMDF project file (`cinder-wb.vcxproj`)
- [ ] HVCI compatibility audit (no PsCreateSystemThread on imported sections, etc.)
- [ ] Add CI step to download WDK in `release.yml` and build the driver

## File layout (planned)

```
cinder-wb-windows/
├── cinder-wb.inf            # driver install manifest
├── cinder-wb.c              # DriverEntry, AddDevice, IRP dispatch
├── cinder-wb.h              # IOCTLs, structures
├── cinder-wb.rc             # version resource
├── cinder-wb.vcxproj        # WDK build project
└── README.md                # this file
```
