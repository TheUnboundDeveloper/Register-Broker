/*---------------------------------------------------------------------------*\
| SuperioNct6775.c — Nuvoton NCT6775 "classic" Super-I/O sensor backend       |
|                    (modern group: NCT6779/6791/6792/6793/6795/6796/6797/6798)|
|                                                                            |
|   Reads NCT6775-family hardware-monitor sensors over the LPC Super-I/O      |
|   interface. This family uses a DIFFERENT access architecture from the      |
|   NCT668x EC-space family (SuperioNct.c): a bank-select window, not a        |
|   page/index/data EC window.                                               |
|                                                                            |
|   Encodings are register FACTS reproduced from the Linux nct6775 driver     |
|   (drivers/hwmon/nct6775-core.c, nct6775-platform.c, nct6775.h). Every       |
|   literal below was cross-checked against a second independent reference     |
|   (see docs/SUPERIO-NCT6775-FAMILY.md for the citations):                    |
|                                                                            |
|     * SIO config port 0x2E/0x4E; enter = 0x87 x2, exit = 0xAA              |
|     * chip id at SIO regs 0x20/0x21, masked 0xFFF8 (mask must stay 0xFFF8:   |
|       NCT6796=0xD420 vs NCT6798=0xD428 differ only in the low nibble)       |
|     * HWM logical device 0x0B; base I/O port from SIO 0x60/0x61, aligned ~7 |
|     * HWM access (rel. base): write 0x4E (bank-select) to (base+5), bank to |
|       (base+6), register-offset to (base+5), read data from (base+6)        |
|     * IO-space lock: NCT6791+ gate the HWM mapping behind SIO CR 0x28 bit   |
|       0x10 — read-modify-write to clear it (Linux nct6791_enable_io_mapping; |
|       byte-identical across independent references). NCT6779 does NOT need   |
|       it and is excluded, exactly as the references do.                      |
|     * temps:  signed byte (monitor regs 0x73/0x75/0x77/0x79/0x7B word-sized, |
|               half-deg = bit7 of reg+1; PECI 0x27 byte-only)                |
|     * fans:   16-bit big-endian RPM, direct read of 0x4C0..0x4CE            |
|               (Linux fan_from_reg_rpm: these regs already hold RPM)         |
|     * volts:  single ADC byte at 0x480..0x48F (broker applies 8 mV/LSB)     |
|                                                                            |
|   READ-ONLY. The only write is the verbatim, bounded IO-space-lock clear    |
|   (a single SIO config-register read-modify-write of one bit) on the chips  |
|   that documentedly require it — never a sensor/SMBus/RGB write.            |
|                                                                            |
|   NARROW BY DESIGN: the IOCTL selects a named {kind,index}; the HWM register |
|   is looked up from a baked-in table here. The client never names a raw     |
|   address. The kernel returns raw bytes; the broker decodes.               |
\*---------------------------------------------------------------------------*/
#include "SmbusController.h"

/* SIO config (try both common index/data port pairs). */
#define SIO_PORT_A            0x2E
#define SIO_PORT_B            0x4E
#define SIO_ENTER_KEY         0x87
#define SIO_EXIT_KEY          0xAA
#define SIO_REG_CHIPID_HI     0x20
#define SIO_REG_CHIPID_LO     0x21
#define SIO_REG_LDSEL         0x07
#define SIO_REG_ENABLE        0x30      /* LDN activate: bit0 */
#define SIO_REG_BASE_HI       0x60
#define SIO_REG_BASE_LO       0x61
#define NCT6775_LD_HWM        0x0B

/* NCT6791+ HM I/O-space lock (Linux NCT6791_REG_HM_IO_SPACE_LOCK_ENABLE).
   Bit 0x10 set = HWM mapping disabled. */
#define SIO_REG_IO_SPACE_LOCK 0x28
#define IO_SPACE_LOCK_BIT     0x10

/* Chip ids (16-bit, masked 0xFFF8 — see header). Modern group only. */
#define NCT6775_ID_MASK       0xFFF8
#define ID_NCT6779            0xC560
#define ID_NCT6791            0xC800
#define ID_NCT6792            0xC910
#define ID_NCT6793            0xD120
#define ID_NCT6795            0xD350
#define ID_NCT6796            0xD420
#define ID_NCT6797            0xD450
#define ID_NCT6798            0xD428

/* HWM bank-select access offsets relative to the resolved (aligned) base port. */
#define HWM_ADDR_OFF          0x05      /* address / bank-select register */
#define HWM_DATA_OFF          0x06      /* data register                  */
#define HWM_BANK_SELECT       0x4E      /* write to addr port to begin a bank switch */
#define HWM_BASE_ALIGN        0xFFF8u   /* IOREGION_ALIGNMENT (~7) */

