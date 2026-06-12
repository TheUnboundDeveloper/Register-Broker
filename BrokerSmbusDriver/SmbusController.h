/*---------------------------------------------------------------------------*\
| SmbusController.h                                                          |
|                                                                            |
|   Internal driver model for the detected SMBus host controller and the     |
|   vendor-dispatch interface. The user-mode contract (IOCTL) is             |
|   vendor-agnostic; this is where the driver picks Intel i801 vs AMD FCH.   |
\*---------------------------------------------------------------------------*/
#pragma once

#include <ntddk.h>
#include "inc/SmbusBrokerProtocol.h"

typedef enum _SMBUS_VENDOR
{
    SmbusVendorUnknown = BROKER_SMBUS_VENDOR_UNKNOWN,
    SmbusVendorIntel   = BROKER_SMBUS_VENDOR_INTEL,
    SmbusVendorAmd     = BROKER_SMBUS_VENDOR_AMD
} SMBUS_VENDOR;

#define SMBUS_MAX_BUSES 8

/* One addressable SMBus segment. AMD FCH multiplexes several segments onto one
   controller (PortSelect); Intel i801 typically has a single segment. */
typedef struct _SMBUS_BUS
{
    USHORT IoBase;       /* SMBus I/O base for this segment            */
    UCHAR  PortSelect;   /* AMD FCH port-select value; unused on Intel */
} SMBUS_BUS;

/* Defined below; the controller records which backend claimed it. */
struct _SMBUS_BACKEND_DESCRIPTOR;

typedef struct _SMBUS_CONTROLLER
{
    SMBUS_VENDOR Vendor;
    ULONG        PciDevice;
    ULONG        PciFunction;
    USHORT       PciVendorId;
    USHORT       PciDeviceId;
    UCHAR        PciRevision;              /* PCI revision id (gates AMD KERNCZ) */
    ULONG        BusCount;                 /* usable entries in Buses[] */
    SMBUS_BUS    Buses[SMBUS_MAX_BUSES];
    BOOLEAN      ReadImplemented;          /* set TRUE once the vendor read path is wired */
    BOOLEAN      SmuAvailable;             /* TRUE on AMD Family 17h+ (CPU temp via SMN)  */
    UCHAR        CpuFamily;                 /* CPUID effective family (e.g. 0x17, 0x19)    */
    UCHAR        CpuModel;                  /* CPUID effective model (selects CCD offset)  */
    UINT32       SmuCcdOffset;              /* k10temp per-model CCD temp offset; 0 = none */
    BOOLEAN      SuperioAvailable;          /* TRUE when a supported Super-I/O was found     */
    USHORT       SuperioBase;               /* EC base I/O port (from LD-HWM / LD-EC)        */
    USHORT       SuperioChipId;             /* detected SIO chip id (e.g. 0xD592, 0x8688)   */
    UCHAR        SuperioKind;               /* BROKER_SUPERIO_KIND_NCT / _ITE / _NONE      */
    const struct _SMBUS_BACKEND_DESCRIPTOR* Backend;   /* SMBus backend that claimed the
                                               controller at detect; NULL if none */
} SMBUS_CONTROLLER;

/*---------------------------------------------------------------------------*\
| Backend registry (the Phase-3 detector registry).                           |
|                                                                             |
|   Detection and dispatch are table-driven: adding a backend = one source    |
|   file + one descriptor row. Table order IS detection order. The tables     |
|   live next to their dispatch (SMBus: SmbusDetect.c; Super-I/O:             |
|   SuperioNct.c) and are also what IOCTL_BROKER_ENUM_BACKENDS reports.       |
\*---------------------------------------------------------------------------*/

/* An SMBus host-controller backend. Claimed by PCI vendor id of the SMBus-class
   device; Vendor is the wire value INFO reports for it. */
typedef struct _SMBUS_BACKEND_DESCRIPTOR
{
    const CHAR*   Name;                     /* short ASCII name (ENUM_BACKENDS)      */
    SMBUS_VENDOR  Vendor;                   /* BROKER_SMBUS_VENDOR_* reported by INFO */
    const USHORT* PciVendorIds;             /* PCI vendor ids this backend claims     */
    ULONG         PciVendorIdCount;
    BOOLEAN       WriteImplemented;         /* advertises BROKER_SMBUS_CAP_WRITE      */
    NTSTATUS (*DiscoverBuses)(_Inout_ SMBUS_CONTROLLER* Controller);
    UINT32   (*Read)(_In_ const SMBUS_CONTROLLER*, _In_ const BROKER_SMBUS_XFER_REQUEST*, _Out_ BROKER_SMBUS_XFER_RESPONSE*);
    UINT32   (*Write)(_In_ const SMBUS_CONTROLLER*, _In_ const BROKER_SMBUS_WRITE_REQUEST*);
} SMBUS_BACKEND_DESCRIPTOR;

/* A Super-I/O sensor backend. Detect probes the SIO chip id and claims the
   controller on a gate match (it MUST no-op if SuperioAvailable is already set);
   Kind is the wire value its reads dispatch on. */
typedef struct _SUPERIO_BACKEND_DESCRIPTOR
{
    const CHAR* Name;                       /* short ASCII name (ENUM_BACKENDS) */
    UCHAR       Kind;                       /* BROKER_SUPERIO_KIND_* it claims  */
    VOID   (*Detect)(_Inout_ SMBUS_CONTROLLER* Controller);
    UINT32 (*Read)(_In_ const SMBUS_CONTROLLER*, _In_ UINT32 Kind, _In_ UINT32 Index, _Out_ UINT32* Raw);
} SUPERIO_BACKEND_DESCRIPTOR;

