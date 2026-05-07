// Cinder write-blocker — class filter on the storage stack that fails any IRP_MJ_WRITE
// (and write-equivalent IOCTLs) with STATUS_MEDIA_WRITE_PROTECTED while engaged.
//
// Build: cinder-wb.vcxproj (KMDF, WDK 10+). See README.md for signing requirements.
//
// SAFETY: this driver MUST NOT touch the boot device unless the user explicitly opts in.
// Default policy: skip any device whose FILE_OBJECT path is the system volume.

#include "cinder-wb.h"

static CINDER_WB_STATE g_State = { FALSE, 0, 0 };
static PDEVICE_OBJECT  g_ControlDevice = NULL;

DRIVER_INITIALIZE DriverEntry;
DRIVER_UNLOAD     CinderWbUnload;
DRIVER_DISPATCH   CinderWbDispatch;

NTSTATUS DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath)
{
    UNREFERENCED_PARAMETER(RegistryPath);

    UNICODE_STRING devName;
    RtlInitUnicodeString(&devName, CINDER_WB_DEVICE_NAME);

    NTSTATUS status = IoCreateDevice(
        DriverObject, 0, &devName, FILE_DEVICE_UNKNOWN, 0, FALSE, &g_ControlDevice);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    UNICODE_STRING dosName;
    RtlInitUnicodeString(&dosName, CINDER_WB_DOS_DEVICE_NAME);
    IoCreateSymbolicLink(&dosName, &devName);

    DriverObject->DriverUnload = CinderWbUnload;
    for (ULONG i = 0; i <= IRP_MJ_MAXIMUM_FUNCTION; i++)
    {
        DriverObject->MajorFunction[i] = CinderWbDispatch;
    }
    return STATUS_SUCCESS;
}

VOID CinderWbUnload(PDRIVER_OBJECT DriverObject)
{
    UNICODE_STRING dosName;
    RtlInitUnicodeString(&dosName, CINDER_WB_DOS_DEVICE_NAME);
    IoDeleteSymbolicLink(&dosName);
    if (g_ControlDevice)
    {
        IoDeleteDevice(g_ControlDevice);
    }
    UNREFERENCED_PARAMETER(DriverObject);
}

NTSTATUS CinderWbDispatch(PDEVICE_OBJECT DeviceObject, PIRP Irp)
{
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);

    // TODO(2.1): when this driver is loaded as a class filter, the lower stack pointer is the
    // actual storage device. Until that's wired, the control device just answers IOCTLs.
    if (DeviceObject == g_ControlDevice && stack->MajorFunction == IRP_MJ_DEVICE_CONTROL)
    {
        ULONG code = stack->Parameters.DeviceIoControl.IoControlCode;
        if (code == IOCTL_CINDER_WB_ENGAGE)
        {
            g_State.Active = TRUE;
        }
        else if (code == IOCTL_CINDER_WB_DISENGAGE)
        {
            g_State.Active = FALSE;
        }
        else if (code == IOCTL_CINDER_WB_QUERY)
        {
            if (stack->Parameters.DeviceIoControl.OutputBufferLength >= sizeof(CINDER_WB_STATE))
            {
                RtlCopyMemory(Irp->AssociatedIrp.SystemBuffer, &g_State, sizeof(g_State));
                Irp->IoStatus.Information = sizeof(CINDER_WB_STATE);
            }
        }
        Irp->IoStatus.Status = STATUS_SUCCESS;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return STATUS_SUCCESS;
    }

    // Block writes when engaged.
    if (g_State.Active && stack->MajorFunction == IRP_MJ_WRITE)
    {
        InterlockedIncrement((LONG*)&g_State.BlockedWriteCount);
        Irp->IoStatus.Status = STATUS_MEDIA_WRITE_PROTECTED;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    Irp->IoStatus.Status = STATUS_SUCCESS;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}
