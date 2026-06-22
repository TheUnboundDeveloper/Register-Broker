namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| CorsairVengeanceController                                                  |
|                                                                            |
|   The ORIGINAL Corsair Vengeance RGB DDR4 (single solid-color per stick),   |
|   reproduced as register FACTS — distinct from the newer per-LED            |
|   CorsairDramController. One logical LED. Plain SMBus byte writes:           |
|     0xA4 = 0x00  (fade time = 0 -> static)                                   |
|     0xB0 = R, 0xB1 = G, 0xB2 = B                                            |
|     0xA6 = 0x00  (mode SINGLE -> commits/latches)                            |
|   Identity gate (non-destructive): registers 0xA0..0xAF all read back 0xBA. |
|   RGB window 0x58-0x5F only (CorsairVenDram class) — the device is also      |
|   reachable at 0x18-0x1F, but that aliases the JC42 DIMM-temp range and is   |
|   REFUSED in-kernel, so RGB writes can never disturb the thermal sensors.    |
\*---------------------------------------------------------------------------*/
internal sealed class CorsairVengeanceController
{
    public const int LedCount = 1;                 // single solid-color zone (hardware fact)
    private const byte Signature = 0xBA;            // 0xA0..0xAF all read this

    private const int REG_FADE_TIME = 0xA4;
    private const int REG_MODE      = 0xA6;
    private const int REG_RED       = 0xB0;
    private const int REG_GREEN     = 0xB1;
    private const int REG_BLUE      = 0xB2;
    private const int MODE_SINGLE   = 0x00;

    private readonly ISmbusBackend _drv;
    private readonly int _bus, _addr;
    private readonly object _io = new();

    public CorsairVengeanceController(ISmbusBackend drv, int bus, int addr) { _drv = drv; _bus = bus; _addr = addr; }

    private bool W(int reg, int val) => _drv.TryWrite(_bus, _addr, reg, val, word: false, RgbWriteClass.CorsairVenDram, out _);

    public bool SetAll(byte r, byte g, byte b)
    {
        lock (_io)
        {
            // Color regs first, then MODE=SINGLE last (the commit).
            return W(REG_FADE_TIME, 0x00) && W(REG_RED, r) && W(REG_GREEN, g) && W(REG_BLUE, b) && W(REG_MODE, MODE_SINGLE);
        }
    }

    public bool SetColors(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        (byte R, byte G, byte B) c = colors.Count > 0 ? colors[0] : ((byte)0, (byte)0, (byte)0);
        return SetAll(c.R, c.G, c.B);
    }

    /// <summary>Identifies a Corsair Vengeance stick: registers 0xA0..0xAF must all read 0xBA. Reads only.</summary>
    public static bool TryIdentify(ISmbusBackend drv, int bus, int addr)
    {
        for (int reg = 0xA0; reg <= 0xAF; reg++)
        {
            SmbusResult r = drv.Read(bus, addr, reg, SmbusOp.ReadByte, 1);
            if (!r.Ok || r.Data.Length < 1 || r.Data[0] != Signature) return false;
        }
        return true;
    }
}

internal sealed class CorsairVengeanceRgbController : IRgbController
{
    private readonly CorsairVengeanceController _dev;
    public string Id { get; }
    public string Label { get; }
    public int LedCount => CorsairVengeanceController.LedCount;
    public RgbZoneKind Kind => RgbZoneKind.Dram;
    public RgbTransport Transport => RgbTransport.Smbus;

    public CorsairVengeanceRgbController(ISmbusBackend backend, string id, string label, int bus, int addr)
    {
        _dev = new CorsairVengeanceController(backend, bus, addr); Id = id; Label = label;
    }

    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors) => _dev.SetColors(colors);
}