/* The registries (SmbusDetect.c / SuperioNct.c). */
extern const SMBUS_BACKEND_DESCRIPTOR   g_SmbusBackends[];
extern const ULONG                      g_SmbusBackendCount;
extern const SUPERIO_BACKEND_DESCRIPTOR g_SuperioBackends[];
extern const ULONG                      g_SuperioBackendCount;

/* One-time detection: scan PCI, identify the vendor, discover base(s). Safe to
   call at DriverEntry. Leaves Vendor = Unknown if no SMBus controller is found. */
NTSTATUS SmbusDetectController(_Out_ SMBUS_CONTROLLER* Controller);

/* Run every registered Super-I/O probe in table order; first claim wins (a board
   has one Super-I/O HWM, so later probes are skipped once one claims the chip). */
VOID SuperioDetectAll(_Inout_ SMBUS_CONTROLLER* Controller);

/* Vendor-dispatched bounded read. Returns an BROKER_SMBUS_STATUS value. */
UINT32 SmbusReadXfer(
    _In_  const SMBUS_CONTROLLER*           Controller,
    _In_  const BROKER_SMBUS_XFER_REQUEST* Req,
    _Out_       BROKER_SMBUS_XFER_RESPONSE* Resp);

/* Vendor-dispatched bounded write (byte/word). Address already brick-guarded upstream. */
UINT32 SmbusWriteXfer(
    _In_ const SMBUS_CONTROLLER*            Controller,
    _In_ const BROKER_SMBUS_WRITE_REQUEST* Req);

/* ---- Vendor backends (SmbusAmd.c / SmbusIntel.c) ---- */
NTSTATUS SmbusAmdDiscoverBuses(_Inout_ SMBUS_CONTROLLER* Controller);
UINT32   SmbusAmdRead(_In_ const SMBUS_CONTROLLER*, _In_ const BROKER_SMBUS_XFER_REQUEST*, _Out_ BROKER_SMBUS_XFER_RESPONSE*);
UINT32   SmbusAmdWrite(_In_ const SMBUS_CONTROLLER*, _In_ const BROKER_SMBUS_WRITE_REQUEST*);

NTSTATUS SmbusIntelDiscoverBuses(_Inout_ SMBUS_CONTROLLER* Controller);
UINT32   SmbusIntelRead(_In_ const SMBUS_CONTROLLER*, _In_ const BROKER_SMBUS_XFER_REQUEST*, _Out_ BROKER_SMBUS_XFER_RESPONSE*);
UINT32   SmbusIntelWrite(_In_ const SMBUS_CONTROLLER*, _In_ const BROKER_SMBUS_WRITE_REQUEST*);

/* ---- AMD SMU sensor backend (SmuAmd.c) ---- */
/* Detect AMD Family 17h+ via CPUID and set Controller->SmuAvailable / CpuFamily. */
VOID   SmuAmdDetect(_Inout_ SMBUS_CONTROLLER* Controller);
/* Read a baked-in SMU sensor's raw SMN register. Returns BROKER_SMBUS_STATUS. */
UINT32 SmuAmdRead(_In_ const SMBUS_CONTROLLER* Controller, _In_ UINT32 Sensor, _Out_ UINT32* Raw);

/* ---- Super-I/O (NCT668x EC family: 6683/6686/6687D) sensor backend (SuperioNct.c) ---- */
/* Probe LPC 0x2E/0x4E for an NCT668x; set Controller->SuperioAvailable / Base / ChipId / Kind. */
VOID   SuperioNctDetect(_Inout_ SMBUS_CONTROLLER* Controller);
/* Read a baked-in Super-I/O sensor (Kind, Index) raw value. Returns BROKER_SMBUS_STATUS. */
UINT32 SuperioNctRead(_In_ const SMBUS_CONTROLLER* Controller, _In_ UINT32 Kind, _In_ UINT32 Index, _Out_ UINT32* Raw);

/* ---- Super-I/O (NCT6775 "classic" family, bank-select) backend (SuperioNct6775.c) ---- */
/* Probe LPC 0x2E/0x4E for a modern NCT6775-family chip (6779/6791..6798); no-ops if an
   earlier backend already claimed a chip. Read-only except the documented one-bit
   IO-space-lock clear on NCT6791+ (see docs/SUPERIO-NCT6775-FAMILY.md). */
VOID   SuperioNct6775Detect(_Inout_ SMBUS_CONTROLLER* Controller);
UINT32 SuperioNct6775Read(_In_ const SMBUS_CONTROLLER* Controller, _In_ UINT32 Kind, _In_ UINT32 Index, _Out_ UINT32* Raw);

/* The ITE IT87xx backend (SuperioIte.c) was retired 2026-06-11 after expert
   corrections (design record: docs/GIGABYTE-SUPPORT.md). BROKER_SUPERIO_KIND_ITE
   stays reserved in the protocol header so the wire value is never reused. */

/* Dispatch a Super-I/O read to whichever backend was detected. Implemented in
   SuperioNct.c. Driver.c calls this so the IOCTL handler stays backend-agnostic. */
UINT32 SuperioReadDispatch(_In_ const SMBUS_CONTROLLER* Controller, _In_ UINT32 Kind, _In_ UINT32 Index, _Out_ UINT32* Raw);
