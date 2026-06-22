/*---------------------------------------------------------------------------*\
| Driver.c — BrokerSmbus                                                     |
|                                                                             |
|   Minimal non-PnP KMDF "software" driver that exposes a single control      |
|   device (\\.\BrokerSmbus) speaking the IOCTL contract in                  |
|   inc/SmbusBrokerProtocol.h. The driver's ENTIRE purpose is bounded SMBus   |
|   transactions on behalf of the user-mode broker — nothing else.            |
|                                                                             |
|   Requires the Windows Driver Kit (WDK) to build. See README.md.            |
\*---------------------------------------------------------------------------*/
#include <ntddk.h>
#include <wdf.h>
#include <wdmsec.h>          /* SDDL_DEVOBJ_SYS_ALL_ADM_ALL */
#include "inc/SmbusBrokerProtocol.h"
#include "Smbus.h"

DRIVER_INITIALIZE                   DriverEntry;
EVT_WDF_DRIVER_UNLOAD               BrokerSmbusEvtDriverUnload;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL  BrokerSmbusEvtIoDeviceControl;

static NTSTATUS BrokerSmbusCreateControlDevice(_In_ WDFDRIVER Driver);

/* Detected once at load; read-only thereafter. */
static SMBUS_CONTROLLER g_Controller;

/* Bounded ASCII copy for ENUM_BACKENDS names (always null-terminates). */
static VOID BrokerCopyBackendName(_Out_writes_(BROKER_BACKEND_NAME_MAX) CHAR* Dest,
                                  _In_z_ const CHAR* Src)
{
    ULONG i;
    for (i = 0; i + 1 < BROKER_BACKEND_NAME_MAX && Src[i] != '\0'; i++)
        Dest[i] = Src[i];
    Dest[i] = '\0';
}

_Use_decl_annotations_
NTSTATUS DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDFDRIVER         driver;
    NTSTATUS          status;

    WDF_DRIVER_CONFIG_INIT(&config, WDF_NO_EVENT_CALLBACK);
    config.DriverInitFlags |= WdfDriverInitNonPnpDriver;   /* software-only, loaded as a service */
    config.EvtDriverUnload  = BrokerSmbusEvtDriverUnload;

    status = WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, &driver);
    if (!NT_SUCCESS(status))
        return status;

    /* Auto-detect the SMBus host controller once. Failure is non-fatal: the
       device still loads and reports Vendor=Unknown / no read capability. */
    (void)SmbusDetectController(&g_Controller);

    /* Independently detect the AMD SMU CPU-temperature path (CPUID-gated). */
    SmuAmdDetect(&g_Controller);

    /* Independently detect a Super-I/O for board temps/fans over LPC. Probes run
       in registry order (g_SuperioBackends in SuperioNct.c: NCT668x EC family,
       then the NCT6775 bank-select family); the first probe to claim a chip wins,
       so a board matches exactly one backend. */
    SuperioDetectAll(&g_Controller);

    return BrokerSmbusCreateControlDevice(driver);
}

_Use_decl_annotations_
VOID BrokerSmbusEvtDriverUnload(WDFDRIVER Driver)
{
    UNREFERENCED_PARAMETER(Driver);
    /* The control device is a child of the driver and is torn down automatically. */
}

