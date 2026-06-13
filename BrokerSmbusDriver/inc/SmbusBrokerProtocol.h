/*---------------------------------------------------------------------------*\
| SmbusBrokerProtocol.h                                                       |
|                                                                             |
|   Shared IOCTL contract between the BrokerSmbus kernel driver and the      |
|   user-mode broker (BrokerSensorBridge). This is the ENTIRE surface the    |
|   driver exposes: bounded SMBus transactions only. No physical-memory       |
|   mapping, no MSR access, no arbitrary port I/O — that is what makes this   |
|   not WinRing0.                                                             |
|                                                                             |
|   The C# side (Smbus/SmbusTypes.cs) mirrors these layouts byte-for-byte.    |
|   Keep them in sync.                                                        |
\*---------------------------------------------------------------------------*/
#pragma once

#ifndef CTL_CODE
#include <winioctl.h>
#endif

#define BROKER_SMBUS_DEVICE_NAME    L"\\Device\\BrokerSmbus"
#define BROKER_SMBUS_SYMLINK_NAME   L"\\DosDevices\\BrokerSmbus"
/* User-mode opens \\.\BrokerSmbus */

#define BROKER_SMBUS_PROTOCOL_VERSION  1u
#define BROKER_SMBUS_MAX_BLOCK         32u

/* Detected host-controller vendor (reported by IOCTL_BROKER_SMBUS_INFO). */
#define BROKER_SMBUS_VENDOR_UNKNOWN    0u
#define BROKER_SMBUS_VENDOR_INTEL      1u
#define BROKER_SMBUS_VENDOR_AMD        2u

#define FILE_DEVICE_BROKER_SMBUS       0x8000u

