/*---------------------------------------------------------------------------*\
| SmbusAmd.c — AMD FCH (PIIX4 / SB800 / Family 17h+ "KERNCZ") SMBus backend    |
|                                                                            |
|   PRIMARY bring-up target (the dev box is AMD). Read-only SMBus            |
|   transactions for AMD FCH host controllers.                              |
|                                                                            |
|   Encodings are PORTED, NOT INVENTED — they come from the authoritative    |
|   sources named in BRINGUP-AMD-FCH.md:                                     |
|     * Linux drivers/i2c/busses/i2c-piix4.c (base discovery, KERNCZ port    |
|       mux, transaction sequence, error bits)                              |
|     * a second open-source PIIX4 implementation (cross-check: same         |
|       controller registers + the RGB device address windows)               |
|                                                                            |
|   Scope deliberately covers Family 17h/19h (Zen, "KERNCZ"): SMBus I/O base |
|   read from the FCH PM block via 0xCD6/0xCD7 with smb_en = 0x00, and the   |
|   4-way port mux selected through PM index 0x02 (mask 0x18, shift 3).      |
|   Older SB700/SP5100 FCHs use a different smb_en (0x2C) and are left       |
|   unimplemented (BusCount = 0) so the broker never offers smbus:read on    |
|   hardware this file has not been validated against.                      |
\*---------------------------------------------------------------------------*/
#include "SmbusController.h"

/*---------------------------------------------------------------------------*\
| FCH PM index/data port pair and the registers we read through it.          |
|   (Linux: SB800_PIIX4_SMB_IDX 0xcd6; smb_en = 0x00 on Family 17h+.)         |
\*---------------------------------------------------------------------------*/
#define FCH_PM_IDX_PORT          0x0CD6      /* index port  */
#define FCH_PM_DATA_PORT         0x0CD7      /* data port   */

#define FCH_PM_SMBUS_EN          0x00        /* primary smb_en (Family 17h+)         */
#define FCH_PM_SMBUS_EN_BIT      0x10        /* primary: smba_en_lo & 0x10 => enabled */
#define FCH_SMBUS_AUX_OFFSET     0x20u       /* secondary base = primary + 0x20 (0x0B00 -> 0x0B20). */

/*---------------------------------------------------------------------------*\
| KERNCZ gating. This file implements ONLY the Family 17h/19h (Zen) path:     |
| smb_en = 0x00 base discovery + the PM-index-0x02 / mask-0x18 port mux.      |
| Older AMD FCHs (SB700/SB800, smb_en = 0x2C, different mux) must NOT take     |
| this path or base discovery reads the wrong PM register. The device-id +    |
| revision test mirrors Linux i2c-piix4's KERNCZ/Hudson2 detection; anything  |
| else is left unimplemented (BusCount = 0) so the broker never offers         |
| smbus:read on hardware this code has not been validated against.            |
\*---------------------------------------------------------------------------*/
#define FCH_DEV_KERNCZ           0x790B
#define FCH_DEV_HUDSON2          0x780B
#define FCH_REV_KERNCZ_MIN       0x51
#define FCH_REV_HUDSON2_MIN      0x59

static BOOLEAN SmbusAmdIsKerncz(USHORT Device, UCHAR Revision)
{
    return (Device == FCH_DEV_KERNCZ  && Revision >= FCH_REV_KERNCZ_MIN) ||
           (Device == FCH_DEV_HUDSON2 && Revision >= FCH_REV_HUDSON2_MIN);
}

/* KERNCZ (Family 17h/19h) port select: PM index 0x02, bits [4:3]. */
#define FCH_PM_PORT_IDX_KERNCZ   0x02
#define FCH_PM_PORT_MASK_KERNCZ  0x18
#define FCH_PM_PORT_SHIFT_KERNCZ 3
#define FCH_SMBUS_PORT_COUNT     4u          /* 4-way mux on Family 17h+ (<= SMBUS_MAX_BUSES) */

/*---------------------------------------------------------------------------*\
| SMBus host-controller registers (offset from the I/O base). PIIX4/SB800.   |
|   Matches the register map in BRINGUP-AMD-FCH.md §3.                        |
\*---------------------------------------------------------------------------*/
#define SMBHSTSTS                0x00
#define SMBHSTCNT                0x02
#define SMBHSTCMD                0x03
#define SMBHSTADD                0x04
#define SMBHSTDAT0               0x05
#define SMBHSTDAT1               0x06
#define SMBBLKDAT                0x07