/* Baked-in sensor register tables. Sizes are the per-kind real counts; the kernel
   bounds Index to these and the broker exposes exactly this many channels.

   Temps: the 5 peripheral "monitor" registers present on the whole modern group
   (NCT6779 floor) plus the PECI/CPU peripheral byte at 0x27. The monitor regs are
   word-sized (value at reg, half-degree at bit7 of reg+1); 0x27 is byte-only. */
#define NCT6775_TEMP_COUNT    6u
#define NCT6775_FAN_COUNT     7u
#define NCT6775_VOLT_COUNT    16u

static const USHORT g_TempReg[NCT6775_TEMP_COUNT]  = { 0x073, 0x075, 0x077, 0x079, 0x07B, 0x027 };
static const UCHAR  g_TempWord[NCT6775_TEMP_COUNT] = {     1,     1,     1,     1,     1,     0 };
/* Fans: Linux NCT6779_REG_FAN, read as direct 16-bit RPM (fan_from_reg_rpm). */
static const USHORT g_FanReg[NCT6775_FAN_COUNT]    = { 0x4C0, 0x4C2, 0x4C4, 0x4C6, 0x4C8, 0x4CA, 0x4CE };
/* Voltages: 0x480..0x48F, one ADC byte each (modern-group voltage registers). */
/* (computed as 0x480 + index in the read path) */

static KMUTEX  g_Nct6775Lock;
static BOOLEAN g_Nct6775LockReady = FALSE;

static __forceinline VOID PortOut(USHORT Port, UCHAR Value)
{
    WRITE_PORT_UCHAR((PUCHAR)(ULONG_PTR)Port, Value);
}

static __forceinline UCHAR PortIn(USHORT Port)
{
    return READ_PORT_UCHAR((PUCHAR)(ULONG_PTR)Port);
}

static UCHAR SioInb(USHORT IoReg, UCHAR Reg)
{
    PortOut(IoReg, Reg);
    return PortIn((USHORT)(IoReg + 1));
}

static VOID SioOutb(USHORT IoReg, UCHAR Reg, UCHAR Value)
{
    PortOut(IoReg, Reg);
    PortOut((USHORT)(IoReg + 1), Value);
}

static VOID SioEnter(USHORT IoReg)
{
    PortOut(IoReg, SIO_ENTER_KEY);
    PortOut(IoReg, SIO_ENTER_KEY);
}

static VOID SioExit(USHORT IoReg)
{
    PortOut(IoReg, SIO_EXIT_KEY);
}

/* TRUE for the modern NCT6775-family chips this backend supports. */
static __forceinline BOOLEAN Nct6775IdMatches(USHORT Id)
{
    USHORT m = (USHORT)(Id & NCT6775_ID_MASK);
    return m == ID_NCT6779 || m == ID_NCT6791 || m == ID_NCT6792 || m == ID_NCT6793 ||
           m == ID_NCT6795 || m == ID_NCT6796 || m == ID_NCT6797 || m == ID_NCT6798;
}

/* TRUE for the chips that gate the HWM mapping behind the SIO 0x28 I/O-space lock.
   This is the Linux nct6791_enable_io_mapping gate: NCT6791/6792/6793/6795/6796/6797/6798
   (NOT NCT6779 — it has no such lock). */
static __forceinline BOOLEAN Nct6775NeedsIoSpaceUnlock(USHORT Id)
{
    USHORT m = (USHORT)(Id & NCT6775_ID_MASK);
    return m == ID_NCT6791 || m == ID_NCT6792 || m == ID_NCT6793 ||
           m == ID_NCT6795 || m == ID_NCT6796 || m == ID_NCT6797 || m == ID_NCT6798;
}

/* Read one HWM byte at a 16-bit register address via the bank-select window.
   Caller holds g_Nct6775Lock. Base is the aligned HWM base I/O port. */
static UCHAR HwmRead(USHORT Base, USHORT Reg)
{
    USHORT addrPort = (USHORT)(Base + HWM_ADDR_OFF);
    USHORT dataPort = (USHORT)(Base + HWM_DATA_OFF);
    UCHAR  bank     = (UCHAR)(Reg >> 8);
    UCHAR  offset   = (UCHAR)(Reg & 0xFF);

    PortOut(addrPort, HWM_BANK_SELECT);   /* begin bank switch */
    PortOut(dataPort, bank);              /* select bank       */
    PortOut(addrPort, offset);            /* select register   */
    return PortIn(dataPort);              /* read data byte    */
}