static NTSTATUS BrokerSmbusCreateControlDevice(WDFDRIVER Driver)
{
    PWDFDEVICE_INIT     init;
    WDFDEVICE           device;
    WDF_IO_QUEUE_CONFIG queueConfig;
    NTSTATUS            status;

    DECLARE_CONST_UNICODE_STRING(deviceName, BROKER_SMBUS_DEVICE_NAME);
    DECLARE_CONST_UNICODE_STRING(symlink,    BROKER_SMBUS_SYMLINK_NAME);

    /* Restrict the device to SYSTEM + Administrators. The broker that opens it
       runs with the privilege needed to load the driver in the first place. */
    init = WdfControlDeviceInitAllocate(Driver, &SDDL_DEVOBJ_SYS_ALL_ADM_ALL);
    if (init == NULL)
        return STATUS_INSUFFICIENT_RESOURCES;

    WdfDeviceInitSetExclusive(init, FALSE);
    WdfDeviceInitSetDeviceType(init, FILE_DEVICE_BROKER_SMBUS);

    status = WdfDeviceInitAssignName(init, &deviceName);
    if (!NT_SUCCESS(status)) { WdfDeviceInitFree(init); return status; }

    status = WdfDeviceCreate(&init, WDF_NO_OBJECT_ATTRIBUTES, &device);
    if (!NT_SUCCESS(status)) { if (init) WdfDeviceInitFree(init); return status; }

    status = WdfDeviceCreateSymbolicLink(device, &symlink);
    if (!NT_SUCCESS(status))
        return status;

    /* Sequential dispatch is LOAD-BEARING for safety: it serializes ALL IOCTLs (XFER, SMU,
       SUPERIO, WRITE) to one at a time, so the SMBus/SMU/EC backends — which touch shared
       FCH/firmware state and have only per-backend mutexes — can never run concurrently with
       each other. Do not change this to parallel dispatch without adding a single global
       hardware lock across all backends. */
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = BrokerSmbusEvtIoDeviceControl;

    /*-----------------------------------------------------------------------*\
    | Force EvtIoDeviceControl to run at PASSIVE_LEVEL. The SMBus backends     |
    | serialize with a dispatcher mutex and sleep between bounded poll steps   |
    | (KeWaitForSingleObject / KeDelayExecutionThread), both of which require  |
    | PASSIVE_LEVEL. Without this the framework may dispatch at DISPATCH_LEVEL. |
    \*-----------------------------------------------------------------------*/
    WDF_OBJECT_ATTRIBUTES queueAttributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&queueAttributes);
    queueAttributes.ExecutionLevel = WdfExecutionLevelPassive;

    status = WdfIoQueueCreate(device, &queueConfig, &queueAttributes, NULL);
    if (!NT_SUCCESS(status))
        return status;

    WdfControlFinishInitializing(device);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
