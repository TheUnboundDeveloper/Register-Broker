namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| AsusRogAllyController                                                       |
|                                                                            |
|   ASUS ROG Ally / Ally X handheld over USB-HID (VID 0x0B05, PID 0x1ABE /     |
|   0x1B4C, interface 2, usage FF31:0076), reproduced as protocol FACTS.       |
|   FEATURE reports, 64 bytes, with a REAL (nonzero) report id in byte 0.       |
|   Fixed 4 LEDs (Left Stick ×2, Right Stick ×2). A one-time init handshake     |
|   ("ASUS Tech.Inc.") + brightness, then a single DIRECT feature report        |
|   (0x5A 0xD1) carries all 4 RGB triplets. No CRC. Board-independent, opt-in.  |
\*---------------------------------------------------------------------------*/
internal sealed class AsusRogAllyController
{
    public const ushort VendorId = 0x0B05;
    public const int Interface = 2;
    public const ushort UsagePage = 0xFF31, Usage = 0x0076;
    public const int LedCount = 4;
    public static readonly ushort[] ProductIds = { 0x1ABE, 0x1B4C };

    private const int Len = 64;
    // The init handshake string the firmware expects ("ASUS Tech.Inc.").
    private static readonly byte[] AsusTech = "ASUS Tech.Inc."u8.ToArray();

    private readonly HidDevice _hid;
    private readonly object _io = new();
    private bool _inited;
    public AsusRogAllyController(HidDevice hid) { _hid = hid; }

    public static byte[] BuildInit1() { var p = new byte[Len]; p[0] = 0x5D; p[1] = 0xB9; return p; }
    public static byte[] BuildInit2()
    { var p = new byte[Len]; p[0] = 0x5D; p[1] = 0x41; AsusTech.CopyTo(p, 2); return p; }
    public static byte[] BuildBrightness(byte b)
    { var p = new byte[Len]; p[0] = 0x5A; p[1] = 0xBA; p[2] = 0xC5; p[3] = 0xC4; p[4] = b; return p; }

    /// <summary>DIRECT feature report: [5A, D1, 08, 0C] then 4 LEDs of R,G,B from offset 4.</summary>
    public static byte[] BuildDirect(IReadOnlyList<(byte R, byte G, byte B)> c)
    {
        var p = new byte[Len]; p[0] = 0x5A; p[1] = 0xD1; p[2] = 0x08; p[3] = 0x0C;
        for (int i = 0; i < LedCount; i++)
        {
            (byte R, byte G, byte B) col = i < c.Count ? c[i] : ((byte)0, (byte)0, (byte)0);
            int o = 4 + i * 3; p[o] = col.R; p[o + 1] = col.G; p[o + 2] = col.B;
        }
        return p;
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            if (!_inited)
            {
                if (!_hid.SetFeature(BuildInit1()) || !_hid.SetFeature(BuildInit2())) return false;
                _hid.SetFeature(BuildBrightness(0x03));   // max brightness
                _inited = true;
            }
            return _hid.SetFeature(BuildDirect(colors));
        }
    }

    public bool SetAll(byte r, byte g, byte b)
        => SetLeds(new[] { (r, g, b), (r, g, b), (r, g, b), (r, g, b) });
}

internal sealed class AsusRogAllyRgbController : IRgbController
{
    private readonly AsusRogAllyController _dev;
    public string Id => "asus.rogally";
    public string Label => "ASUS ROG Ally";
    public int LedCount => AsusRogAllyController.LedCount;
    public RgbZoneKind Kind => RgbZoneKind.Cooler;   // handheld device LEDs (not a board/peripheral class)
    public RgbTransport Transport => RgbTransport.UsbHid;
    public AsusRogAllyRgbController(HidDevice hid) { _dev = new AsusRogAllyController(hid); }
    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}