/* SMBHSTCNT protocol/size field (bits [4:2]) and START bit. */
#define PIIX4_QUICK              0x00
#define PIIX4_BYTE               0x04
#define PIIX4_BYTE_DATA          0x08
#define PIIX4_WORD_DATA          0x0C
#define PIIX4_BLOCK_DATA         0x14
#define SMBHSTCNT_SIZE_MASK      0x1C
#define SMBHSTCNT_START          0x40

/* SMBHSTSTS bits (read back after a transaction). */
#define SMBHSTSTS_HOST_BUSY      0x01
#define SMBHSTSTS_FAILED         0x10
#define SMBHSTSTS_BUS_COLLISION  0x08
#define SMBHSTSTS_DEV_ERR        0x04
#define SMBHSTSTS_ERROR_MASK     (SMBHSTSTS_FAILED | SMBHSTSTS_BUS_COLLISION | SMBHSTSTS_DEV_ERR)

/* Bounded poll, two phases. FAST: 25 us busy-wait stalls for up to ~2 ms —
   covers a normal byte/word/3-byte-block transaction end-to-end at 100 kHz.
   SLOW fallback: 250 us thread sleeps for up to ~48 ms more before BusError.
   The previous single-phase KeDelayExecutionThread(250 us) loop was the RGB
   "crawl": the kernel rounds sub-tick sleep requests UP to the timer tick
   (1-15.6 ms), costing milliseconds per transaction. KeStallExecutionProcessor
   is exact (busy-wait); we run at PASSIVE_LEVEL so short stalls are safe. */
#define SMBUS_POLL_FAST_STALL_US   25u
#define SMBUS_POLL_FAST_TOTAL_US   2000u
#define SMBUS_POLL_SLOW_STEP_US    250u
#define SMBUS_POLL_SLOW_MAX_STEPS  192u

/*---------------------------------------------------------------------------*\
| Serialization. The IOCTL queue is already sequential (WdfIoQueueDispatch-   |
| Sequential), but the FCH port-select is GLOBAL controller state shared with |
| the SMU/EC, so we also take a dispatcher mutex across select+xfer+restore   |
| and restore the previous port afterwards (mirrors i2c-piix4). KMUTEX keeps   |
| us at PASSIVE_LEVEL so the poll loop may sleep rather than busy-wait.        |
| Initialized once in DiscoverBuses (called single-threaded at DriverEntry).  |
\*---------------------------------------------------------------------------*/
static KMUTEX  g_AmdBusLock;
static BOOLEAN g_AmdBusLockReady = FALSE;

static __forceinline UCHAR PmRead(UCHAR Index)
{
    WRITE_PORT_UCHAR((PUCHAR)(ULONG_PTR)FCH_PM_IDX_PORT, Index);
    return READ_PORT_UCHAR((PUCHAR)(ULONG_PTR)FCH_PM_DATA_PORT);
}

static __forceinline VOID PmWrite(UCHAR Index, UCHAR Value)
{
    WRITE_PORT_UCHAR((PUCHAR)(ULONG_PTR)FCH_PM_IDX_PORT, Index);
    WRITE_PORT_UCHAR((PUCHAR)(ULONG_PTR)FCH_PM_DATA_PORT, Value);
}

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
    /* Relative wait: negative 100 ns units. */
    interval.QuadPart = -(LONGLONG)Microseconds * 10;
    KeDelayExecutionThread(KernelMode, FALSE, &interval);
}