/* METHOD_BUFFERED so the I/O manager copies request/response; small payloads. */
#define IOCTL_BROKER_SMBUS_INFO \
    CTL_CODE(FILE_DEVICE_BROKER_SMBUS, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_BROKER_SMBUS_XFER \
    CTL_CODE(FILE_DEVICE_BROKER_SMBUS, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)
/* Read a baked-in SMU/SMN sensor register (e.g. AMD CPU temperature). The client
   selects a NAMED sensor, never an SMN address — the register is hardcoded in the
   kernel so this can never become an arbitrary-SMN-read primitive. Returns the raw
   32-bit register value; the broker applies the per-model decode in user mode. */
#define IOCTL_BROKER_SMU_READ \
    CTL_CODE(FILE_DEVICE_BROKER_SMBUS, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS)
/* Read a baked-in Super-I/O (NCT6687D) sensor: a named {kind, index}, never a raw
   EC address. The kernel maps it to the hardcoded EC register and returns the raw
   bytes; the broker decodes (temp = signed + half-degree bit; fan = 16-bit RPM). */
#define IOCTL_BROKER_SUPERIO_READ \
    CTL_CODE(FILE_DEVICE_BROKER_SMBUS, 0x803, METHOD_BUFFERED, FILE_ANY_ACCESS)
/* Bounded SMBus WRITE (byte/word). Gated by an in-kernel brick-guard: only the RGB
   controller address range is writable; SPD and everything else is refused IN THE
   KERNEL, independent of the caller. This is the one write surface — kept tiny. */
#define IOCTL_BROKER_SMBUS_WRITE \
    CTL_CODE(FILE_DEVICE_BROKER_SMBUS, 0x804, METHOD_BUFFERED, FILE_ANY_ACCESS)
/* Enumerate the driver's registered hardware backends (read-only, no hardware
   touched — reports the detect-time state). Lists every backend COMPILED IN, with
   an Active flag for the ones that claimed hardware, so the broker can log/serve
   coverage truthfully without hard-coding the backend list a second time. */
#define IOCTL_BROKER_ENUM_BACKENDS \
    CTL_CODE(FILE_DEVICE_BROKER_SMBUS, 0x805, METHOD_BUFFERED, FILE_ANY_ACCESS)
/* Bounded Super-I/O (NCT6687 EC) RGB register WRITE — the motherboard-header RGB path.
   Like IOCTL_BROKER_SMBUS_WRITE this is brick-guarded IN THE KERNEL: only the NCT6687
   RGB register window is writable; the sensor banks (temps 0x100 / volts 0x120 / fans
   0x140), the SIO config space, and everything else are refused, no matter what the
   broker sends. The RGB window is a property of the NCT6687 SILICON (identical on every
   board using the chip), so new boards/zones never need a driver change — the broker's
   signed RgbBoardProfile carries the per-board specifics. SEPARATE from the SMBus write
   path: motherboard headers are on the EC, not the SMBus. */
#define IOCTL_BROKER_SUPERIO_RGB_WRITE \
    CTL_CODE(FILE_DEVICE_BROKER_SMBUS, 0x806, METHOD_BUFFERED, FILE_ANY_ACCESS)

/* Read-only operations in Phase B. Writes are intentionally not defined yet:
   they are the brick-risk surface and arrive only with the address allowlist
   and write guards in a later phase. */
typedef enum _BROKER_SMBUS_OP
{
    BrokerSmbusReadByte  = 0,
    BrokerSmbusReadWord  = 1,
    BrokerSmbusReadBlock = 2,
    /* Write ops are valid only on IOCTL_BROKER_SMBUS_WRITE (never the read XFER). */
    BrokerSmbusWriteByte  = 3,
    BrokerSmbusWriteWord  = 4,
    /* Bounded block write (1..BROKER_SMBUS_MAX_BLOCK bytes in one bus transaction).
       Same brick-guard as byte/word. Added so an RGB frame is a few atomic block
       transactions instead of dozens of byte transactions (each LED's 3 color bytes
       land together — no transient wrong-color mixes and far less bus time). */
    BrokerSmbusWriteBlock = 5
} BROKER_SMBUS_OP;

/* RGB controller write windows — the ONLY addresses the kernel will write to. ENE/Aura
   DRAM RGB controllers live in these windows; SPD (0x50-0x57), the SPD page-select
   (0x36/0x37), DIMM temp sensors (0x18-0x1F), and everything else are refused in-kernel.
   Two windows: the "standard" ENE DRAM range, and the 0x39/0x3A range this board uses.
   Both are clear of the protected addresses above. Widen deliberately. */
#define BROKER_SMBUS_RGB_ADDR_MIN   0x70u
#define BROKER_SMBUS_RGB_ADDR_MAX   0x77u
#define BROKER_SMBUS_DRAM_ADDR_MIN  0x39u   /* ENE DRAM on this AM4 board (0x39/0x3A) */
#define BROKER_SMBUS_DRAM_ADDR_MAX  0x3Au

/* NCT6687 EC RGB write window — the ONLY EC addresses the kernel will WRITE (16-bit
   page:index EC addresses). This bounds the IOCTL_BROKER_SUPERIO_RGB_WRITE path the
   same way the SMBus window bounds SMBus writes. The window MUST stay clear of the
   sensor banks (temps 0x100 / volts 0x120 / fans 0x140) and the SIO config space.
   Like the SMBus guard it constrains the EC ADDRESS region, not the value — any byte
   in the window is writable, and the broker's signed RgbBoardProfile decides which
   registers are actually written, so a new NCT6687 board needs no driver change.

   HW-UNVALIDATED: the exact NCT6687 RGB register page/offsets are NOT yet confirmed on
   hardware (OpenRGB drives MSI motherboard RGB over USB-HID, not the EC). Until they are
   validated with the bring-up probes, the EC RGB write path is kept INERT at RUNTIME by
   the SuperioRgbImplemented flag (always FALSE today) — the guard refuses every write
   regardless of these window values, and CAP_SUPERIO_RGB stays off, so the path is
   present and brick-guarded but cannot touch hardware. The window below is a documented
   PLACEHOLDER; replace it with the validated region AND set SuperioRgbImplemented = TRUE
   in SuperioNctDetect (a deliberate, re-validated, re-signed driver change) to enable it.
   Widen/move deliberately. */
#define BROKER_NCT6687_RGB_ADDR_MIN  0x0F00u   /* placeholder — NOT yet hardware-validated */
#define BROKER_NCT6687_RGB_ADDR_MAX  0x0FFFu

typedef enum _BROKER_SMBUS_STATUS
{
    BrokerSmbusOk             = 0,
    BrokerSmbusNotImplemented = 1,   /* driver present, controller path not yet wired */
    BrokerSmbusBadRequest     = 2,
    BrokerSmbusBusError       = 3,
    BrokerSmbusForbidden      = 4    /* blocked by the in-kernel address guard */
} BROKER_SMBUS_STATUS;

/* Named SMU sensors. Each maps to a baked-in SMN register in the kernel; the
   client never names a raw address. Extend deliberately, one validated sensor
   at a time. */
typedef enum _BROKER_SMU_SENSOR
{
    BrokerSmuCpuTemp  = 0,            /* AMD Family 17h/19h reported temperature (Tctl)   */
    /* Per-CCD die temperatures (k10temp ZEN_CCD_TEMP). Sensor n maps to CCD index (n-1);
       the kernel bakes in the per-model SMN address. Raw is the 32-bit register; the broker
       checks the valid bit (0x800) and decodes (raw & 0x7FF)·0.125 − 49 °C. */
    BrokerSmuCcd0Temp = 1,
    BrokerSmuCcd1Temp = 2,
    BrokerSmuCcd2Temp = 3,
    BrokerSmuCcd3Temp = 4,
    BrokerSmuCcd4Temp = 5,
    BrokerSmuCcd5Temp = 6,
    BrokerSmuCcd6Temp = 7,
    BrokerSmuCcd7Temp = 8
} BROKER_SMU_SENSOR;

#define BROKER_SMU_CCD_MAX   8u

/* Super-I/O sensor kinds. Index is bounded per kind in the kernel; the HWM/EC register
   is baked in — the client never names a raw address. The RAW PACKING is per-backend
   and decoded broker-side according to the detected chip (SuperioChipId):
     * NCT668x EC family (6683/6686/6687D): temp = valueByte | (halfByte<<8);
       fan/volt = 16-bit big-endian.
     * NCT6775 family (bank-select): temp = valueByte | (halfByte<<8) (PECI byte-only);
       fan = 16-bit big-endian RPM; volt = single ADC byte (8 mV/LSB, broker-decoded). */
typedef enum _BROKER_SUPERIO_KIND
{
    BrokerSuperioTemp    = 0,
    BrokerSuperioFan     = 1,
    BrokerSuperioVoltage = 2
} BROKER_SUPERIO_KIND;

/* Generous upper bounds covering all backends; each backend bounds Index to its own
   real count (NCT668x EC: 7/8/16, NCT6775: 6/7/16) and returns BadRequest past it. */
#define BROKER_SUPERIO_TEMP_COUNT   7u
#define BROKER_SUPERIO_FAN_COUNT    8u
#define BROKER_SUPERIO_VOLT_COUNT   16u    /* EC voltage bank 0x120..0x13E (before fans @0x140) */

/* Detected Super-I/O backend family (the broker derives decode/labels from the raw
   SuperioChipId; these document the ranges). NCT668x EC ids: 0xC73x/0xD44x/0xD59x.
   NCT6775-family ids: 0xC56x/0xC80x/0xC91x/0xD12x/0xD35x/0xD42x/0xD45x.
   KIND_ITE (2) is RESERVED — the ITE backend was retired 2026-06-11 (design
   record: docs/GIGABYTE-SUPPORT.md); never reuse the wire value. */
#define BROKER_SUPERIO_KIND_NONE     0u
#define BROKER_SUPERIO_KIND_NCT      1u
#define BROKER_SUPERIO_KIND_ITE      2u   /* reserved (archived backend) */
#define BROKER_SUPERIO_KIND_NCT6775  3u

#include <pshpack1.h>

typedef struct _BROKER_SMBUS_XFER_REQUEST
{
    UINT32 Version;     /* = BROKER_SMBUS_PROTOCOL_VERSION */
    UINT32 Op;          /* BROKER_SMBUS_OP                 */
    UINT32 BusIndex;    /* which detected SMBus controller  */
    UINT32 Address;     /* 7-bit SMBus device address       */
    UINT32 Command;     /* register / command code          */
    UINT32 Length;      /* block length requested (<= 32)   */
} BROKER_SMBUS_XFER_REQUEST;

typedef struct _BROKER_SMBUS_XFER_RESPONSE
{
    UINT32 Status;      /* BROKER_SMBUS_STATUS                 */
    UINT32 Length;      /* number of valid bytes in Data        */
    UINT8  Data[BROKER_SMBUS_MAX_BLOCK];
} BROKER_SMBUS_XFER_RESPONSE;

typedef struct _BROKER_SMBUS_INFO_RESPONSE
{
    UINT32 Version;        /* BROKER_SMBUS_PROTOCOL_VERSION  */
    UINT32 BusCount;       /* usable SMBus segments/ports     */
    UINT32 Capabilities;   /* bit0 = SMBus read, bit1 = SMU read */
    UINT32 Vendor;         /* BROKER_SMBUS_VENDOR_*           */
    UINT32 BusInfo[8];     /* per bus (diagnostic): (PortSelect << 16) | IoBase */
    /* Detected Super-I/O chip id (SIO regs 0x20/0x21), 0 if none. APPENDED at the end so
       existing field offsets are unchanged; the broker derives NCT vs ITE from the id range
       (0xD59x = Nuvoton, 0x86xx/0x87xx = ITE) to pick the right decode/labels. Older clients
       that read only the first 48 bytes keep working. */
    UINT32 SuperioChipId;
} BROKER_SMBUS_INFO_RESPONSE;

typedef struct _BROKER_SMU_READ_REQUEST
{
    UINT32 Version;        /* = BROKER_SMBUS_PROTOCOL_VERSION */
    UINT32 Sensor;         /* BROKER_SMU_SENSOR (named, not an address) */
} BROKER_SMU_READ_REQUEST;

typedef struct _BROKER_SMU_READ_RESPONSE
{
    UINT32 Status;         /* BROKER_SMBUS_STATUS                       */
    UINT32 Raw;            /* raw 32-bit SMN register; broker decodes    */
} BROKER_SMU_READ_RESPONSE;

typedef struct _BROKER_SUPERIO_READ_REQUEST
{
    UINT32 Version;        /* = BROKER_SMBUS_PROTOCOL_VERSION */
    UINT32 Kind;           /* BROKER_SUPERIO_KIND             */
    UINT32 Index;          /* bounded per kind                 */
} BROKER_SUPERIO_READ_REQUEST;

typedef struct _BROKER_SUPERIO_READ_RESPONSE
{
    UINT32 Status;         /* BROKER_SMBUS_STATUS                              */
    UINT32 Raw;            /* temp: value | (halfByte<<8); fan: 16-bit RPM      */
} BROKER_SUPERIO_READ_RESPONSE;

typedef struct _BROKER_SMBUS_WRITE_REQUEST
{
    UINT32 Version;        /* = BROKER_SMBUS_PROTOCOL_VERSION    */
    UINT32 Op;             /* BrokerSmbusWriteByte / WriteWord / WriteBlock */
    UINT32 BusIndex;       /* which detected SMBus controller     */
    UINT32 Address;        /* 7-bit device address (brick-guarded) */
    UINT32 Command;        /* register / command byte             */
    UINT32 Data;           /* byte (low 8) or word (low 16); unused for block */
    /* Block payload, APPENDED so the original 24-byte layout (byte/word) is
       unchanged: a byte/word request may still send only the first 24 bytes.
       For WriteBlock the full struct is required and Length must be
       1..BROKER_SMBUS_MAX_BLOCK (validated in-kernel). */
    UINT32 Length;         /* valid bytes in Block (WriteBlock only) */
    UINT8  Block[BROKER_SMBUS_MAX_BLOCK];
} BROKER_SMBUS_WRITE_REQUEST;

/* The prefix a byte/word write may legally truncate the request to. */
#define BROKER_SMBUS_WRITE_REQUEST_V1_SIZE  24u

typedef struct _BROKER_SMBUS_WRITE_RESPONSE
{
    UINT32 Status;         /* BROKER_SMBUS_STATUS (Forbidden if address not on RGB range) */
} BROKER_SMBUS_WRITE_RESPONSE;

/* IOCTL_BROKER_SUPERIO_RGB_WRITE: write Length (1..MAX_BLOCK) bytes to consecutive EC
   addresses starting at Address (16-bit page:index). Brick-guarded to the NCT6687 RGB
   window in-kernel; refused unless SuperioRgbImplemented (HW-validated) is set. */
typedef struct _BROKER_SUPERIO_RGB_WRITE_REQUEST
{
    UINT32 Version;        /* = BROKER_SMBUS_PROTOCOL_VERSION                 */
    UINT32 Address;        /* 16-bit EC address (page:index), brick-guarded   */
    UINT32 Length;         /* valid bytes in Block (1..BROKER_SMBUS_MAX_BLOCK) */
    UINT8  Block[BROKER_SMBUS_MAX_BLOCK];
} BROKER_SUPERIO_RGB_WRITE_REQUEST;

typedef struct _BROKER_SUPERIO_RGB_WRITE_RESPONSE
{
    UINT32 Status;         /* BROKER_SMBUS_STATUS (Forbidden if EC addr not on RGB window;
                              NotImplemented while the EC RGB path is HW-unvalidated) */
} BROKER_SUPERIO_RGB_WRITE_RESPONSE;

/* Backend classes reported by IOCTL_BROKER_ENUM_BACKENDS. */
#define BROKER_BACKEND_CLASS_SMBUS    0u   /* SMBus host controller (Detail: PCI device id)        */
#define BROKER_BACKEND_CLASS_SMU      1u   /* CPU SMU/SMN sensors  (Detail: (CpuFamily<<8)|Model)  */
#define BROKER_BACKEND_CLASS_SUPERIO  2u   /* Super-I/O sensors    (Detail: SIO chip id)           */

#define BROKER_BACKEND_NAME_MAX       32u
#define BROKER_ENUM_BACKENDS_MAX      16u

typedef struct _BROKER_BACKEND_INFO
{
    CHAR   Name[BROKER_BACKEND_NAME_MAX];   /* ASCII, null-terminated         */
    UINT32 Class;                           /* BROKER_BACKEND_CLASS_*         */
    UINT32 Active;                          /* 1 = claimed hardware at detect */
    UINT32 Detail;                          /* class-specific id; 0 if inactive */
} BROKER_BACKEND_INFO;

typedef struct _BROKER_ENUM_BACKENDS_RESPONSE
{
    UINT32 Version;        /* BROKER_SMBUS_PROTOCOL_VERSION */
    UINT32 Count;          /* valid entries in Backends[]    */
    BROKER_BACKEND_INFO Backends[BROKER_ENUM_BACKENDS_MAX];
} BROKER_ENUM_BACKENDS_RESPONSE;

#include <poppack.h>

#define BROKER_SMBUS_CAP_READ        0x1u
#define BROKER_SMBUS_CAP_SMU         0x2u
#define BROKER_SMBUS_CAP_SUPERIO     0x4u
#define BROKER_SMBUS_CAP_WRITE       0x8u
/* The brick-guarded NCT6687 EC RGB write path (IOCTL_BROKER_SUPERIO_RGB_WRITE). Off until
   the EC RGB register window is hardware-validated (SuperioRgbImplemented); see the window
   constants above. The broker gates its motherboard-header EC zone on this bit. */
#define BROKER_SMBUS_CAP_SUPERIO_RGB 0x10u
