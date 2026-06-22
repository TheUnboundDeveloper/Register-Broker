namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| EvgaMbController                                                            |
|                                                                            |
|   EVGA "ACX 30" motherboard RGB on the host SMBus (address 0x28), reproduced |
|   as register FACTS. Single global color. Plain SMBus byte writes bracketed  |
|   by an unlock/lock handshake on the CONTROL register:                       |
|     unlock: 0x0E=0xE5, 0x0E=0xE9, read 0x0E                                  |
|     color : 0x09=R, 0x0A=G, 0x0B=B   (after a one-time STATIC mode latch)    |
|     lock  : 0x0E=0xE0, read 0x0E                                            |
|   EvgaMb brick-guard class (0x28 only). The controller PERSISTS each update  |
|   to NVRAM, so identical colors are de-duplicated to avoid flash wear.       |
\*---------------------------------------------------------------------------*/
internal sealed class EvgaMbController
{
    public const int LedCount = 1;                 // single global color (hardware fact)

    private const int REG_DETECT  = 0x01;           // bit0 set => present
    private const int REG_RED     = 0x09;
    private const int REG_GREEN   = 0x0A;
    private const int REG_BLUE    = 0x0B;
    private const int REG_MODE    = 0x0C;
    private const int REG_CONTROL = 0x0E;           // unlock/lock
    private const int REG_MODE_A  = 0x21;           // mode-set helper
    private const int REG_MODE_B  = 0x22;           // mode-set helper
    private const int MODE_STATIC = 0x01;

    private readonly ISmbusBackend _drv;
    private readonly int _bus, _addr;
    private readonly object _io = new();
    private bool _modeLatched;
    private (byte R, byte G, byte B)? _last;        // NVRAM-wear dedup

    public EvgaMbController(ISmbusBackend drv, int bus, int addr) { _drv = drv; _bus = bus; _addr = addr; }

    private bool W(int reg, int val) => _drv.TryWrite(_bus, _addr, reg, val, word: false, RgbWriteClass.EvgaMb, out _);

    private bool Unlock()
    {
        if (!W(REG_CONTROL, 0xE5) || !W(REG_CONTROL, 0xE9)) return false;
        _drv.Read(_bus, _addr, REG_CONTROL, SmbusOp.ReadByte, 1);   // required settle read (discarded)
        return true;
    }

    private bool Lock()
    {
        if (!W(REG_CONTROL, 0xE0)) return false;
        _drv.Read(_bus, _addr, REG_CONTROL, SmbusOp.ReadByte, 1);
        return true;
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        lock (_io)
        {
            if (_last is { } p && p.R == r && p.G == g && p.B == b) return true;   // skip NVRAM re-write
            if (!Unlock()) return false;
            if (!_modeLatched)
            {
                // STATIC mode: 0x21=0xE5, 0x22=0xE7, then MODE=STATIC.
                if (!W(REG_MODE_A, 0xE5) || !W(REG_MODE_B, 0xE7) || !W(REG_MODE, MODE_STATIC)) { Lock(); return false; }
                _modeLatched = true;
            }
            bool ok = W(REG_RED, r) && W(REG_GREEN, g) && W(REG_BLUE, b);
            Lock();
            if (ok) _last = (r, g, b);
            return ok;
        }
    }

    public bool SetColors(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        (byte R, byte G, byte B) c = colors.Count > 0 ? colors[0] : ((byte)0, (byte)0, (byte)0);
        return SetAll(c.R, c.G, c.B);
    }

    /// <summary>Identifies an EVGA ACX30 controller: register 0x01 read nonzero with bit0 set. Reads only.</summary>
    public static bool TryIdentify(ISmbusBackend drv, int bus, int addr)
    {
        SmbusResult r = drv.Read(bus, addr, REG_DETECT, SmbusOp.ReadByte, 1);
        return r.Ok && r.Data.Length >= 1 && r.Data[0] > 0 && (r.Data[0] & 1) != 0;
    }
}

internal sealed class EvgaMbRgbController : IRgbController
{
    private readonly EvgaMbController _dev;
    public string Id { get; }
    public string Label { get; }
    public int LedCount => EvgaMbController.LedCount;
    public RgbZoneKind Kind => RgbZoneKind.Mb12V;
    public RgbTransport Transport => RgbTransport.Smbus;

    public EvgaMbRgbController(ISmbusBackend backend, string id, string label, int bus, int addr)
    {
        _dev = new EvgaMbController(backend, bus, addr); Id = id; Label = label;
    }

    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors) => _dev.SetColors(colors);
}
