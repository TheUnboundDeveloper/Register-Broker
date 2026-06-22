namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| SMBus broker types — mirror BrokerSmbusDriver/inc/SmbusBrokerProtocol.h.   |
| Reads (SMBus/SMU/Super-I/O) plus the brick-guarded RGB write path.          |
\*---------------------------------------------------------------------------*/
internal enum SmbusOp
{
    ReadByte  = 0,
    ReadWord  = 1,
    ReadBlock = 2
}

/// <summary>
/// SMBus RGB write device-class (mirror BROKER_RGB_WRITE_CLASS). A bounded write names the
/// class; the kernel permits ONLY that class's baked address window. Class 0 = ENE/Aura DRAM
/// (the legacy windows, the default for any caller that does not specify one). Each new value
/// must have a matching g_RgbWriteProfiles row in the driver (Smbus.c).
/// </summary>
internal enum RgbWriteClass : uint
{
    EneDram        = 0,   // ENE/Aura DRAM: 0x70-0x77 + 0x39-0x3A
    CorsairDram    = 1,   // Corsair Vengeance RGB Pro/RT DRAM: 0x58-0x5F
    CrucialDram    = 2,   // Crucial Ballistix: 0x20-0x27 + 0x39-0x3C
    HyperXDram     = 3,   // HyperX Predator/Fury: 0x27
    FuryDram       = 4,   // Kingston Fury: 0x58-0x67
    ViperDram      = 5,   // Patriot Viper / Viper Steel: 0x77
    XtreemDram     = 6,   // T-Force Xtreem (ENE): 0x70-0x78 + 0x39-0x3D
    CorsairVenDram = 7,   // Corsair Vengeance (original): 0x58-0x5F
    AsrockMb       = 8,   // ASRock Polychrome / ASR RGB motherboard: 0x6A
    EvgaMb         = 9    // EVGA ACX30 motherboard: 0x28
}

internal enum SmbusStatus
{
    Ok             = 0,
    NotImplemented = 1,
    BadRequest     = 2,
    BusError       = 3,
    Forbidden      = 4,
    Unavailable    = 100   // broker-side: no driver present (not a driver status)
}

internal readonly record struct SmbusResult(SmbusStatus Status, byte[] Data)
{
    public bool Ok => Status == SmbusStatus.Ok;
    public static SmbusResult Fail(SmbusStatus s) => new(s, Array.Empty<byte>());
}

/// <summary>Backend classes reported by IOCTL_BROKER_ENUM_BACKENDS (mirror BROKER_BACKEND_CLASS_*).</summary>
internal enum BackendClass
{
    Smbus   = 0,   // SMBus host controller (Detail: PCI device id)
    Smu     = 1,   // CPU SMU/SMN sensors   (Detail: (CpuFamily << 8) | CpuModel)
    Superio = 2    // Super-I/O sensors     (Detail: SIO chip id)
}

/// <summary>
/// One registered driver backend (an IOCTL_BROKER_ENUM_BACKENDS entry): every backend
/// the driver compiled in, with Active set on the ones that claimed hardware at detect.
/// </summary>
internal readonly record struct BackendInfo(string Name, BackendClass Class, bool Active, uint Detail);

internal interface ISmbusBackend
{
    /// <summary>True only when a usable SMBus driver is present and reports a read capability.</summary>
    bool Available { get; }

    /// <summary>True when the driver reports the SMU sensor path (e.g. AMD CPU temperature).</summary>
    bool SmuAvailable { get; }

    /// <summary>True when the driver reports a supported Super-I/O (board temps/fans).</summary>
    bool SuperioAvailable { get; }

    /// <summary>
    /// Detected Super-I/O chip id (SIO regs 0x20/0x21), 0 if none. Lets the catalog pick the
    /// right decode/labels per chip family (0xD59x = Nuvoton NCT6687D-class EC).
    /// </summary>
    int SuperioChipId { get; }

    /// <summary>True when the driver reports the brick-guarded SMBus write path (CAP_WRITE).</summary>
    bool WriteAvailable { get; }

