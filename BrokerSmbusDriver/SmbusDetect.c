/*---------------------------------------------------------------------------*\
| SmbusDetect.c — chipset auto-detection + vendor dispatch                    |
|                                                                            |
|   Scans PCI bus 0 for the SMBus host controller (PCI class 0x0C0500),       |
|   identifies the vendor from the PCI vendor id, and hands off to the        |
|   vendor backend to discover the I/O base(s). PCI config reads are          |
|   read-only and low risk; the vendor-specific base discovery and the actual |
|   transaction are in SmbusAmd.c / SmbusIntel.c.                            |
\*---------------------------------------------------------------------------*/
#include "SmbusController.h"

/* PCI base-class 0x0C (serial bus), sub-class 0x05 (SMBus). */
#define PCI_SMBUS_CLASSCODE 0x0C0500u

static SMBUS_VENDOR SmbusVendorFromPciId(USHORT VendorId)
{
    switch (VendorId)
    {
        case 0x8086: return SmbusVendorIntel;   /* Intel            */
        case 0x1022: return SmbusVendorAmd;     /* AMD              */
        case 0x1002: return SmbusVendorAmd;     /* ATI/AMD (older FCH) */
        default:     return SmbusVendorUnknown;
    }
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

            Controller->Vendor      = SmbusVendorFromPciId(vid);
            Controller->PciDevice   = dev;
            Controller->PciFunction = fn;
            Controller->PciVendorId = vid;
            Controller->PciDeviceId = did;
            Controller->PciRevision = (UCHAR)(classReg & 0xFF);

            switch (Controller->Vendor)
            {
                case SmbusVendorAmd:   return SmbusAmdDiscoverBuses(Controller);
                case SmbusVendorIntel: return SmbusIntelDiscoverBuses(Controller);
                default:               return STATUS_NOT_SUPPORTED;
            }
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

    switch (Controller->Vendor)
    {
        case SmbusVendorAmd:   return SmbusAmdRead(Controller, Req, Resp);
        case SmbusVendorIntel: return SmbusIntelRead(Controller, Req, Resp);
        default:               return BrokerSmbusNotImplemented;
    }
}

UINT32 SmbusWriteXfer(const SMBUS_CONTROLLER* Controller,
                      const BROKER_SMBUS_WRITE_REQUEST* Req)
{
    if (Req->BusIndex >= Controller->BusCount)
        return BrokerSmbusBadRequest;

    switch (Controller->Vendor)
    {
        case SmbusVendorAmd:   return SmbusAmdWrite(Controller, Req);
        case SmbusVendorIntel: return SmbusIntelWrite(Controller, Req);
        default:               return BrokerSmbusNotImplemented;
    }
}
