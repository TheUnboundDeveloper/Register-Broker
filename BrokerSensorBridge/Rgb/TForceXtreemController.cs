namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| TForceXtreemController                                                      |
|                                                                            |
|   T-Force Xtreem DDR4 RGB (an ENE controller), reproduced as register FACTS.|
|   ENE indirection: select a byte-swapped 16-bit register via command 0x00,  |
|   then read 0x81 / write 0x01 / block 0x03. DIRECT mode is latched once      |
|   (0xE020=1, apply 0xE02F=1); each LED is then a 3-byte block {R,B,G} at     |
|   0xE100 + 3*offset with NO per-frame apply. 15 LEDs on a FOLDED strip, so   |
|   the logical->physical index is remapped (0,14,1,13,...). Byte order R,B,G. |
|   Lives at 0x70-0x78 / 0x39-0x3D (XtreemDram brick-guard class). Identity:   |
|   regs 0x90..0xA0 read back 0x10..0x20. (Per-stick address remap from 0x77   |
|   is NOT performed; we drive sticks already resident in the window.)         |
\*---------------------------------------------------------------------------*/
internal sealed class TForceXtreemController
{
    public const int LedCount = 15;                 // fixed (hardware fact)

    private const int REG_DIRECT        = 0xE020;
    private const int REG_APPLY         = 0xE02F;
    private const int REG_COLORS_DIRECT = 0xE100;    // per-LED R,B,G triplets
    private const int APPLY_VAL         = 0x01;

    private readonly ISmbusBackend _drv;
    private readonly int _bus, _addr;
    private readonly object _io = new();
    private bool _directLatched;

    public TForceXtreemController(ISmbusBackend drv, int bus, int addr) { _drv = drv; _bus = bus; _addr = addr; }

    /// <summary>Folded-strip logical->physical LED index: even -> x/2, odd -> (count-1) - x/2.</summary>
    public static int FoldedOffset(int x, int count) => ((x & 1) != 0) ? (count - 1 - (x >> 1)) : (x >> 1);

    private static int Swap(int reg) => ((reg << 8) & 0xFF00) | ((reg >> 8) & 0x00FF);

    private bool SelectWriteByte(int reg, byte val)
    {
        if (!_drv.TryWrite(_bus, _addr, 0x00, Swap(reg), word: true, RgbWriteClass.XtreemDram, out _)) return false;
        return _drv.TryWrite(_bus, _addr, 0x01, val, word: false, RgbWriteClass.XtreemDram, out _);
    }

    private bool SelectWriteBlock(int reg, ReadOnlySpan<byte> data)
    {
        if (!_drv.TryWrite(_bus, _addr, 0x00, Swap(reg), word: true, RgbWriteClass.XtreemDram, out _)) return false;
        return _drv.TryWriteBlock(_bus, _addr, 0x03, data, RgbWriteClass.XtreemDram, out _);
    }

    private bool EnsureDirect()
    {
        if (_directLatched) return true;
        if (!SelectWriteByte(REG_DIRECT, 0x01)) return false;
        if (!SelectWriteByte(REG_APPLY, APPLY_VAL)) return false;
        _directLatched = true;
        return true;
    }

    public bool SetColors(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            if (!EnsureDirect()) { _directLatched = false; return false; }
            Span<byte> rbg = stackalloc byte[3];
            for (int led = 0; led < LedCount; led++)
            {
                (byte R, byte G, byte B) c = led < colors.Count ? colors[led] : ((byte)0, (byte)0, (byte)0);
                rbg[0] = c.R; rbg[1] = c.B; rbg[2] = c.G;        // ENE Xtreem byte order: R, B, G
                int off = FoldedOffset(led, LedCount);
                if (!SelectWriteBlock(REG_COLORS_DIRECT + 3 * off, rbg)) { _directLatched = false; return false; }
            }
            return true;
        }
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var colors = new (byte, byte, byte)[LedCount];
        for (int i = 0; i < LedCount; i++) colors[i] = (r, g, b);
        return SetColors(colors);
    }

    /// <summary>Identifies an Xtreem ENE controller: regs 0x90..0xA0 must read back 0x10..0x20. Reads only.</summary>
    public static bool TryIdentify(ISmbusBackend drv, int bus, int addr)
    {
        for (int reg = 0x90; reg <= 0xA0; reg++)
        {
            SmbusResult r = drv.Read(bus, addr, reg, SmbusOp.ReadByte, 1);
            if (!r.Ok || r.Data.Length < 1 || r.Data[0] != (reg - 0x80)) return false;
        }
        return true;
    }
}

internal sealed class TForceXtreemRgbController : IRgbController
{
    private readonly TForceXtreemController _dev;
    public string Id { get; }
    public string Label { get; }
    public int LedCount => TForceXtreemController.LedCount;
    public RgbZoneKind Kind => RgbZoneKind.Dram;
    public RgbTransport Transport => RgbTransport.Smbus;

    public TForceXtreemRgbController(ISmbusBackend backend, string id, string label, int bus, int addr)
    {
        _dev = new TForceXtreemController(backend, bus, addr); Id = id; Label = label;
    }

    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors) => _dev.SetColors(colors);
}
