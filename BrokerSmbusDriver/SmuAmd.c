/*---------------------------------------------------------------------------*\
| SmuAmd.c — AMD SMU/SMN sensor backend (CPU temperature)                      |
|                                                                            |
|   Reads the AMD Family 17h/19h "reported temperature" register over the    |
|   SMN (System Management Network), which is reached through index/data       |
|   registers in the PCI config space of the root complex at 00:00.0.        |
|                                                                            |
|   Encodings are PORTED, NOT INVENTED — from Linux `drivers/hwmon/k10temp.c` |
|   + `arch/x86/kernel/amd_nb.c` and the zenpower driver:                     |
|     * SMN index reg = PCI cfg 0x60, data reg = PCI cfg 0x64 (root 00:00.0)  |
|     * reported-temp control register SMN addr = 0x00059800 (Fam 17h/19h)    |
|                                                                            |
|   NARROW BY DESIGN: the IOCTL selects a *named* sensor; the SMN address is  |
|   hardcoded here. The client can never name an arbitrary SMN address, so    |
|   this can never become a general SMN-read primitive. The kernel returns    |
|   the RAW 32-bit register; the per-model Tctl/Tdie decode lives in the      |
|   user-mode broker (it changes per CPU and is easy to update there).        |
\*---------------------------------------------------------------------------*/
#include "SmbusController.h"
#include <intrin.h>

/* SMN index/data in the root complex (00:00.0) PCI config space. */
#define AMD_ROOT_BUS        0u
#define AMD_ROOT_SLOT       0u          /* device 0, function 0 */
#define AMD_SMN_INDEX_OFF   0x60u       /* write 32-bit SMN address here */
#define AMD_SMN_DATA_OFF    0x64u       /* read 32-bit value from here   */

/* Reported-temperature control register base (Family 17h/19h). The per-CCD die-temp
   registers are at this base + a per-model offset + ccd*4 (k10temp ZEN_CCD_TEMP). */
#define ZEN_REPORTED_TEMP_CTRL  0x00059800u
#define ZEN_CCD_TEMP(offset, ccd)  (ZEN_REPORTED_TEMP_CTRL + (offset) + ((ccd) * 4u))

/* AMD SVI2 voltage-telemetry plane registers, PORTED (not invented) from the zenpower
   driver (GPL-2.0) and cross-checked against the Linux k10temp "core/SoC voltages" patch.
   Base SMN 0x0005A000; PLANE0/PLANE1 offsets and the core/SoC plane assignment are
   per-model. We bake ONLY the models whose offsets are documented and identical:
     * Matisse  (17h/0x71) and Vermeer (19h/0x21): core = +0x10, SoC = +0xC.
   Every other model leaves the planes 0 → SVI voltages are simply not offered (never
   read a wrong register). The broker decodes the plane value to volts. */
#define ZEN_SVI_BASE            0x0005A000u
#define ZEN_SVI_PLANE_0X10      (ZEN_SVI_BASE + 0x10u)   /* core on Matisse/Vermeer */
#define ZEN_SVI_PLANE_0X0C      (ZEN_SVI_BASE + 0x0Cu)   /* SoC  on Matisse/Vermeer */

static KMUTEX  g_SmuLock;
static BOOLEAN g_SmuLockReady = FALSE;

/* Effective CPUID family/model, or family 0 if the CPU is not AuthenticAMD. */
static VOID SmuAmdCpuFamilyModel(UCHAR* Family, UCHAR* Model)
{
    int    regs[4];
    UINT32 eax;
    UCHAR  baseFam, extFam, baseModel, extModel;

    *Family = 0;
    *Model  = 0;

    __cpuid(regs, 0);
    if ((UINT32)regs[1] != 0x68747541u ||   /* "Auth" (ebx) */
        (UINT32)regs[3] != 0x69746E65u ||   /* "enti" (edx) */
        (UINT32)regs[2] != 0x444D4163u)     /* "cAMD" (ecx) */
        return;

    __cpuid(regs, 1);
    eax       = (UINT32)regs[0];
    baseFam   = (UCHAR)((eax >> 8)  & 0xF);
    extFam    = (UCHAR)((eax >> 20) & 0xFF);
    baseModel = (UCHAR)((eax >> 4)  & 0xF);
    extModel  = (UCHAR)((eax >> 16) & 0xF);

    *Family = (baseFam == 0xF) ? (UCHAR)(baseFam + extFam) : baseFam;
    /* Extended model applies when base family is 0xF (always true on Zen). */
    *Model  = (baseFam == 0xF) ? (UCHAR)((extModel << 4) | baseModel) : baseModel;
}

/* Per-model CCD-temperature SMN offset, ported verbatim from Linux k10temp.c. Returns 0
   when the model is not in the table — CCD temps are then simply not offered (never read
   a wrong register). Vermeer (19h/0x21, e.g. 5800X3D) -> 0x154. */
static UINT32 SmuAmdCcdOffset(UCHAR Family, UCHAR Model)
{
    if (Family == 0x17)
    {
        if (Model == 0x1 || Model == 0x8 || Model == 0x11 || Model == 0x18) return 0x154u;
        if (Model == 0x31 || Model == 0x47 || Model == 0x60 ||
            Model == 0x68 || Model == 0x71)                                 return 0x154u;
        if (Model >= 0xA0 && Model <= 0xAF)                                 return 0x300u;
    }
    else if (Family == 0x19)
    {
        if (Model <= 0x1 || Model == 0x8 || Model == 0x21 ||
            (Model >= 0x50 && Model <= 0x5F))                               return 0x154u;
        if (Model >= 0x40 && Model <= 0x4F)                                 return 0x300u;
        if ((Model >= 0x60 && Model <= 0x6F) || (Model >= 0x70 && Model <= 0x7F)) return 0x308u;
        if ((Model >= 0x10 && Model <= 0x1F) || (Model >= 0xA0 && Model <= 0xAF)) return 0x300u;
    }
    return 0u;
}

