/*---------------------------------------------------------------------------*\
| SmbusIntel.c — Intel ICH/PCH (i801) SMBus backend                          |
|                                                                            |
|   Read-only SMBus transactions for the Intel i801 host controller. Like    |
|   the AMD backend, encodings are PORTED, NOT INVENTED — from Linux          |
|   drivers/i2c/busses/i2c-i801.c (and corroborated by BRINGUP-i801.md).      |
|                                                                            |
|   Differences from the AMD/PIIX4 path:                                      |
|     * I/O base IS a PCI BAR (BAR4), not a PM-block register.                |
|     * Completion is signalled by the INTR status bit (0x02) going high      |
|       with HOST_BUSY low — not merely HOST_BUSY clearing.                   |
|     * Block reads use the 32-byte buffer (SMBAUXCTL E32B): read the count   |
|       from DAT0 then stream the bytes out of SMBBLKDAT.                     |
|     * Single segment (BusCount = 1); no port mux.                           |
|                                                                            |
|   NOT yet validated on Intel hardware — bring up against SPD (0x50) per     |
|   BRINGUP-i801.md before trusting it.                                       |
\*---------------------------------------------------------------------------*/
#include "SmbusController.h"

/*---------------------------------------------------------------------------*\
| PCI config: BAR4 holds the I/O base; HSTCFG bit0 is the host-enable flag.  |
\*---------------------------------------------------------------------------*/
#define I801_PCI_BAR4_OFFSET     0x20        /* BAR4 = config 0x10 + 4*4     */
#define I801_PCI_HSTCFG_OFFSET   0x40
#define I801_HSTCFG_HST_EN       0x01
#define I801_IO_BASE_MASK        0xFFE0      /* SMBus I/O region is 32B aligned */
#define I801_BAR_IO_INDICATOR    0x01        /* low bit set => I/O space BAR  */

/*---------------------------------------------------------------------------*\
| SMBus host-controller registers (offset from the I/O base).                |
\*---------------------------------------------------------------------------*/
#define SMBHSTSTS                0x00
#define SMBHSTCNT                0x02
#define SMBHSTCMD                0x03
#define SMBHSTADD                0x04
#define SMBHSTDAT0               0x05
#define SMBHSTDAT1               0x06
#define SMBBLKDAT                0x07
#define SMBAUXCTL                0x0D

/* SMBHSTSTS bits. */
#define SMBHSTSTS_HOST_BUSY      0x01
#define SMBHSTSTS_INTR           0x02
#define SMBHSTSTS_DEV_ERR        0x04
#define SMBHSTSTS_BUS_ERR        0x08
#define SMBHSTSTS_FAILED         0x10
#define STATUS_ERROR_FLAGS       (SMBHSTSTS_FAILED | SMBHSTSTS_BUS_ERR | SMBHSTSTS_DEV_ERR) /* 0x1C */
#define STATUS_CLEAR_ALL         0xFF        /* write-1-clear all status bits */

/* SMBHSTCNT bits. */
#define SMBHSTCNT_KILL           0x02
#define SMBHSTCNT_START          0x40

/* SMBHSTCNT protocol/size field (bits [4:2]). */
#define I801_BYTE_DATA           0x08
#define I801_WORD_DATA           0x0C
#define I801_BLOCK_DATA          0x14

/* SMBAUXCTL bits. */
#define SMBAUXCTL_E32B           0x02        /* 32-byte block buffer mode     */

/* Bounded poll: 250 us per step, up to 200 steps (~50 ms) before BusError. */
#define SMBUS_POLL_STEP_US       250u
#define SMBUS_POLL_MAX_STEPS     200u

/*---------------------------------------------------------------------------*\
| Serialization — see the AMD backend for the rationale. Intel has no port    |
| mux, but the SMBus is still shared with ACPI/EC, so transactions are        |
| serialized through a dispatcher mutex (PASSIVE_LEVEL so the poll may sleep).|
| We deliberately do NOT use the INUSE_STS hardware semaphore: it is          |
| unreliable across PCHs and Linux i2c-i801 abandoned it.                     |
\*---------------------------------------------------------------------------*/
static KMUTEX  g_IntelBusLock;
static BOOLEAN g_IntelBusLockReady = FALSE;

