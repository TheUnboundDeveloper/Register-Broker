/*---------------------------------------------------------------------------*\
| Smbus.c — BrokerSmbus transaction front-end                               |
|                                                                             |
|   Vendor-agnostic validation + an in-kernel address guard (defense in       |
|   depth: the driver never assumes the broker is honest), then dispatch to   |
|   the auto-detected vendor backend (SmbusDetect.c -> SmbusAmd.c /           |
|   SmbusIntel.c). The vendor read sequences are the parts that touch the     |
|   bus; they are stubbed until validated on hardware (see the BRINGUP docs). |
\*---------------------------------------------------------------------------*/
#include "Smbus.h"

/* In-kernel guard. Phase B is read-only and limited to the 7-bit address
   space. Tighten this to a configured RGB allowlist when controller access
   lands, so a compromised broker still cannot read arbitrary devices. */
static BOOLEAN BrokerSmbusAddressAllowed(_In_ UINT32 Address)
{
    return (Address <= 0x7F);
}

UINT32 BrokerSmbusXfer(const SMBUS_CONTROLLER* Controller,
                        const BROKER_SMBUS_XFER_REQUEST* Req,
                        BROKER_SMBUS_XFER_RESPONSE* Resp)
{
    if (Req->Version != BROKER_SMBUS_PROTOCOL_VERSION) return BrokerSmbusBadRequest;
    if (Req->Op > BrokerSmbusReadBlock)                return BrokerSmbusBadRequest;
    if (Req->Length > BROKER_SMBUS_MAX_BLOCK)          return BrokerSmbusBadRequest;
    if (Req->BusIndex >= Controller->BusCount)          return BrokerSmbusBadRequest;  /* bound the array index here, not just in the backend */
    if (!BrokerSmbusAddressAllowed(Req->Address))      return BrokerSmbusForbidden;
    if (Controller->Vendor == SmbusVendorUnknown)       return BrokerSmbusNotImplemented;

    return SmbusReadXfer(Controller, Req, Resp);
}

/* BRICK-GUARD. Writes are allowed ONLY to the RGB controller address window; SPD
   (0x50-0x57) and every other address are refused here, in the kernel, no matter
   what the broker sends. This is the hard safety boundary for the write path.
   NOTE: the guard constrains the device ADDRESS, not the Command (register) or data —
   within an allowed RGB controller any register/value is writable. The broker's baked
   RgbCatalog is what bounds which registers are actually written; if a per-address
   register allowlist is ever needed, add it here (mirroring the named-sensor pattern). */
static BOOLEAN BrokerSmbusWriteAddressAllowed(_In_ UINT32 Address)
{
    return (Address >= BROKER_SMBUS_RGB_ADDR_MIN  && Address <= BROKER_SMBUS_RGB_ADDR_MAX) ||
           (Address >= BROKER_SMBUS_DRAM_ADDR_MIN && Address <= BROKER_SMBUS_DRAM_ADDR_MAX);
}

UINT32 BrokerSmbusWrite(const SMBUS_CONTROLLER* Controller,
                         const BROKER_SMBUS_WRITE_REQUEST* Req)
{
    if (Req->Version != BROKER_SMBUS_PROTOCOL_VERSION)      return BrokerSmbusBadRequest;
    if (Req->Op != BrokerSmbusWriteByte &&
        Req->Op != BrokerSmbusWriteWord &&
        Req->Op != BrokerSmbusWriteBlock)                  return BrokerSmbusBadRequest;
    if (Req->Op == BrokerSmbusWriteBlock &&
        (Req->Length == 0 ||
         Req->Length > BROKER_SMBUS_MAX_BLOCK))            return BrokerSmbusBadRequest;  /* bound Block[] use in the backends */
    if (Req->BusIndex >= Controller->BusCount)              return BrokerSmbusBadRequest;  /* bound the array index here too */
    if (!BrokerSmbusWriteAddressAllowed(Req->Address))     return BrokerSmbusForbidden;
    if (Controller->Vendor == SmbusVendorUnknown)           return BrokerSmbusNotImplemented;

    return SmbusWriteXfer(Controller, Req);
}
