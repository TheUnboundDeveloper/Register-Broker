/*---------------------------------------------------------------------------*\
| SmbusDetect.c — chipset auto-detection + backend registry/dispatch          |
|                                                                            |
|   Scans PCI bus 0 for the SMBus host controller (PCI class 0x0C0500),       |
|   matches the PCI vendor id against the backend registry below, and hands   |
|   off to the matched backend to discover the I/O base(s). PCI config reads  |
|   are read-only and low risk; the vendor-specific base discovery and the    |
|   actual transaction are in SmbusAmd.c / SmbusIntel.c.                     |
|                                                                            |
|   Adding an SMBus host backend = one SmbusXxx.c file + one descriptor row   |
|   in g_SmbusBackends. Table order is match order.                           |
\*---------------------------------------------------------------------------*/
#include "SmbusController.h"

/* PCI base-class 0x0C (serial bus), sub-class 0x05 (SMBus). */
#define PCI_SMBUS_CLASSCODE 0x0C0500u

/*-- The SMBus host-controller backend registry. --*/

static const USHORT g_AmdPciVendorIds[]   = { 0x1022, 0x1002 };   /* AMD, ATI/AMD (older FCH) */
static const USHORT g_IntelPciVendorIds[] = { 0x8086 };

/* WriteImplemented: only the AMD path's bounded brick-guarded write is validated;
   Intel's write returns NotImplemented, so it must not advertise CAP_WRITE. */
const SMBUS_BACKEND_DESCRIPTOR g_SmbusBackends[] =
{
    { "AMD FCH",    SmbusVendorAmd,   g_AmdPciVendorIds,   RTL_NUMBER_OF(g_AmdPciVendorIds),
      TRUE,  SmbusAmdDiscoverBuses,   SmbusAmdRead,   SmbusAmdWrite   },
    { "Intel i801", SmbusVendorIntel, g_IntelPciVendorIds, RTL_NUMBER_OF(g_IntelPciVendorIds),
      FALSE, SmbusIntelDiscoverBuses, SmbusIntelRead, SmbusIntelWrite },
};
const ULONG g_SmbusBackendCount = RTL_NUMBER_OF(g_SmbusBackends);

static const SMBUS_BACKEND_DESCRIPTOR* SmbusBackendFromPciId(USHORT VendorId)
{
    ULONG i, v;
    for (i = 0; i < g_SmbusBackendCount; i++)
        for (v = 0; v < g_SmbusBackends[i].PciVendorIdCount; v++)
            if (g_SmbusBackends[i].PciVendorIds[v] == VendorId)
                return &g_SmbusBackends[i];
    return NULL;
}

NTSTATUS SmbusDetectController(SMBUS_CONTROLLER* Controller)
{
    RtlZeroMemory(Controller, sizeof(*Controller));
    Controller->Vendor = SmbusVendorUnknown;

    for (ULONG dev = 0; dev < 32; dev++)
    {
        for (ULONG fn = 0; fn < 8; fn++)
        {
            PCI_SLOT_NUMBER slot;
            slot.u.AsULONG = 0;
            slot.u.bits.DeviceNumber   = dev;
            slot.u.bits.FunctionNumber = fn;

            ULONG ids = 0;
            ULONG got = HalGetBusDataByOffset(PCIConfiguration, 0, slot.u.AsULONG, &ids, 0, sizeof(ids));
            if (got != sizeof(ids))
                continue;

            USHORT vid = (USHORT)(ids & 0xFFFF);
            USHORT did = (USHORT)(ids >> 16);
            if (vid == 0xFFFF)
                continue;                          /* no device in this slot */

            /* Config dword at 0x08: byte 0 = revision id, bytes 1..3 = class code. */
            ULONG classReg = 0;
            HalGetBusDataByOffset(PCIConfiguration, 0, slot.u.AsULONG, &classReg, 0x08, sizeof(classReg));
            if ((classReg >> 8) != PCI_SMBUS_CLASSCODE)
                continue;                          /* not the SMBus controller */

            Controller->Backend     = SmbusBackendFromPciId(vid);
            Controller->Vendor      = Controller->Backend ? Controller->Backend->Vendor
                                                          : SmbusVendorUnknown;
            Controller->PciDevice   = dev;
            Controller->PciFunction = fn;
            Controller->PciVendorId = vid;
            Controller->PciDeviceId = did;
            Controller->PciRevision = (UCHAR)(classReg & 0xFF);

            return Controller->Backend ? Controller->Backend->DiscoverBuses(Controller)
                                       : STATUS_NOT_SUPPORTED;
        }
    }

    return STATUS_NOT_FOUND;
}

UINT32 SmbusReadXfer(const SMBUS_CONTROLLER* Controller,
                     const BROKER_SMBUS_XFER_REQUEST* Req,
                     BROKER_SMBUS_XFER_RESPONSE* Resp)
{
    if (Req->BusIndex >= Controller->BusCount)
        return BrokerSmbusBadRequest;

    return Controller->Backend ? Controller->Backend->Read(Controller, Req, Resp)
                               : BrokerSmbusNotImplemented;
}

UINT32 SmbusWriteXfer(const SMBUS_CONTROLLER* Controller,
                      const BROKER_SMBUS_WRITE_REQUEST* Req)
{
    if (Req->BusIndex >= Controller->BusCount)
        return BrokerSmbusBadRequest;

    return Controller->Backend ? Controller->Backend->Write(Controller, Req)
                               : BrokerSmbusNotImplemented;
}
