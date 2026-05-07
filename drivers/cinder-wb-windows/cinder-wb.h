// Cinder write-blocker — public IOCTL surface shared by user-mode (Cinder.Imaging) and the
// kernel driver. Kept ABI-stable across versions; new IOCTLs append, never reuse codes.
#pragma once

#include <ntddk.h>

#define CINDER_WB_DEVICE_NAME      L"\\Device\\CinderWriteBlocker"
#define CINDER_WB_DOS_DEVICE_NAME  L"\\DosDevices\\CinderWB"

#define IOCTL_CINDER_WB_ENGAGE    \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)

#define IOCTL_CINDER_WB_DISENGAGE \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)

#define IOCTL_CINDER_WB_QUERY     \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS)

typedef struct _CINDER_WB_STATE {
    BOOLEAN Active;
    ULONG   BlockedWriteCount;
    ULONG   BlockedIoctlCount;
} CINDER_WB_STATE, *PCINDER_WB_STATE;