static __forceinline UCHAR HstRead(USHORT Base, USHORT Reg)
{
    return READ_PORT_UCHAR((PUCHAR)(ULONG_PTR)(Base + Reg));
}

static __forceinline VOID HstWrite(USHORT Base, USHORT Reg, UCHAR Value)
{
    WRITE_PORT_UCHAR((PUCHAR)(ULONG_PTR)(Base + Reg), Value);
}

static VOID SmbusSleepUs(ULONG Microseconds)
{
    LARGE_INTEGER interval;
    interval.QuadPart = -(LONGLONG)Microseconds * 10;   /* relative, 100 ns units */
    KeDelayExecutionThread(KernelMode, FALSE, &interval);
}

static ULONG IntelPciRead(const SMBUS_CONTROLLER* Controller, ULONG Offset, PVOID Buffer, ULONG Length)
{
    PCI_SLOT_NUMBER slot;
    slot.u.AsULONG = 0;
    slot.u.bits.DeviceNumber   = Controller->PciDevice;
    slot.u.bits.FunctionNumber = Controller->PciFunction;
    return HalGetBusDataByOffset(PCIConfiguration, 0, slot.u.AsULONG, Buffer, Offset, Length);
}

/*---------------------------------------------------------------------------*\
| Run one i801 transaction: clear status, program ADD/CMD, set protocol +     |
| START, poll for INTR (or error) with HOST_BUSY low, bounded. Reads only.    |
\*---------------------------------------------------------------------------*/
static UINT32 SmbusIntelTransaction(USHORT Base, UCHAR Address, UCHAR Command, UCHAR SizeProto)
{
    UCHAR status;
    ULONG step;

    /* Clear stale status, then require the host to be idle. */
    HstWrite(Base, SMBHSTSTS, STATUS_CLEAR_ALL);
    status = HstRead(Base, SMBHSTSTS);
    if (status & SMBHSTSTS_HOST_BUSY)
        return BrokerSmbusBusError;            /* bus busy — do not fight it */

    HstWrite(Base, SMBHSTADD, (UCHAR)((Address << 1) | 0x01));
    HstWrite(Base, SMBHSTCMD, Command);

    /* Start. INTREN stays off — we poll. */
    HstWrite(Base, SMBHSTCNT, (UCHAR)(SizeProto | SMBHSTCNT_START));

    for (step = 0; step < SMBUS_POLL_MAX_STEPS; step++)
    {
        SmbusSleepUs(SMBUS_POLL_STEP_US);
        status = HstRead(Base, SMBHSTSTS);
        if ((status & SMBHSTSTS_HOST_BUSY) == 0 &&
            (status & (SMBHSTSTS_INTR | STATUS_ERROR_FLAGS)))
            break;
    }

    if (step >= SMBUS_POLL_MAX_STEPS)
    {
        HstWrite(Base, SMBHSTCNT, SMBHSTCNT_KILL);          /* abort */
        HstWrite(Base, SMBHSTSTS, STATUS_CLEAR_ALL);
        return BrokerSmbusBusError;
    }

    if (status & STATUS_ERROR_FLAGS)
    {
        HstWrite(Base, SMBHSTSTS, STATUS_CLEAR_ALL);
        return BrokerSmbusBusError;
    }

    return BrokerSmbusOk;
}

NTSTATUS SmbusIntelDiscoverBuses(SMBUS_CONTROLLER* Controller)
{
    ULONG  bar4 = 0, cfg = 0;
    USHORT base;

    Controller->BusCount        = 0;
    Controller->ReadImplemented = FALSE;

    if (!g_IntelBusLockReady)
    {
        KeInitializeMutex(&g_IntelBusLock, 0);
        g_IntelBusLockReady = TRUE;
    }

    /* Host must be enabled; do not modify the config register. */
    if (IntelPciRead(Controller, I801_PCI_HSTCFG_OFFSET, &cfg, sizeof(cfg)) != sizeof(cfg))
        return STATUS_SUCCESS;
    if ((cfg & I801_HSTCFG_HST_EN) == 0)
        return STATUS_SUCCESS;

    /* I/O base = BAR4. Require an I/O-space BAR and a plausible address. */
    if (IntelPciRead(Controller, I801_PCI_BAR4_OFFSET, &bar4, sizeof(bar4)) != sizeof(bar4))
        return STATUS_SUCCESS;
    if ((bar4 & I801_BAR_IO_INDICATOR) == 0)
        return STATUS_SUCCESS;                  /* not an I/O BAR */

    base = (USHORT)(bar4 & I801_IO_BASE_MASK);
    if (base == 0x0000 || base == (0xFFFF & I801_IO_BASE_MASK))
        return STATUS_SUCCESS;

    Controller->Buses[0].IoBase     = base;
    Controller->Buses[0].PortSelect = 0;        /* unused on Intel */
    Controller->BusCount            = 1;
    Controller->ReadImplemented     = TRUE;
    return STATUS_SUCCESS;
}