/*---------------------------------------------------------------------------*\
| Poll SMBHSTSTS until HOST_BUSY clears: fast 25 us stalls (~2 ms), then slow  |
| 250 us sleeps (~48 ms). Returns TRUE with the final status in *StatusOut;    |
| FALSE on timeout (caller treats as BusError and clears status).              |
\*---------------------------------------------------------------------------*/
static BOOLEAN SmbusAmdPollIdle(USHORT Base, UCHAR* StatusOut)
{
    UCHAR temp = 0;
    ULONG waited;
    ULONG step;

    for (waited = 0; waited < SMBUS_POLL_FAST_TOTAL_US; waited += SMBUS_POLL_FAST_STALL_US)
    {
        temp = HstRead(Base, SMBHSTSTS);
        if ((temp & SMBHSTSTS_HOST_BUSY) == 0)
        {
            *StatusOut = temp;
            return TRUE;
        }
        KeStallExecutionProcessor(SMBUS_POLL_FAST_STALL_US);
    }

    for (step = 0; step < SMBUS_POLL_SLOW_MAX_STEPS; step++)
    {
        temp = HstRead(Base, SMBHSTSTS);
        if ((temp & SMBHSTSTS_HOST_BUSY) == 0)
        {
            *StatusOut = temp;
            return TRUE;
        }
        SmbusSleepUs(SMBUS_POLL_SLOW_STEP_US);
    }

    *StatusOut = temp;
    return FALSE;
}

/*---------------------------------------------------------------------------*\
| Select an FCH SMBus port (0..3) on Family 17h+ and return the previous      |
| port value so the caller can restore it. Caller holds g_AmdBusLock.         |
\*---------------------------------------------------------------------------*/
static UCHAR SmbusAmdSelectPort(UCHAR Port)
{
    UCHAR cur = PmRead(FCH_PM_PORT_IDX_KERNCZ);
    UCHAR want = (UCHAR)((cur & ~FCH_PM_PORT_MASK_KERNCZ) |
                         ((Port << FCH_PM_PORT_SHIFT_KERNCZ) & FCH_PM_PORT_MASK_KERNCZ));
    if (want != cur)
        PmWrite(FCH_PM_PORT_IDX_KERNCZ, want);
    return cur;
}

static VOID SmbusAmdRestorePort(UCHAR PreviousRegValue)
{
    UCHAR cur = PmRead(FCH_PM_PORT_IDX_KERNCZ);
    if (cur != PreviousRegValue)
        PmWrite(FCH_PM_PORT_IDX_KERNCZ, PreviousRegValue);
}

/*---------------------------------------------------------------------------*\
| Run one PIIX4 transaction on the selected port: ensure the host is idle,    |
| program ADD/CMD, set the protocol + START, poll to completion with a        |
| bounded timeout, then map the error bits. Reads only.                       |
\*---------------------------------------------------------------------------*/
static UINT32 SmbusAmdTransaction(USHORT Base, UCHAR Address, UCHAR Command, UCHAR SizeProto)
{
    UCHAR temp;

    /* Host must be idle. If status is dirty, clear it once and recheck. */
    temp = HstRead(Base, SMBHSTSTS);
    if (temp != 0x00)
    {
        HstWrite(Base, SMBHSTSTS, temp);
        temp = HstRead(Base, SMBHSTSTS);
        if (temp != 0x00)
            return BrokerSmbusBusError;
    }

    /* Address (read) + command/register. */
    HstWrite(Base, SMBHSTADD, (UCHAR)((Address << 1) | 0x01));
    HstWrite(Base, SMBHSTCMD, Command);

    /* Program protocol size, then set START (matches i2c-piix4 two-step). */
    HstWrite(Base, SMBHSTCNT, (UCHAR)(SizeProto & SMBHSTCNT_SIZE_MASK));
    HstWrite(Base, SMBHSTCNT,
             (UCHAR)((HstRead(Base, SMBHSTCNT)) | SMBHSTCNT_START));

    /* Poll HOST_BUSY low, bounded (fast stalls then slow sleeps). */
    if (!SmbusAmdPollIdle(Base, &temp))
    {
        /* Stuck — abandon, leave status cleared, do not fight the bus. */
        HstWrite(Base, SMBHSTSTS, HstRead(Base, SMBHSTSTS));
        return BrokerSmbusBusError;
    }

    if (temp & SMBHSTSTS_ERROR_MASK)
    {
        HstWrite(Base, SMBHSTSTS, temp);   /* clear */
        return BrokerSmbusBusError;
    }

    return BrokerSmbusOk;
}

