/*---------------------------------------------------------------------------*\
| Smbus.h — bounded SMBus transaction entry point for BrokerSmbus.          |
\*---------------------------------------------------------------------------*/
#pragma once

#include <ntddk.h>
#include "SmbusController.h"

/* Validates the request, applies the in-kernel guard, and dispatches to the
   detected vendor backend. Returns an BROKER_SMBUS_STATUS. */
UINT32 BrokerSmbusXfer(
    _In_  const SMBUS_CONTROLLER*           Controller,
    _In_  const BROKER_SMBUS_XFER_REQUEST* Req,
    _Out_       BROKER_SMBUS_XFER_RESPONSE* Resp);

/* Validates a WRITE request, applies the in-kernel brick-guard (RGB address range
   only — SPD and everything else refused), and dispatches to the vendor backend. */
UINT32 BrokerSmbusWrite(
    _In_ const SMBUS_CONTROLLER*            Controller,
    _In_ const BROKER_SMBUS_WRITE_REQUEST* Req);
