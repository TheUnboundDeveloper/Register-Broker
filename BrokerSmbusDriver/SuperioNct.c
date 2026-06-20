/*---------------------------------------------------------------------------*\
| SuperioNct.c — Nuvoton NCT668x EC-family Super-I/O sensor backend           |
|                (NCT6683 / NCT6686 / NCT6687D — board temps/fans/voltages)   |
|                                                                            |
|   Reads NCT668x hardware-monitor sensors over the LPC Super-I/O interface.  |
|   The three chips share the same detection flow, HWM logical device, EC     |
|   page/index/data window, register banks, and decode — the proven port      |
|   sources treat them as one group (docs/SUPERIO-NCT6683-NCT6686.md), so     |
|   widening from NCT6687D is a chip-id gate change only. NCT6687D is the     |
|   HWiNFO-validated chip; 6683/6686 run the identical path.                  |
|   Encodings are PORTED, NOT INVENTED — from the Linux NCT6687D kernel       |
|   module (github.com/Fred78290/nct6687d), Linux nct6683.c, and a second     |
|   independent reference:                                                    |
|     * SIO config port 0x2E/0x4E; enter = 0x87 x2, exit = 0xAA              |
|     * chip id at SIO regs 0x20/0x21, masked 0xFFF0:                         |
|         NCT6683 = 0xC730, NCT6686 = 0xD440, NCT6687x = 0xD590              |
|     * HWM logical device 0x0B; EC base I/O port from SIO regs 0x60/0x61    |
|     * EC space (rel. base): page +0x04, index +0x05, data +0x06           |
|     * temps: signed byte at 0x100+i*2, half-deg bit at (0x101+i*2)>>7      |
|     * fans:  16-bit big-endian RPM at 0x140+i*2                            |
|     * pwm:   single duty byte (0..255) at 0x160+i (READ-ONLY telemetry)     |
|                                                                            |
|   NARROW BY DESIGN: the IOCTL selects a named {kind,index}; the EC register |
|   is computed from a baked-in formula here. The client never names an EC    |
|   address. The kernel returns raw bytes; the broker decodes (temp/fan).     |
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
#define SIO_REG_BASE_HI       0x60
#define SIO_REG_BASE_LO       0x61
#define NCT6687_LD_HWM        0x0B

/* The NCT668x EC family (register-identical; see docs/SUPERIO-NCT6683-NCT6686.md). */
#define NCT668X_CHIPID_MASK   0xFFF0
#define NCT6683_CHIPID        0xC730
#define NCT6686_CHIPID        0xD440
#define NCT6687_CHIPID        0xD590

/* EC-space register offsets relative to the HWM base port. */
#define EC_PAGE_OFF           0x04
#define EC_INDEX_OFF          0x05
#define EC_DATA_OFF           0x06
#define EC_PAGE_SELECT        0xFF

/* Baked-in sensor register bases (EC addresses). */
#define NCT6687_TEMP_BASE     0x100      /* temp i:    0x100 + i*2 (value), +1 (half bit)  */
#define NCT6687_VOLTAGE_BASE  0x120      /* voltage i: 0x120 + i*2 (16-bit mV, big-endian) */
#define NCT6687_FAN_BASE      0x140      /* fan  i:    0x140 + i*2 (16-bit RPM, big-endian) */
#define NCT6687_PWM_BASE      0x160      /* pwm  i:    0x160 + i   (single duty byte 0..255) */

static KMUTEX  g_SuperioLock;
static BOOLEAN g_SuperioLockReady = FALSE;

/* TRUE for any chip of the register-identical NCT668x EC family. */
static __forceinline BOOLEAN Nct668xIdMatches(USHORT Id)
{
    USHORT m = (USHORT)(Id & NCT668X_CHIPID_MASK);
    return m == NCT6683_CHIPID || m == NCT6686_CHIPID || m == NCT6687_CHIPID;
}

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

/* Read one EC byte at a 16-bit EC address (page:index). Caller holds g_SuperioLock. */
static UCHAR EcRead(USHORT Base, USHORT Address)
{
    UCHAR page  = (UCHAR)(Address >> 8);
    UCHAR index = (UCHAR)(Address & 0xFF);

    PortOut((USHORT)(Base + EC_PAGE_OFF), EC_PAGE_SELECT);
    PortOut((USHORT)(Base + EC_PAGE_OFF), page);
    PortOut((USHORT)(Base + EC_INDEX_OFF), index);
    return PortIn((USHORT)(Base + EC_DATA_OFF));
}

/* Write one EC byte at a 16-bit EC address (page:index). Caller holds g_SuperioLock.
   Mirrors EcRead exactly (page-select + index) but drives the data port instead. Only
   ever reached for addresses the RGB brick-guard has already cleared. */
