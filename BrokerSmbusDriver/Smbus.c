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

/* DEVICE-AWARE BRICK-GUARD. Writes are allowed ONLY to the address window(s) of the
   DEVICE CLASS the request names; SPD (0x50-0x57) and every other address are refused
   here, in the kernel, no matter what the broker sends. Each class permits ONLY its own
   window — a DRAM-RGB write can never reach a GPU's address, and an unknown class is
   refused outright. This is strictly tighter than one shared window. The per-device maps
   live in this SIGNED table, never in data (same rule as the sensor register maps).
   NOTE: the guard constrains the device ADDRESS, not the Command (register) or data —
   within an allowed RGB controller any register/value is writable; the broker's baked
   RgbCatalog bounds which registers are actually written. */
typedef struct _RGB_WRITE_PROFILE
{
    UINT32 ClassId;     /* BROKER_RGB_WRITE_CLASS                          */
    UINT32 AddrMin;     /* primary allowed 7-bit SMBus address window      */
    UINT32 AddrMax;
    UINT32 AddrMin2;    /* optional second window (AddrMax2 == 0 => unused) */
    UINT32 AddrMax2;
} RGB_WRITE_PROFILE;

static const RGB_WRITE_PROFILE g_RgbWriteProfiles[] =
{
    /* ENE/Aura DRAM keeps both legacy windows (standard 0x70-0x77 + this board's 0x39/0x3A). */
    { BrokerRgbClassEneDram,     BROKER_SMBUS_RGB_ADDR_MIN,        BROKER_SMBUS_RGB_ADDR_MAX,
                                 BROKER_SMBUS_DRAM_ADDR_MIN,       BROKER_SMBUS_DRAM_ADDR_MAX },
    { BrokerRgbClassCorsairDram, BROKER_RGB_CORSAIR_DRAM_ADDR_MIN, BROKER_RGB_CORSAIR_DRAM_ADDR_MAX,
                                 0u,                               0u },
    { BrokerRgbClassCrucialDram, BROKER_RGB_CRUCIAL_A_MIN,         BROKER_RGB_CRUCIAL_A_MAX,
                                 BROKER_RGB_CRUCIAL_B_MIN,         BROKER_RGB_CRUCIAL_B_MAX },
    { BrokerRgbClassHyperXDram,  BROKER_RGB_HYPERX_ADDR,           BROKER_RGB_HYPERX_ADDR,
                                 0u,                               0u },
    { BrokerRgbClassFuryDram,    BROKER_RGB_FURY_ADDR_MIN,         BROKER_RGB_FURY_ADDR_MAX,
                                 0u,                               0u },
    { BrokerRgbClassViperDram,   BROKER_RGB_VIPER_ADDR,            BROKER_RGB_VIPER_ADDR,
                                 0u,                               0u },
    { BrokerRgbClassXtreemDram,  BROKER_RGB_XTREEM_A_MIN,          BROKER_RGB_XTREEM_A_MAX,
                                 BROKER_RGB_XTREEM_B_MIN,          BROKER_RGB_XTREEM_B_MAX },
    { BrokerRgbClassCorsairVenDram, BROKER_RGB_CORSAIR_VEN_MIN,    BROKER_RGB_CORSAIR_VEN_MAX,
                                 0u,                               0u },
    { BrokerRgbClassAsrockMb,    BROKER_RGB_ASROCK_ADDR,           BROKER_RGB_ASROCK_ADDR,
                                 0u,                               0u },
    { BrokerRgbClassEvgaMb,      BROKER_RGB_EVGA_ADDR,             BROKER_RGB_EVGA_ADDR,
                                 0u,                               0u },
};

static BOOLEAN BrokerSmbusWriteAddressAllowed(_In_ UINT32 DeviceClass, _In_ UINT32 Address)
{
    ULONG i;
    for (i = 0; i < ARRAYSIZE(g_RgbWriteProfiles); ++i)
    {
        const RGB_WRITE_PROFILE* p = &g_RgbWriteProfiles[i];
        if (p->ClassId != DeviceClass)
            continue;

        if (Address >= p->AddrMin && Address <= p->AddrMax)
            return TRUE;
        if (p->AddrMax2 != 0u && Address >= p->AddrMin2 && Address <= p->AddrMax2)
            return TRUE;
        return FALSE;   /* class matched but the address is outside its window */
    }
    return FALSE;       /* unknown device class — refuse */
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
    if (!BrokerSmbusWriteAddressAllowed(Req->DeviceClass, Req->Address)) return BrokerSmbusForbidden;
    if (Controller->Vendor == SmbusVendorUnknown)           return BrokerSmbusNotImplemented;

    return SmbusWriteXfer(Controller, Req);
}
