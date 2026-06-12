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

    /// <summary>Human-readable backend state for logs / health.</summary>
    string Describe { get; }

    SmbusResult Read(int bus, int address, int command, SmbusOp op, int length);

    /// <summary>Reads a named SMU sensor's raw 32-bit register. The caller never supplies an address.</summary>
    bool TryReadSmuRaw(uint sensor, out uint raw, out SmbusStatus status);

    /// <summary>
    /// True when CCD die-temperature index <paramref name="ccd"/> reports valid (the SMU CCD
    /// register's valid bit is set). Detected once and cached; gates the cpu.ccd{n}.temp entries.
    /// </summary>
    bool CcdTempPresent(int ccd);

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

    /// <summary>Bounded SMBus write (byte/word), kernel brick-guarded to RGB addresses only.</summary>
    bool TryWrite(int bus, int address, int command, int data, bool word, out SmbusStatus status);

    /// <summary>
    /// Bounded SMBus BLOCK write (1..32 bytes in one bus transaction), same kernel
    /// brick-guard. One RGB LED's 3 color bytes land atomically instead of as three
    /// byte transactions — this is what makes per-LED frames fast and tear-free.
    /// </summary>
    bool TryWriteBlock(int bus, int address, int command, ReadOnlySpan<byte> data, out SmbusStatus status);
}