VOID BrokerSmbusEvtIoDeviceControl(WDFQUEUE Queue, WDFREQUEST Request,
    size_t OutputBufferLength, size_t InputBufferLength, ULONG IoControlCode)
{
    NTSTATUS status        = STATUS_INVALID_DEVICE_REQUEST;
    size_t   bytesReturned = 0;
    PVOID    inBuf, outBuf;
    size_t   bufLen;

    UNREFERENCED_PARAMETER(Queue);
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    switch (IoControlCode)
    {
    case IOCTL_BROKER_SMBUS_INFO:
    {
        BROKER_SMBUS_INFO_RESPONSE* info;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(*info), &outBuf, &bufLen);
        if (!NT_SUCCESS(status)) break;

        info = (BROKER_SMBUS_INFO_RESPONSE*)outBuf;
        /* Zero first so any field not explicitly set below (or added later) can never
           leak stale system-buffer/pool bytes to user mode, matching the XFER/SMU/SUPERIO
           handlers. Every field is in fact written below today. */
        RtlZeroMemory(info, sizeof(*info));
        info->Version      = BROKER_SMBUS_PROTOCOL_VERSION;
        info->BusCount     = g_Controller.BusCount;
        info->Capabilities = (g_Controller.ReadImplemented  ? BROKER_SMBUS_CAP_READ    : 0)
                           | (g_Controller.SmuAvailable     ? BROKER_SMBUS_CAP_SMU     : 0)
                           | (g_Controller.SuperioAvailable ? BROKER_SMBUS_CAP_SUPERIO : 0)
                           | ((g_Controller.Backend != NULL &&
                               g_Controller.Backend->WriteImplemented &&
                               g_Controller.ReadImplemented)
                                                            ? BROKER_SMBUS_CAP_WRITE   : 0)
                           | (g_Controller.SuperioRgbImplemented
                                                            ? BROKER_SMBUS_CAP_SUPERIO_RGB : 0);
        info->Vendor       = (UINT32)g_Controller.Vendor;
        for (ULONG bi = 0; bi < 8; bi++)
            info->BusInfo[bi] = (bi < g_Controller.BusCount)
                ? (((UINT32)g_Controller.Buses[bi].PortSelect << 16) | g_Controller.Buses[bi].IoBase)
                : 0;
        info->SuperioChipId = (UINT32)g_Controller.SuperioChipId;   /* 0xD59x = Nuvoton, 0x86xx/0x87xx = ITE */
        bytesReturned      = sizeof(*info);
        break;
    }
    case IOCTL_BROKER_SMBUS_XFER:
    {
        BROKER_SMBUS_XFER_REQUEST   reqLocal;
        BROKER_SMBUS_XFER_RESPONSE* resp;

        status = WdfRequestRetrieveInputBuffer(Request, sizeof(reqLocal), &inBuf, &bufLen);
        if (!NT_SUCCESS(status)) break;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(*resp), &outBuf, &bufLen);
        if (!NT_SUCCESS(status)) break;

        /* METHOD_BUFFERED aliases the input and output onto a single system
           buffer, so inBuf and outBuf point at the same memory. Snapshot the
           request into a local BEFORE zeroing the response — otherwise
           RtlZeroMemory wipes the request fields (Version -> 0 -> BadRequest). */
        reqLocal = *(BROKER_SMBUS_XFER_REQUEST*)inBuf;
        resp = (BROKER_SMBUS_XFER_RESPONSE*)outBuf;
        RtlZeroMemory(resp, sizeof(*resp));

        resp->Status  = BrokerSmbusXfer(&g_Controller, &reqLocal, resp);
        bytesReturned = sizeof(*resp);
        status        = STATUS_SUCCESS;
        break;
    }
    case IOCTL_BROKER_SMU_READ:
    {
        BROKER_SMU_READ_REQUEST   reqLocal;
        BROKER_SMU_READ_RESPONSE* resp;
        UINT32                     raw = 0;

        status = WdfRequestRetrieveInputBuffer(Request, sizeof(reqLocal), &inBuf, &bufLen);
        if (!NT_SUCCESS(status)) break;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(*resp), &outBuf, &bufLen);
        if (!NT_SUCCESS(status)) break;

        /* Snapshot before zeroing the response (METHOD_BUFFERED aliases the buffers). */
        reqLocal = *(BROKER_SMU_READ_REQUEST*)inBuf;
        resp = (BROKER_SMU_READ_RESPONSE*)outBuf;
        RtlZeroMemory(resp, sizeof(*resp));

        if (reqLocal.Version != BROKER_SMBUS_PROTOCOL_VERSION)
        {
            resp->Status = BrokerSmbusBadRequest;
        }
        else
        {
            resp->Status = SmuAmdRead(&g_Controller, reqLocal.Sensor, &raw);
            resp->Raw    = raw;
        }
        bytesReturned = sizeof(*resp);
        status        = STATUS_SUCCESS;
        break;
    }
    case IOCTL_BROKER_SUPERIO_READ:
    {
        BROKER_SUPERIO_READ_REQUEST   reqLocal;
        BROKER_SUPERIO_READ_RESPONSE* resp;
        UINT32                         raw = 0;

        status = WdfRequestRetrieveInputBuffer(Request, sizeof(reqLocal), &inBuf, &bufLen);
        if (!NT_SUCCESS(status)) break;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(*resp), &outBuf, &bufLen);
        if (!NT_SUCCESS(status)) break;

        /* Snapshot before zeroing the response (METHOD_BUFFERED aliases the buffers). */
        reqLocal = *(BROKER_SUPERIO_READ_REQUEST*)inBuf;
        resp = (BROKER_SUPERIO_READ_RESPONSE*)outBuf;
        RtlZeroMemory(resp, sizeof(*resp));

        if (reqLocal.Version != BROKER_SMBUS_PROTOCOL_VERSION)
        {
            resp->Status = BrokerSmbusBadRequest;
        }
        else
        {
            resp->Status = SuperioReadDispatch(&g_Controller, reqLocal.Kind, reqLocal.Index, &raw);
            resp->Raw    = raw;
        }
        bytesReturned = sizeof(*resp);
        status        = STATUS_SUCCESS;
        break;
    }
    case IOCTL_BROKER_ENUM_BACKENDS:
    {
        BROKER_ENUM_BACKENDS_RESPONSE* resp;
        ULONG i;

        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(*resp), &outBuf, &bufLen);
        if (!NT_SUCCESS(status)) break;

        /* Output-only (like INFO): reports the detect-time registry state; touches
           no hardware. Built straight from the backend tables, so this can never
           drift from what the driver actually compiled in. */
        resp = (BROKER_ENUM_BACKENDS_RESPONSE*)outBuf;
        RtlZeroMemory(resp, sizeof(*resp));
        resp->Version = BROKER_SMBUS_PROTOCOL_VERSION;

        for (i = 0; i < g_SmbusBackendCount && resp->Count < BROKER_ENUM_BACKENDS_MAX; i++)
        {
            BROKER_BACKEND_INFO* e = &resp->Backends[resp->Count++];
            BrokerCopyBackendName(e->Name, g_SmbusBackends[i].Name);
            e->Class  = BROKER_BACKEND_CLASS_SMBUS;
            e->Active = (g_Controller.Backend == &g_SmbusBackends[i]) ? 1u : 0u;
            e->Detail = e->Active ? g_Controller.PciDeviceId : 0u;
        }

        /* The SMU path is CPUID-gated (not PCI/SIO-probed) — a single fixed entry. */
        if (resp->Count < BROKER_ENUM_BACKENDS_MAX)
        {
            BROKER_BACKEND_INFO* e = &resp->Backends[resp->Count++];
            BrokerCopyBackendName(e->Name, "AMD SMU");
            e->Class  = BROKER_BACKEND_CLASS_SMU;
            e->Active = g_Controller.SmuAvailable ? 1u : 0u;
            e->Detail = e->Active
                ? (((UINT32)g_Controller.CpuFamily << 8) | g_Controller.CpuModel) : 0u;
        }

        for (i = 0; i < g_SuperioBackendCount && resp->Count < BROKER_ENUM_BACKENDS_MAX; i++)
        {
            BROKER_BACKEND_INFO* e = &resp->Backends[resp->Count++];
            BrokerCopyBackendName(e->Name, g_SuperioBackends[i].Name);
            e->Class  = BROKER_BACKEND_CLASS_SUPERIO;
            e->Active = (g_Controller.SuperioAvailable &&
                         g_Controller.SuperioKind == g_SuperioBackends[i].Kind) ? 1u : 0u;
            e->Detail = e->Active ? g_Controller.SuperioChipId : 0u;
        }

        bytesReturned = sizeof(*resp);
        status        = STATUS_SUCCESS;
        break;
    }
    case IOCTL_BROKER_SMBUS_WRITE:
    {
        BROKER_SMBUS_WRITE_REQUEST   reqLocal;
        BROKER_SMBUS_WRITE_RESPONSE* resp;
        size_t                        inLen;

        /* Byte/word writes may legally send only the original 24-byte prefix;
           the block fields were APPENDED for WriteBlock. Accept the prefix as
           the minimum, zero-fill the rest, and require the full struct (so
           Block[]/Length are caller-supplied, not our zero padding) for block. */
        status = WdfRequestRetrieveInputBuffer(Request, BROKER_SMBUS_WRITE_REQUEST_V1_SIZE, &inBuf, &inLen);
        if (!NT_SUCCESS(status)) break;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(*resp), &outBuf, &bufLen);
        if (!NT_SUCCESS(status)) break;

        /* Snapshot before zeroing the response (METHOD_BUFFERED aliases the buffers).
           Explicit clamp (not the min() macro) so the copy length never depends on a
           header-provided helper being in scope. */
        RtlZeroMemory(&reqLocal, sizeof(reqLocal));
        RtlCopyMemory(&reqLocal, inBuf, (inLen < sizeof(reqLocal)) ? inLen : sizeof(reqLocal));
        resp = (BROKER_SMBUS_WRITE_RESPONSE*)outBuf;
        RtlZeroMemory(resp, sizeof(*resp));

        if (reqLocal.Op == BrokerSmbusWriteBlock && inLen < BROKER_SMBUS_WRITE_REQUEST_BLOCK_SIZE)
        {
            resp->Status = BrokerSmbusBadRequest;
        }
        else
        {
            /* BrokerSmbusWrite applies the in-kernel brick-guard (RGB range only). */
            resp->Status = BrokerSmbusWrite(&g_Controller, &reqLocal);
        }
        bytesReturned = sizeof(*resp);
        status        = STATUS_SUCCESS;
        break;
    }
    case IOCTL_BROKER_SUPERIO_RGB_WRITE:
    {
        BROKER_SUPERIO_RGB_WRITE_REQUEST   reqLocal;
        BROKER_SUPERIO_RGB_WRITE_RESPONSE* resp;

        status = WdfRequestRetrieveInputBuffer(Request, sizeof(reqLocal), &inBuf, &bufLen);
        if (!NT_SUCCESS(status)) break;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(*resp), &outBuf, &bufLen);
        if (!NT_SUCCESS(status)) break;

        /* Snapshot before zeroing the response (METHOD_BUFFERED aliases the buffers). */
        reqLocal = *(BROKER_SUPERIO_RGB_WRITE_REQUEST*)inBuf;
        resp = (BROKER_SUPERIO_RGB_WRITE_RESPONSE*)outBuf;
        RtlZeroMemory(resp, sizeof(*resp));

        /* SuperioRgbWrite applies the in-kernel RGB-window brick-guard and the
           SuperioRgbImplemented kill-switch (refuses everything while HW-unvalidated). */
        resp->Status  = SuperioRgbWrite(&g_Controller, &reqLocal);
        bytesReturned = sizeof(*resp);
        status        = STATUS_SUCCESS;
        break;
    }
    default:
        break;
    }

    WdfRequestCompleteWithInformation(Request, status, bytesReturned);
}
