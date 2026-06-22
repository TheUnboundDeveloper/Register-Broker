using System.Threading;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| AsrockMbController                                                          |
|                                                                            |
|   ASRock motherboard RGB on the host SMBus (address 0x6A), reproduced as     |
|   register FACTS. One device, three firmware protocol variants distinguished |
|   by the firmware-major byte read from register 0x00 (block read, length 2):|
|     0x01 = ASR RGB     (single LED; color -> register == mode byte)         |
|     0x02 = Polychrome V1 (per-zone; color -> register == mode byte)         |
|     0x03 = Polychrome V2 (per-zone; color -> fixed register 0x34)           |
|   This drives a SOLID whole-board STATIC color across all populated zones    |
|   (the broker's no-effects scope). All transactions are SMBus BLOCK writes   |
|   (even single bytes), each followed by ~1 ms (firmware drops faster writes).|
|   AsrockMb brick-guard class (0x6A only). HW-UNVALIDATED.                    |
\*---------------------------------------------------------------------------*/
internal sealed class AsrockMbController
{
    public const int VariantAsr = 0x01, VariantV1 = 0x02, VariantV2 = 0x03;

    private const int REG_FIRMWARE  = 0x00;
    private const int REG_MODE      = 0x30;
    private const int REG_ZONE_SEL  = 0x31;     // zone/LED select
    private const int REG_SET_ALL   = 0x32;     // V1 "set all" clear / V2 select-single
    private const int REG_ZONE_SIZE = 0x33;     // 6-byte zone LED-count config (V1/V2)
    private const int REG_COLOR_V2  = 0x34;     // V2 fixed color register
    private const int MODE_STATIC   = 0x11;

    private readonly ISmbusBackend _drv;
    private readonly int _bus, _addr, _variant;
    private readonly int[] _zones;              // active zone indices (V1/V2); {0} for ASR
    private readonly object _io = new();
    private bool _modeLatched;

    public AsrockMbController(ISmbusBackend drv, int bus, int addr, int variant, int[] zones)
    {
        _drv = drv; _bus = bus; _addr = addr; _variant = variant; _zones = zones;
    }

    public int ZoneCount => _zones.Length;

    /// <summary>One SMBus block write to <paramref name="reg"/> + the firmware settle delay.</summary>
    private bool Blk(int reg, ReadOnlySpan<byte> data)
    {
        bool ok = _drv.TryWriteBlock(_bus, _addr, reg, data, RgbWriteClass.AsrockMb, out _);
        Thread.Sleep(1);
        return ok;
    }

    private bool SetZoneStatic(int zone, byte r, byte g, byte b)
    {
        Span<byte> rgb = stackalloc byte[3] { r, g, b };
        switch (_variant)
        {
            case VariantV2:
                return Blk(REG_ZONE_SEL, stackalloc byte[1] { (byte)zone }) && Blk(REG_COLOR_V2, rgb);
            case VariantV1:
                return Blk(REG_ZONE_SEL, stackalloc byte[1] { (byte)zone }) && Blk(MODE_STATIC, rgb);
            default: // ASR
                return Blk(REG_ZONE_SEL, stackalloc byte[1] { (byte)zone }) && Blk(MODE_STATIC, rgb);
        }
    }

    private bool LatchModeStatic()
    {
        foreach (int zone in _zones)
        {
            switch (_variant)
            {
                case VariantV2:
                    if (!Blk(REG_MODE, stackalloc byte[1] { MODE_STATIC }) || !Blk(REG_SET_ALL, stackalloc byte[1] { 0x00 })) return false;
                    break;
                case VariantV1:
                    if (!Blk(REG_SET_ALL, stackalloc byte[1] { 0x00 }) || !Blk(REG_ZONE_SEL, stackalloc byte[1] { (byte)zone })
                        || !Blk(REG_MODE, stackalloc byte[1] { MODE_STATIC })) return false;
                    break;
                default: // ASR (single zone)
                    if (!Blk(REG_MODE, stackalloc byte[1] { MODE_STATIC })) return false;
                    break;
            }
        }
        return true;
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        lock (_io)
        {
            if (!_modeLatched) { if (!LatchModeStatic()) return false; _modeLatched = true; }
            foreach (int zone in _zones)
                if (!SetZoneStatic(zone, r, g, b)) { _modeLatched = false; return false; }
            return true;
        }
    }

    public bool SetColors(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        (byte R, byte G, byte B) c = colors.Count > 0 ? colors[0] : ((byte)0, (byte)0, (byte)0);
        return SetAll(c.R, c.G, c.B);
    }

    /// <summary>
    /// Identifies an ASRock controller and its variant: block-read register 0x00 must return exactly
    /// 2 bytes; byte[0] is the firmware major (0x01/0x02/0x03). Returns the active zone indices (read
    /// from the 6-byte 0x33 config for V1/V2; {0} for ASR). Non-destructive (reads only).
    /// </summary>
    public static bool TryIdentify(ISmbusBackend drv, int bus, int addr, out int variant, out int[] zones)
    {
        variant = 0; zones = Array.Empty<int>();
        SmbusResult fw = drv.Read(bus, addr, REG_FIRMWARE, SmbusOp.ReadBlock, 32);
        if (!fw.Ok || fw.Data.Length != 2) return false;
        variant = fw.Data[0];
        if (variant is not (VariantAsr or VariantV1 or VariantV2)) { variant = 0; return false; }

        if (variant == VariantAsr) { zones = new[] { 0 }; return true; }

        SmbusResult zs = drv.Read(bus, addr, REG_ZONE_SIZE, SmbusOp.ReadBlock, 32);
        if (!zs.Ok || zs.Data.Length != 6) return false;
        var active = new List<int>();
        for (int i = 0; i < 6; i++) if (zs.Data[i] > 0) active.Add(i);
        if (active.Count == 0) return false;
        zones = active.ToArray();
        return true;
    }
}

internal sealed class AsrockMbRgbController : IRgbController
{
    private readonly AsrockMbController _dev;
    public string Id { get; }
    public string Label { get; }
    public int LedCount => 1;                    // solid whole-board color
    public RgbZoneKind Kind => RgbZoneKind.Mb12V;
    public RgbTransport Transport => RgbTransport.Smbus;

    public AsrockMbRgbController(ISmbusBackend backend, string id, string label, int bus, int addr, int variant, int[] zones)
    {
        _dev = new AsrockMbController(backend, bus, addr, variant, zones); Id = id; Label = label;
    }

    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors) => _dev.SetColors(colors);
}