NTSTATUS SmbusAmdDiscoverBuses(SMBUS_CONTROLLER* Controller)
{
    UCHAR  enLo, enHi;
    USHORT base;
    ULONG  i;

    Controller->BusCount        = 0;
    Controller->ReadImplemented = FALSE;

    /* Only the Zen-era FCH uses the smb_en=0x00 / PM-0x02-mux path below. */
    if (!SmbusAmdIsKerncz(Controller->PciDeviceId, Controller->PciRevision))
        return STATUS_SUCCESS;             /* older/unknown AMD FCH — leave unimplemented */

    if (!g_AmdBusLockReady)
    {
        KeInitializeMutex(&g_AmdBusLock, 0);
        g_AmdBusLockReady = TRUE;
    }

    /*
     * Family 17h+ base discovery: smb_en = 0x00. Low byte bit 0x10 is the
     * SMBus-enabled flag; high byte << 8 is the I/O base (commonly 0x0B00).
     * Read it — never hard-code (BRINGUP-AMD-FCH.md §1).
     */
    enLo = PmRead(FCH_PM_SMBUS_EN);
    enHi = PmRead(FCH_PM_SMBUS_EN + 1);

    if ((enLo & FCH_PM_SMBUS_EN_BIT) == 0)
        return STATUS_SUCCESS;             /* SMBus decode disabled */

    base = (USHORT)(enHi << 8);
    if (base == 0x0000 || base == 0xFF00)
        return STATUS_SUCCESS;             /* implausible base; stay disabled */

    /* PRIMARY controller: expose the 4-way FCH port mux as buses 0-3. */
    for (i = 0; i < FCH_SMBUS_PORT_COUNT; i++)
    {
        Controller->Buses[i].IoBase     = base;
        Controller->Buses[i].PortSelect = (UCHAR)i;
    }

    /* NOTE: this board's DRAM RGB (ENE) is on the PRIMARY bus 0 at 0x39/0x3A, alongside
       the SPD — not on a secondary controller. The AM4 secondary (0x0B20) exists but is
       not needed here; leaving it out avoids driving a controller whose state we did not
       cleanly verify. Re-add with proper detection if a board ever needs it. */
    Controller->BusCount        = i;        /* 4 primary mux ports */
    Controller->ReadImplemented = TRUE;
    return STATUS_SUCCESS;
}

UINT32 SmbusAmdRead(const SMBUS_CONTROLLER* Controller,
                    const BROKER_SMBUS_XFER_REQUEST* Req,
                    BROKER_SMBUS_XFER_RESPONSE* Resp)
{
    const SMBUS_BUS* bus;
    USHORT base;
    UCHAR  prevPort;
    UCHAR  sizeProto;
    UINT32 status;

    if (!Controller->ReadImplemented || !g_AmdBusLockReady)
        return BrokerSmbusNotImplemented;
    if (Req->BusIndex >= Controller->BusCount)
        return BrokerSmbusBadRequest;

    switch (Req->Op)
    {
        case BrokerSmbusReadByte:  sizeProto = PIIX4_BYTE_DATA;  break;
        case BrokerSmbusReadWord:  sizeProto = PIIX4_WORD_DATA;  break;
        case BrokerSmbusReadBlock: sizeProto = PIIX4_BLOCK_DATA; break;
        default:                    return BrokerSmbusBadRequest;
    }

    bus  = &Controller->Buses[Req->BusIndex];
    base = bus->IoBase;

    /* Serialize port-select + transaction + restore as one critical section. */
    KeWaitForSingleObject(&g_AmdBusLock, Executive, KernelMode, FALSE, NULL);

    prevPort = SmbusAmdSelectPort(bus->PortSelect);

    status = SmbusAmdTransaction(base, (UCHAR)Req->Address, (UCHAR)Req->Command, sizeProto);

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
                /* Reading SMBHSTCNT resets the block-data FIFO pointer. */
                (VOID)HstRead(base, SMBHSTCNT);
                for (UCHAR n = 0; n < count; n++)
                    Resp->Data[n] = HstRead(base, SMBBLKDAT);
                Resp->Length = count;
                break;
            }
        }
    }

    SmbusAmdRestorePort(prevPort);

    KeReleaseMutex(&g_AmdBusLock, FALSE);

    return status;
}