UINT32 SmbusIntelRead(const SMBUS_CONTROLLER* Controller,
                      const BROKER_SMBUS_XFER_REQUEST* Req,
                      BROKER_SMBUS_XFER_RESPONSE* Resp)
{
    USHORT base;
    UCHAR  sizeProto;
    UCHAR  auxSaved = 0;
    BOOLEAN isBlock;
    UINT32 status;

    if (!Controller->ReadImplemented || !g_IntelBusLockReady)
        return BrokerSmbusNotImplemented;
    if (Req->BusIndex >= Controller->BusCount)
        return BrokerSmbusBadRequest;

    switch (Req->Op)
    {
        case BrokerSmbusReadByte:  sizeProto = I801_BYTE_DATA;  break;
        case BrokerSmbusReadWord:  sizeProto = I801_WORD_DATA;  break;
        case BrokerSmbusReadBlock: sizeProto = I801_BLOCK_DATA; break;
        default:                    return BrokerSmbusBadRequest;
    }
    isBlock = (Req->Op == BrokerSmbusReadBlock);

    base = Controller->Buses[Req->BusIndex].IoBase;

    KeWaitForSingleObject(&g_IntelBusLock, Executive, KernelMode, FALSE, NULL);

    /* Block reads use the 32-byte buffer; enable E32B and restore it after. */
    if (isBlock)
    {
        auxSaved = HstRead(base, SMBAUXCTL);
        HstWrite(base, SMBAUXCTL, (UCHAR)(auxSaved | SMBAUXCTL_E32B));
    }

    status = SmbusIntelTransaction(base, (UCHAR)Req->Address, (UCHAR)Req->Command, sizeProto);

    if (status == BrokerSmbusOk)
    {
        switch (Req->Op)
        {
            case BrokerSmbusReadByte:
                Resp->Data[0] = HstRead(base, SMBHSTDAT0);
                Resp->Length  = 1;
                break;

            case BrokerSmbusReadWord:
                Resp->Data[0] = HstRead(base, SMBHSTDAT0);
                Resp->Data[1] = HstRead(base, SMBHSTDAT1);
                Resp->Length  = 2;
                break;

            case BrokerSmbusReadBlock:
            {
                UCHAR count = HstRead(base, SMBHSTDAT0);
                if (count == 0 || count > BROKER_SMBUS_MAX_BLOCK)
                {
                    status = BrokerSmbusBusError;
                    break;
                }
                for (UCHAR n = 0; n < count; n++)
                    Resp->Data[n] = HstRead(base, SMBBLKDAT);
                Resp->Length = count;
                break;
            }
        }
    }

    if (isBlock)
        HstWrite(base, SMBAUXCTL, auxSaved);     /* restore E32B state */

    KeReleaseMutex(&g_IntelBusLock, FALSE);

    return status;
}

UINT32 SmbusIntelWrite(const SMBUS_CONTROLLER* Controller,
                       const BROKER_SMBUS_WRITE_REQUEST* Req)
{
    UNREFERENCED_PARAMETER(Controller);
    UNREFERENCED_PARAMETER(Req);
    /* Intel i801 write is intentionally not implemented: the i801 READ path itself
       is not yet hardware-validated, and shipping an unvalidated WRITE is a brick
       risk. Implement (mirroring SmbusIntelRead with rw=0 + DAT before START) only
       once i801 is brought up on real hardware. */
    return BrokerSmbusNotImplemented;
}