static VOID EcWrite(USHORT Base, USHORT Address, UCHAR Value)
{
    UCHAR page  = (UCHAR)(Address >> 8);
    UCHAR index = (UCHAR)(Address & 0xFF);

    PortOut((USHORT)(Base + EC_PAGE_OFF), EC_PAGE_SELECT);
    PortOut((USHORT)(Base + EC_PAGE_OFF), page);
    PortOut((USHORT)(Base + EC_INDEX_OFF), index);
    PortOut((USHORT)(Base + EC_DATA_OFF), Value);
}

VOID SuperioNctDetect(SMBUS_CONTROLLER* Controller)
{
    static const USHORT ports[2] = { SIO_PORT_A, SIO_PORT_B };
    ULONG i;

    Controller->SuperioAvailable = FALSE;
    Controller->SuperioBase      = 0;
    Controller->SuperioChipId    = 0;
    Controller->SuperioKind      = BROKER_SUPERIO_KIND_NONE;
    /* EC RGB write stays HW-unvalidated: never advertise CAP_SUPERIO_RGB or pass the RGB
       guard until the register window is confirmed on hardware (see SmbusBrokerProtocol.h).
       Flip to TRUE here, alongside a validated BROKER_NCT6687_RGB_ADDR_* window, to enable. */
    Controller->SuperioRgbImplemented = FALSE;

    if (!g_SuperioLockReady)
    {
        KeInitializeMutex(&g_SuperioLock, 0);
        g_SuperioLockReady = TRUE;
    }

    for (i = 0; i < 2; i++)
    {
        USHORT ioreg = ports[i];
        USHORT id;

        SioEnter(ioreg);
        id = (USHORT)(((USHORT)SioInb(ioreg, SIO_REG_CHIPID_HI) << 8) | SioInb(ioreg, SIO_REG_CHIPID_LO));

        if (Nct668xIdMatches(id))
        {
            USHORT base;
            SioOutb(ioreg, SIO_REG_LDSEL, NCT6687_LD_HWM);     /* select HWM logical device */
            base = (USHORT)(((USHORT)SioInb(ioreg, SIO_REG_BASE_HI) << 8) | SioInb(ioreg, SIO_REG_BASE_LO));
            SioExit(ioreg);

            if (base != 0 && base != 0xFFFF)
            {
                Controller->SuperioAvailable = TRUE;
                Controller->SuperioBase      = base;
                Controller->SuperioChipId    = id;
                Controller->SuperioKind      = BROKER_SUPERIO_KIND_NCT;
            }
            return;                                            /* chip found; done either way */
        }

        SioExit(ioreg);
    }
}

UINT32 SuperioNctRead(const SMBUS_CONTROLLER* Controller, UINT32 Kind, UINT32 Index, UINT32* Raw)
{
    USHORT  base = Controller->SuperioBase;
    USHORT  addr;
    UCHAR   a, b;
    BOOLEAN single = FALSE;                       /* PWM is one byte; the rest are two */

    *Raw = 0;

    if (!Controller->SuperioAvailable || !g_SuperioLockReady)
        return BrokerSmbusNotImplemented;

    switch (Kind)
    {
        case BrokerSuperioTemp:
            if (Index >= BROKER_SUPERIO_TEMP_COUNT) return BrokerSmbusBadRequest;
            addr = (USHORT)(NCT6687_TEMP_BASE + Index * 2);
            break;

        case BrokerSuperioFan:
            if (Index >= BROKER_SUPERIO_FAN_COUNT) return BrokerSmbusBadRequest;
            addr = (USHORT)(NCT6687_FAN_BASE + Index * 2);
            break;

        case BrokerSuperioVoltage:
            if (Index >= BROKER_SUPERIO_VOLT_COUNT) return BrokerSmbusBadRequest;
            addr = (USHORT)(NCT6687_VOLTAGE_BASE + Index * 2);
            break;

        case BrokerSuperioPwm:
            /* READ-ONLY: the duty register is read here and returned verbatim; nothing in
               this driver ever WRITES it. Single byte at 0x160+i (no *2 stride). */
            if (Index >= BROKER_SUPERIO_PWM_COUNT) return BrokerSmbusBadRequest;
            addr = (USHORT)(NCT6687_PWM_BASE + Index);
            single = TRUE;
            break;

        default:
            return BrokerSmbusBadRequest;
    }

    /* EC page/index/data is global controller state — serialize. */
    KeWaitForSingleObject(&g_SuperioLock, Executive, KernelMode, FALSE, NULL);
    a = EcRead(base, addr);
    b = single ? (UCHAR)0 : EcRead(base, (USHORT)(addr + 1));
    KeReleaseMutex(&g_SuperioLock, FALSE);

    if (Kind == BrokerSuperioTemp)
        *Raw = (UINT32)a | ((UINT32)b << 8);     /* value byte + half-degree byte         */
    else if (Kind == BrokerSuperioPwm)
        *Raw = (UINT32)a;                        /* 8-bit duty 0..255                      */
    else
        *Raw = ((UINT32)a << 8) | (UINT32)b;     /* 16-bit big-endian (RPM or millivolts)  */

    return BrokerSmbusOk;
}

