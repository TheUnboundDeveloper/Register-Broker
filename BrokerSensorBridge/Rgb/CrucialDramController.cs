namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| CrucialDramController                                                       |
|                                                                            |
|   Crucial Ballistix DDR4 RGB, reproduced as register FACTS (our own code).  |
|   The controller is an ENE-style part: a 16-bit register is selected by a    |
|   byte-swapped word write to command 0x00, then data is read (0x81) / written|
|   (0x01 byte, 0x03 block). Color is PLANAR — 8 reds, then 8 greens, then 8   |
|   blues — each plane one 8-byte block write. Fixed 8 LEDs, no CRC, no apply. |
|   Lives at SMBus 0x20-0x27 / 0x39-0x3C (CrucialDram brick-guard class).      |
|   Identity gate: regs 0xA0..0xAF read back 0x00..0x0F AND a "Micron" string  |
|   at register 0x1025 or 0x1030. (Address remap from 0x27 is NOT performed —  |
|   we drive sticks already resident in the window; flagged for HW bring-up.)  |
\*---------------------------------------------------------------------------*/
internal sealed class CrucialDramController
{
    public const int LedCount = 8;                 // fixed, single linear zone (hardware fact)

    private const int REG_RED_PLANE   = 0x8300;     // R[0..7] at base+led
    private const int REG_GREEN_PLANE = 0x8340;     // G plane (R+0x40)
    private const int REG_BLUE_PLANE  = 0x8380;     // B plane (R+0x80)
    private const int REG_MICRON_1    = 0x1025;     // "Micron" signature, primary location
    private const int REG_MICRON_2    = 0x1030;     // "Micron" signature, fallback location

    private readonly ISmbusBackend _drv;
    private readonly int _bus, _addr;
    private readonly object _io = new();

    public CrucialDramController(ISmbusBackend drv, int bus, int addr) { _drv = drv; _bus = bus; _addr = addr; }

    private static int Swap(int reg) => ((reg << 8) & 0xFF00) | ((reg >> 8) & 0x00FF);

    /// <summary>Selects a 16-bit register (byte-swapped word to command 0x00), then block-writes its bytes (command 0x03).</summary>
    private bool WriteBlockAt(int reg, ReadOnlySpan<byte> data)
    {
        if (!_drv.TryWrite(_bus, _addr, 0x00, Swap(reg), word: true, RgbWriteClass.CrucialDram, out _)) return false;
        return _drv.TryWriteBlock(_bus, _addr, 0x03, data, RgbWriteClass.CrucialDram, out _);
    }

    private bool ReadByteAt(int reg, out byte value)
    {
        value = 0;
        if (!_drv.TryWrite(_bus, _addr, 0x00, Swap(reg), word: true, RgbWriteClass.CrucialDram, out _)) return false;
        SmbusResult r = _drv.Read(_bus, _addr, 0x81, SmbusOp.ReadByte, 1);
        if (!r.Ok || r.Data.Length < 1) return false;
        value = r.Data[0];
        return true;
    }

    /// <summary>Writes a DIRECT frame: three 8-byte planar block writes (R plane, G plane, B plane). No apply/CRC.</summary>
    public bool SetColors(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        Span<byte> red = stackalloc byte[LedCount], grn = stackalloc byte[LedCount], blu = stackalloc byte[LedCount];
        for (int i = 0; i < LedCount; i++)
        {
            (byte R, byte G, byte B) c = i < colors.Count ? colors[i] : ((byte)0, (byte)0, (byte)0);
            red[i] = c.R; grn[i] = c.G; blu[i] = c.B;
        }
        lock (_io)
        {
            return WriteBlockAt(REG_RED_PLANE, red)
                && WriteBlockAt(REG_GREEN_PLANE, grn)
                && WriteBlockAt(REG_BLUE_PLANE, blu);
        }
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var colors = new (byte, byte, byte)[LedCount];
        for (int i = 0; i < LedCount; i++) colors[i] = (r, g, b);
        return SetColors(colors);
    }

    /// <summary>
    /// Identifies a Crucial Ballistix controller: regs 0xA0..0xAF must read 0x00..0x0F, and a "Micron"
    /// ASCII string must be readable at register 0x1025 or 0x1030. Non-destructive (reads only).
    /// </summary>
    public static bool TryIdentify(ISmbusBackend drv, int bus, int addr)
    {
        // Incrementing-register signature: reg 0xA0+i (plain command-byte read) == i.
        for (int i = 0; i < 16; i++)
        {
            SmbusResult r = drv.Read(bus, addr, 0xA0 + i, SmbusOp.ReadByte, 1);
            if (!r.Ok || r.Data.Length < 1 || r.Data[0] != i) return false;
        }
        var probe = new CrucialDramController(drv, bus, addr);
        return probe.ReadsMicron(REG_MICRON_1) || probe.ReadsMicron(REG_MICRON_2);
    }

    private bool ReadsMicron(int baseReg)
    {
        ReadOnlySpan<byte> want = "Micron"u8;
        for (int i = 0; i < want.Length; i++)
            if (!ReadByteAt(baseReg + i, out byte b) || b != want[i]) return false;
        return true;
    }
}

internal sealed class CrucialDramRgbController : IRgbController
{
    private readonly CrucialDramController _dev;
    public string Id { get; }
    public string Label { get; }
    public int LedCount => CrucialDramController.LedCount;
    public RgbZoneKind Kind => RgbZoneKind.Dram;
    public RgbTransport Transport => RgbTransport.Smbus;

    public CrucialDramRgbController(ISmbusBackend backend, string id, string label, int bus, int addr)
    {
        _dev = new CrucialDramController(backend, bus, addr); Id = id; Label = label;
    }

    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors) => _dev.SetColors(colors);
}
