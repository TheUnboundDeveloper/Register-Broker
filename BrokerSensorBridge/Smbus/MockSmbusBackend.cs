namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| MockSmbusBackend                                                           |
|                                                                            |
|   Deterministic in-memory backend used by --selftest so the smbus:read     |
|   scope plumbing and op routing can be verified without a real driver.     |
|   Returns a value derived from the request so tests can assert round-trip. |
\*---------------------------------------------------------------------------*/
internal sealed class MockSmbusBackend : ISmbusBackend
{
    private readonly uint _smuRaw;
    private readonly uint _superioRaw;

    public bool Available { get; }
    public bool SmuAvailable { get; }
    public string Describe => Available ? "mock SMBus backend (available)" : "mock SMBus backend (unavailable)";

    public MockSmbusBackend(bool available, bool smuAvailable = false, uint smuRaw = 0x69BB0000,
                            bool superioAvailable = false, int superioChipId = 0, uint superioRaw = 0)
    {
        Available = available;
        SmuAvailable = smuAvailable;
        _smuRaw = smuRaw;
        SuperioAvailable = superioAvailable;
        SuperioChipId = superioChipId;
        _superioRaw = superioRaw;
    }

    public bool TryReadSmuRaw(uint sensor, out uint raw, out SmbusStatus status)
    {
        if (!SmuAvailable) { raw = 0; status = SmbusStatus.NotImplemented; return false; }
        raw = _smuRaw; status = SmbusStatus.Ok; return true;   // 0x69BB0000 decodes to 56.62 C
    }

    public bool CcdTempPresent(int ccd) => false;

    /// <summary>Mock: SVI voltage telemetry tracks SMU availability (the driver bakes the planes per model).</summary>
    public bool SmuVoltagePresent => SmuAvailable;

    public bool SuperioAvailable { get; }

    public int SuperioChipId { get; }

    public bool TryReadSuperioRaw(uint kind, uint index, out uint raw, out SmbusStatus status)
    {
        if (!SuperioAvailable) { raw = 0; status = SmbusStatus.NotImplemented; return false; }
        raw = _superioRaw; status = SmbusStatus.Ok; return true;
    }

    public bool DimmTempPresent(int index) => false;

    /// <summary>
    /// Mirrors the driver's compiled-in registry (the names must match the kernel's
    /// g_SmbusBackends / g_SuperioBackends + the SMU entry exactly — the selftest uses
    /// this to pin the broker↔driver name contract). Active flags derive from the
    /// mock's configured state via the same ChipFamilies gates the catalog uses.
    /// </summary>
    public IReadOnlyList<BackendInfo> EnumerateBackends() => new[]
    {
        new BackendInfo("AMD FCH",    BackendClass.Smbus,   Available, 0),
        new BackendInfo("Intel i801", BackendClass.Smbus,   false,     0),
        new BackendInfo("AMD SMU",    BackendClass.Smu,     SmuAvailable, 0),
        new BackendInfo("NCT668x EC", BackendClass.Superio, SuperioAvailable && ChipFamilies.IsNctEc(SuperioChipId),   (uint)SuperioChipId),
        new BackendInfo("NCT6775",    BackendClass.Superio, SuperioAvailable && ChipFamilies.IsNct6775(SuperioChipId), (uint)SuperioChipId),
    };

    public bool TryReadDimmTempRaw(int index, out uint raw, out SmbusStatus status)
    {
        raw = 0; status = SmbusStatus.NotImplemented; return false;
    }

    public bool WriteAvailable => Available;   // mock: writes succeed when the mock is "available"

    /// <summary>Mock: the EC RGB path mirrors the kernel's HW-unvalidated default (off) unless asked.</summary>
    public bool SuperioRgbAvailable { get; init; }

    public bool TrySuperioRgbWrite(int ecAddress, ReadOnlySpan<byte> data, out SmbusStatus status)
    {
        if (data.Length is < 1 or > 32) { status = SmbusStatus.BadRequest; return false; }
        status = SuperioRgbAvailable ? SmbusStatus.Ok : SmbusStatus.Forbidden;
        return SuperioRgbAvailable;
    }

    public bool TryWrite(int bus, int address, int command, int data, bool word, out SmbusStatus status)
    {
        status = Available ? SmbusStatus.Ok : SmbusStatus.Unavailable;
        return Available;
    }

    public bool TryWriteBlock(int bus, int address, int command, ReadOnlySpan<byte> data, out SmbusStatus status)
    {
        if (data.Length is < 1 or > 32) { status = SmbusStatus.BadRequest; return false; }
        status = Available ? SmbusStatus.Ok : SmbusStatus.Unavailable;
        return Available;
    }

    public SmbusResult Read(int bus, int address, int command, SmbusOp op, int length)
    {
        if (!Available) return SmbusResult.Fail(SmbusStatus.Unavailable);

        byte seed = (byte)((bus + address + command) & 0xFF);
        return op switch
        {
            SmbusOp.ReadByte  => new SmbusResult(SmbusStatus.Ok, new[] { seed }),
            SmbusOp.ReadWord  => new SmbusResult(SmbusStatus.Ok, new[] { seed, (byte)(seed + 1) }),
            SmbusOp.ReadBlock => new SmbusResult(SmbusStatus.Ok, MakeBlock(seed, length)),
            _ => SmbusResult.Fail(SmbusStatus.BadRequest)
        };
    }

    private static byte[] MakeBlock(byte seed, int length)
    {
        int n = Math.Clamp(length, 1, 32);
        var data = new byte[n];
        for (int i = 0; i < n; i++) data[i] = (byte)(seed + i);
        return data;
    }
}