/*---------------------------------------------------------------------------*\
| Super-I/O backend registry + dispatch.                                      |
|                                                                             |
|   Adding a Super-I/O backend = one SuperioXxx.c file + one row here. Table  |
|   order is DETECTION order; first probe to claim the chip wins (a board has |
|   one Super-I/O HWM). The ITE backend was archived 2026-06-11 —             |
|   BROKER_SUPERIO_KIND_ITE (2) stays reserved in the protocol header so the  |
|   wire value is never reused.                                               |
\*---------------------------------------------------------------------------*/
const SUPERIO_BACKEND_DESCRIPTOR g_SuperioBackends[] =
{
    { "NCT668x EC", BROKER_SUPERIO_KIND_NCT,     SuperioNctDetect,     SuperioNctRead     },
    { "NCT6775",    BROKER_SUPERIO_KIND_NCT6775, SuperioNct6775Detect, SuperioNct6775Read },
};
const ULONG g_SuperioBackendCount = RTL_NUMBER_OF(g_SuperioBackends);

VOID SuperioDetectAll(SMBUS_CONTROLLER* Controller)
{
    ULONG i;
    for (i = 0; i < g_SuperioBackendCount; i++)
    {
        if (Controller->SuperioAvailable)
            break;                        /* claimed by an earlier probe — done */
        g_SuperioBackends[i].Detect(Controller);
    }
}

/* Backend dispatch used by the IOCTL handler. Kind NONE (0) matches no row. */
UINT32 SuperioReadDispatch(const SMBUS_CONTROLLER* Controller, UINT32 Kind, UINT32 Index, UINT32* Raw)
{
    ULONG i;
    *Raw = 0;
    for (i = 0; i < g_SuperioBackendCount; i++)
        if (g_SuperioBackends[i].Kind == Controller->SuperioKind)
            return g_SuperioBackends[i].Read(Controller, Kind, Index, Raw);
    return BrokerSmbusNotImplemented;
}

/*---------------------------------------------------------------------------*\
| NCT6687 EC RGB register WRITE (motherboard-header RGB) + brick-guard.        |
|                                                                             |
|   The hard safety boundary for the EC write path, the EC analogue of        |
|   BrokerSmbusWriteAddressAllowed (Smbus.c): the EC also drives fans and      |
|   voltages, so writes are confined to the NCT6687 RGB register window and    |
|   refused everywhere else, in the kernel, no matter what the broker sends.   |
|   SuperioRgbImplemented gates the whole path off while the window is         |
|   HW-unvalidated (FALSE today) — so this is present and bounded but inert.   |
\*---------------------------------------------------------------------------*/
static BOOLEAN SuperioRgbWriteAddressAllowed(const SMBUS_CONTROLLER* Controller, USHORT Address, UINT32 Length)
{
    USHORT last;

    /* Runtime kill-switch: until the RGB window is validated on hardware this is FALSE,
       so every EC RGB write is refused regardless of the placeholder window values. */
    if (!Controller->SuperioRgbImplemented)            return FALSE;
    if (Length == 0 || Length > BROKER_SMBUS_MAX_BLOCK) return FALSE;

    last = (USHORT)(Address + (USHORT)(Length - 1));
    if (last < Address)                                 return FALSE;   /* 16-bit wrap */

    return (Address >= BROKER_NCT6687_RGB_ADDR_MIN && last <= BROKER_NCT6687_RGB_ADDR_MAX);
}

UINT32 SuperioRgbWrite(const SMBUS_CONTROLLER* Controller, const BROKER_SUPERIO_RGB_WRITE_REQUEST* Req)
{
    USHORT base = Controller->SuperioBase;
    USHORT addr;
    UINT32 i;

    if (Req->Version != BROKER_SMBUS_PROTOCOL_VERSION)                      return BrokerSmbusBadRequest;
    if (!Controller->SuperioAvailable ||
        Controller->SuperioKind != BROKER_SUPERIO_KIND_NCT ||
        !g_SuperioLockReady)                                               return BrokerSmbusNotImplemented;
    if (Req->Address > 0xFFFF)                                              return BrokerSmbusBadRequest;

    addr = (USHORT)Req->Address;
    if (!SuperioRgbWriteAddressAllowed(Controller, addr, Req->Length))      return BrokerSmbusForbidden;

    /* EC page/index/data is global controller state shared with the sensor reads — serialize. */
    KeWaitForSingleObject(&g_SuperioLock, Executive, KernelMode, FALSE, NULL);
    for (i = 0; i < Req->Length; i++)
        EcWrite(base, (USHORT)(addr + i), Req->Block[i]);
    KeReleaseMutex(&g_SuperioLock, FALSE);

    return BrokerSmbusOk;
}