/*---------------------------------------------------------------------------*\
| One PIIX4 WRITE transaction: program ADD (rw=0) + CMD, load the data         |
| registers BEFORE START, set protocol + START, poll bounded, map errors.     |
| Byte/word load DAT0 (and DAT1); block loads the count into DAT0, resets the |
| controller's block FIFO index with one SMBHSTCNT read, then streams the     |
| payload into SMBBLKDAT — the exact i2c-piix4 I2C_SMBUS_BLOCK_DATA write     |
| sequence. The caller has already brick-guarded the address and bounded      |
| BlockLength to 1..BROKER_SMBUS_MAX_BLOCK.                                   |
\*---------------------------------------------------------------------------*/
static UINT32 SmbusAmdWriteTransaction(USHORT Base, UCHAR Address, UCHAR Command, UCHAR SizeProto,
                                       USHORT Data, const UINT8* Block, UINT32 BlockLength)
{
    UCHAR temp;

    temp = HstRead(Base, SMBHSTSTS);
    if (temp != 0x00)
    {
        HstWrite(Base, SMBHSTSTS, temp);
        temp = HstRead(Base, SMBHSTSTS);
        if (temp != 0x00)
            return BrokerSmbusBusError;
    }

    /* Address (write) + command, then the data register(s) before START. */
    HstWrite(Base, SMBHSTADD, (UCHAR)((Address << 1) | 0x00));
    HstWrite(Base, SMBHSTCMD, Command);

    if (SizeProto == PIIX4_BLOCK_DATA)
    {
        UINT32 n;
        HstWrite(Base, SMBHSTDAT0, (UCHAR)BlockLength);
        (VOID)HstRead(Base, SMBHSTCNT);            /* reset the SMBBLKDAT FIFO index */
        for (n = 0; n < BlockLength; n++)
            HstWrite(Base, SMBBLKDAT, Block[n]);
    }
    else
    {
        HstWrite(Base, SMBHSTDAT0, (UCHAR)(Data & 0xFF));
        if (SizeProto == PIIX4_WORD_DATA)
            HstWrite(Base, SMBHSTDAT1, (UCHAR)((Data >> 8) & 0xFF));
    }

    HstWrite(Base, SMBHSTCNT, (UCHAR)(SizeProto & SMBHSTCNT_SIZE_MASK));
    HstWrite(Base, SMBHSTCNT,
             (UCHAR)((HstRead(Base, SMBHSTCNT)) | SMBHSTCNT_START));

    if (!SmbusAmdPollIdle(Base, &temp))
    {
        HstWrite(Base, SMBHSTSTS, HstRead(Base, SMBHSTSTS));
        return BrokerSmbusBusError;
    }

    if (temp & SMBHSTSTS_ERROR_MASK)
    {
        HstWrite(Base, SMBHSTSTS, temp);
        return BrokerSmbusBusError;
    }

    return BrokerSmbusOk;
}

UINT32 SmbusAmdWrite(const SMBUS_CONTROLLER* Controller,
                     const BROKER_SMBUS_WRITE_REQUEST* Req)
{
    const SMBUS_BUS* bus;
    USHORT base;
    UCHAR  prevPort;
    UCHAR  sizeProto;
    UINT32 status;

    if (!Controller->ReadImplemented || !g_AmdBusLockReady)
        return BrokerSmbusNotImplemented;
    if (Req->BusIndex >= Controller->BusCount)
        return BrokerSmbusBadRequest;

    switch (Req->Op)
    {
        case BrokerSmbusWriteByte:  sizeProto = PIIX4_BYTE_DATA;  break;
        case BrokerSmbusWriteWord:  sizeProto = PIIX4_WORD_DATA;  break;
        case BrokerSmbusWriteBlock: sizeProto = PIIX4_BLOCK_DATA; break;
        default:                     return BrokerSmbusBadRequest;
    }

    bus  = &Controller->Buses[Req->BusIndex];
    base = bus->IoBase;

    /* Same lock as reads: port-select + transaction + restore is one critical section. */
    KeWaitForSingleObject(&g_AmdBusLock, Executive, KernelMode, FALSE, NULL);
    prevPort = SmbusAmdSelectPort(bus->PortSelect);
    status   = SmbusAmdWriteTransaction(base, (UCHAR)Req->Address, (UCHAR)Req->Command, sizeProto,
                                        (USHORT)Req->Data, Req->Block, Req->Length);
    SmbusAmdRestorePort(prevPort);
    KeReleaseMutex(&g_AmdBusLock, FALSE);

    return status;
}