    /// <summary>
    /// True when the driver reports the brick-guarded NCT6687 EC RGB write path
    /// (CAP_SUPERIO_RGB). Off until the EC RGB register window is hardware-validated, so the
    /// motherboard-header EC zone stays inert on a driver that has not enabled it.
    /// </summary>
    bool SuperioRgbAvailable { get; }

    /// <summary>Human-readable backend state for logs / health.</summary>
    string Describe { get; }

    /// <summary>
    /// The driver's registered hardware backends (its compiled-in detection registry) with
    /// per-entry Active flags. Empty when no driver is present or the driver predates
    /// IOCTL_BROKER_ENUM_BACKENDS — callers must treat enumeration as diagnostic, never
    /// as the availability gate (the CAP_* bits and chip id above remain authoritative).
    /// </summary>
    IReadOnlyList<BackendInfo> EnumerateBackends();

    SmbusResult Read(int bus, int address, int command, SmbusOp op, int length);

    /// <summary>Reads a named SMU sensor's raw 32-bit register. The caller never supplies an address.</summary>
    bool TryReadSmuRaw(uint sensor, out uint raw, out SmbusStatus status);

    /// <summary>
    /// True when CCD die-temperature index <paramref name="ccd"/> reports valid (the SMU CCD
    /// register's valid bit is set). Detected once and cached; gates the cpu.ccd{n}.temp entries.
    /// </summary>
    bool CcdTempPresent(int ccd);

    /// <summary>
    /// True when the driver serves AMD SVI2 voltage telemetry (the CPU model's plane addresses
    /// are baked in). Probed once and cached; gates the smu.cpu.vcore / smu.soc.voltage entries.
    /// An older driver (or an unsupported CPU model) reports false and the rails stay absent.
    /// </summary>
    bool SmuVoltagePresent { get; }

    /// <summary>Reads a named Super-I/O sensor {kind,index}'s raw value. The caller never supplies an EC address.</summary>
    bool TryReadSuperioRaw(uint kind, uint index, out uint raw, out SmbusStatus status);

    /// <summary>
    /// True when a JEDEC JC42.4 (TSE2004av) DIMM thermal sensor responds at slot <paramref name="index"/>
    /// (SMBus address 0x18 + index). Detected once by a non-destructive probe; cached.
    /// </summary>
    bool DimmTempPresent(int index);

    /// <summary>
    /// Reads the raw 16-bit JC42 temperature register (reg 0x05, packed MSB-first) at DIMM slot
    /// <paramref name="index"/>. The address is baked broker-side (catalog), never client-supplied;
    /// decode (DecodeJc42TempC) is the caller's job.
    /// </summary>
    bool TryReadDimmTempRaw(int index, out uint raw, out SmbusStatus status);

    /// <summary>Bounded SMBus write (byte/word), kernel brick-guarded to the
    /// <paramref name="deviceClass"/>'s address window only.</summary>
    bool TryWrite(int bus, int address, int command, int data, bool word, RgbWriteClass deviceClass, out SmbusStatus status);

    /// <summary>
    /// Bounded SMBus BLOCK write (1..32 bytes in one bus transaction), kernel brick-guarded
    /// to the <paramref name="deviceClass"/>'s address window only. One RGB LED's 3 color
    /// bytes land atomically instead of as three byte transactions — this is what makes
    /// per-LED frames fast and tear-free.
    /// </summary>
    bool TryWriteBlock(int bus, int address, int command, ReadOnlySpan<byte> data, RgbWriteClass deviceClass, out SmbusStatus status);

    /// <summary>
    /// Bounded NCT6687 EC RGB register write (1..32 bytes to consecutive EC addresses from
    /// <paramref name="ecAddress"/>). Kernel brick-guarded to the NCT6687 RGB register window and
    /// refused unless the EC RGB path is hardware-validated (SuperioRgbAvailable). The caller
    /// supplies a baked EC address from the RGB catalog — clients never reach this.
    /// </summary>
    bool TrySuperioRgbWrite(int ecAddress, ReadOnlySpan<byte> data, out SmbusStatus status);
}