VOID SuperioNct6775Detect(SMBUS_CONTROLLER* Controller)
{
    static const USHORT ports[2] = { SIO_PORT_A, SIO_PORT_B };
    ULONG i;

    /* No-op if an earlier Super-I/O backend already claimed a chip (a board has one
       Super-I/O HWM). Keeps a board matching exactly one backend. */
    if (Controller->SuperioAvailable)
        return;

    if (!g_Nct6775LockReady)
    {
        KeInitializeMutex(&g_Nct6775Lock, 0);
        g_Nct6775LockReady = TRUE;
    }

    for (i = 0; i < 2; i++)
    {
        USHORT ioreg = ports[i];
        USHORT id;

        SioEnter(ioreg);
        id = (USHORT)(((USHORT)SioInb(ioreg, SIO_REG_CHIPID_HI) << 8) | SioInb(ioreg, SIO_REG_CHIPID_LO));

        if (Nct6775IdMatches(id))
        {
            USHORT raw, base;

            SioOutb(ioreg, SIO_REG_LDSEL, NCT6775_LD_HWM);     /* select HWM logical device */
            raw  = (USHORT)(((USHORT)SioInb(ioreg, SIO_REG_BASE_HI) << 8) | SioInb(ioreg, SIO_REG_BASE_LO));
            base = (USHORT)(raw & HWM_BASE_ALIGN);             /* IOREGION_ALIGNMENT */

            /* Activate the HWM logical device if it isn't already (Linux probe: set
               SIO_REG_ENABLE bit0). Harmless if already enabled. */
            {
                UCHAR en = SioInb(ioreg, SIO_REG_ENABLE);
                if ((en & 0x01) == 0)
                    SioOutb(ioreg, SIO_REG_ENABLE, (UCHAR)(en | 0x01));
            }

            /* NCT6791+ : clear the HWM I/O-space lock (SIO CR 0x28 bit 0x10) so the HWM
               register window is reachable. Verbatim read-modify-write of one bit; only
               on the chips that documentedly need it. */
            if (Nct6775NeedsIoSpaceUnlock(id))
            {
                UCHAR opt = SioInb(ioreg, SIO_REG_IO_SPACE_LOCK);
                if (opt & IO_SPACE_LOCK_BIT)
                    SioOutb(ioreg, SIO_REG_IO_SPACE_LOCK, (UCHAR)(opt & ~IO_SPACE_LOCK_BIT));
            }

            SioExit(ioreg);

            /* A valid HWM base is page-aligned, non-zero, and not all-ones. Linux also
               requires (addr & 0xF007) == 0 after alignment. */
            if (base != 0 && base != (USHORT)(0xFFFF & HWM_BASE_ALIGN) &&
                base >= 0x100 && ((base & 0xF007) == 0))
            {
                Controller->SuperioAvailable = TRUE;
                Controller->SuperioBase      = base;       /* aligned HWM base port */
                Controller->SuperioChipId    = id;
                Controller->SuperioKind      = BROKER_SUPERIO_KIND_NCT6775;
            }
            return;                                         /* chip found; done either way */
        }

        SioExit(ioreg);
    }
}

UINT32 SuperioNct6775Read(const SMBUS_CONTROLLER* Controller, UINT32 Kind, UINT32 Index, UINT32* Raw)
{
    USHORT base = Controller->SuperioBase;
    USHORT reg;
    UCHAR  a, b;

    *Raw = 0;

    if (!Controller->SuperioAvailable ||
        Controller->SuperioKind != BROKER_SUPERIO_KIND_NCT6775 || !g_Nct6775LockReady)
        return BrokerSmbusNotImplemented;

    switch (Kind)
    {
        case BrokerSuperioTemp:
            if (Index >= NCT6775_TEMP_COUNT) return BrokerSmbusBadRequest;
            reg = g_TempReg[Index];
            KeWaitForSingleObject(&g_Nct6775Lock, Executive, KernelMode, FALSE, NULL);
            a = HwmRead(base, reg);
            b = g_TempWord[Index] ? HwmRead(base, (USHORT)(reg + 1)) : 0;
            KeReleaseMutex(&g_Nct6775Lock, FALSE);
            /* value byte + half-degree byte; broker decodes signed + 0.5*(half>>7). */
            *Raw = (UINT32)a | ((UINT32)b << 8);
            return BrokerSmbusOk;

        case BrokerSuperioFan:
            if (Index >= NCT6775_FAN_COUNT) return BrokerSmbusBadRequest;
            reg = g_FanReg[Index];
            KeWaitForSingleObject(&g_Nct6775Lock, Executive, KernelMode, FALSE, NULL);
            a = HwmRead(base, reg);                          /* high byte */
            b = HwmRead(base, (USHORT)(reg + 1));            /* low byte  */
            KeReleaseMutex(&g_Nct6775Lock, FALSE);
            *Raw = ((UINT32)a << 8) | (UINT32)b;             /* 16-bit big-endian RPM */
            return BrokerSmbusOk;

        case BrokerSuperioVoltage:
            if (Index >= NCT6775_VOLT_COUNT) return BrokerSmbusBadRequest;
            reg = (USHORT)(0x480 + Index);                   /* single ADC byte */
            KeWaitForSingleObject(&g_Nct6775Lock, Executive, KernelMode, FALSE, NULL);
            a = HwmRead(base, reg);
            KeReleaseMutex(&g_Nct6775Lock, FALSE);
            *Raw = (UINT32)a;                                /* broker applies 8 mV/LSB */
            return BrokerSmbusOk;

        default:
            return BrokerSmbusBadRequest;
    }
}