/* Per-model SVI voltage-telemetry plane addresses (zenpower). Sets *Core/*Soc to the SMN
   addresses for VDDCR_CPU / VDDCR_SOC, or leaves them 0 when the model is unknown — only
   models with documented, identical plane layouts are baked. Returns TRUE if known. */
static BOOLEAN SmuAmdSviConfig(UCHAR Family, UCHAR Model, UINT32* Core, UINT32* Soc)
{
    *Core = 0;
    *Soc  = 0;

    /* Matisse (Zen 2 desktop, 17h/0x71) and Vermeer (Zen 3 desktop, 19h/0x21) share the
       same SVI plane layout: PLANE0 (+0x10) = core, PLANE1 (+0xC) = SoC. */
    if ((Family == 0x17 && Model == 0x71) ||
        (Family == 0x19 && Model == 0x21))
    {
        *Core = ZEN_SVI_PLANE_0X10;
        *Soc  = ZEN_SVI_PLANE_0X0C;
        return TRUE;
    }
    return FALSE;
}

VOID SmuAmdDetect(SMBUS_CONTROLLER* Controller)
{
    Controller->SmuAvailable = FALSE;
    Controller->SmuCcdOffset = 0;
    Controller->SmuTelemetryAvailable = FALSE;
    Controller->SmuSviCoreAddr = 0;
    Controller->SmuSviSocAddr  = 0;
    SmuAmdCpuFamilyModel(&Controller->CpuFamily, &Controller->CpuModel);

    if (!g_SmuLockReady)
    {
        KeInitializeMutex(&g_SmuLock, 0);
        g_SmuLockReady = TRUE;
    }

    /* The 0x00059800 reported-temp register is valid on Zen (17h) and Zen 3/4 (19h). */
    if (Controller->CpuFamily == 0x17 || Controller->CpuFamily == 0x19)
    {
        Controller->SmuAvailable = TRUE;
        Controller->SmuCcdOffset = SmuAmdCcdOffset(Controller->CpuFamily, Controller->CpuModel);
        Controller->SmuTelemetryAvailable = SmuAmdSviConfig(Controller->CpuFamily, Controller->CpuModel,
                                                            &Controller->SmuSviCoreAddr,
                                                            &Controller->SmuSviSocAddr);
    }
}

static UINT32 SmuAmdReadSmn(UINT32 SmnAddress, UINT32* Value)
{
    PCI_SLOT_NUMBER slot;
    ULONG xfer;

    slot.u.AsULONG = AMD_ROOT_SLOT;     /* dev 0, fn 0 */

    /* Program the SMN index (the address we want), then read the data register.
       Writing 0x60 only selects what 0x64 returns — it is not a control write. */
    xfer = HalSetBusDataByOffset(PCIConfiguration, AMD_ROOT_BUS, slot.u.AsULONG,
                                 &SmnAddress, AMD_SMN_INDEX_OFF, sizeof(SmnAddress));
    if (xfer != sizeof(SmnAddress))
        return BrokerSmbusBusError;

    xfer = HalGetBusDataByOffset(PCIConfiguration, AMD_ROOT_BUS, slot.u.AsULONG,
                                 Value, AMD_SMN_DATA_OFF, sizeof(*Value));
    if (xfer != sizeof(*Value))
        return BrokerSmbusBusError;

    return BrokerSmbusOk;
}

UINT32 SmuAmdRead(const SMBUS_CONTROLLER* Controller, UINT32 Sensor, UINT32* Raw)
{
    UINT32 status;
    UINT32 smn;

    *Raw = 0;

    if (!Controller->SmuAvailable || !g_SmuLockReady)
        return BrokerSmbusNotImplemented;

    if (Sensor == BrokerSmuCpuTemp)
    {
        smn = ZEN_REPORTED_TEMP_CTRL;
    }
    else if (Sensor >= BrokerSmuCcd0Temp && Sensor <= BrokerSmuCcd7Temp)
    {
        /* Per-CCD die temperature. Address baked from the per-model offset; the client only
           names a CCD index, never an SMN address. Unknown model (offset 0) -> not offered. */
        if (Controller->SmuCcdOffset == 0)
            return BrokerSmbusNotImplemented;
        smn = ZEN_CCD_TEMP(Controller->SmuCcdOffset, (Sensor - BrokerSmuCcd0Temp));
    }
    else if (Sensor == BrokerSmuCoreVoltage || Sensor == BrokerSmuSocVoltage)
    {
        /* SVI2 voltage telemetry. Address baked from the per-model plane config; the client
           names only the logical rail. Unknown model (no planes) -> not offered. */
        if (!Controller->SmuTelemetryAvailable)
            return BrokerSmbusNotImplemented;
        smn = (Sensor == BrokerSmuCoreVoltage) ? Controller->SmuSviCoreAddr
                                               : Controller->SmuSviSocAddr;
        if (smn == 0)
            return BrokerSmbusNotImplemented;
    }
    else
    {
        return BrokerSmbusBadRequest;   /* unknown named sensor */
    }

    /* SMN index/data is global controller state — serialize like the FCH mux. */
    KeWaitForSingleObject(&g_SmuLock, Executive, KernelMode, FALSE, NULL);
    status = SmuAmdReadSmn(smn, Raw);
    KeReleaseMutex(&g_SmuLock, FALSE);

    return status;
}
