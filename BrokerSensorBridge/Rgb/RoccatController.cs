namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| RoccatController                                                            |
|                                                                            |
|   Roccat USB-HID mice (VID 0x1E7D), reproduced as protocol FACTS. Board-     |
|   independent, user-mode, opt-in (AllowHidRgb), reduced assurance. Both use  |
|   FEATURE reports (report id is a real byte at index 0 — no 0x00 prefix):    |
|     * Kone Aimo — 11 LEDs, report 0x0D (46 bytes), 4 bytes/LED (R,G,B,pad).  |
|     * Kone Pro  — 2 LEDs, DIRECT report 0x0D (11 bytes), 3 bytes/LED, after  |
|                   a control/enable packet (report 0x0E).                     |
|   The Roccat Vulcan keyboards are NOT here — per-key, layout-dependent, with  |
|   a brick-sensitive two-interface init sequence (see the coverage doc).      |
\*---------------------------------------------------------------------------*/
internal static class Roccat
{
    public const ushort VendorId = 0x1E7D;
    /// <summary>Control/enable feature report (0x0E): direct=1 enters software control.</summary>
    public static byte[] BuildControl(byte direct) => new byte[] { 0x0E, 0x06, 0x01, direct, 0x00, 0xFF };
}

/*-- Kone Aimo: 11 LEDs, feature report 0x0D, 46 bytes --*/
internal sealed class RoccatKoneAimoController
{
    public static readonly ushort[] ProductIds = { 0x2E27, 0x2E2C };
    public const int Interface = 0;
    public const ushort UsagePage = 0x000B;
    public const ushort Usage = 0x0000;
    public const int LedCount = 11;
    private const int Len = 46;

    private readonly HidDevice _hid;
    private readonly object _io = new();
    private bool _inited;
    public RoccatKoneAimoController(HidDevice hid) { _hid = hid; }

    /// <summary>Color feature report: [0x0D, 0x2E, then 11×(R,G,B,pad)].</summary>
    public static byte[] BuildColor(IReadOnlyList<(byte R, byte G, byte B)> c)
    {
        var p = new byte[Len];
        p[0] = 0x0D; p[1] = 0x2E;
        for (int i = 0; i < LedCount; i++)
        {
            (byte R, byte G, byte B) col = i < c.Count ? c[i] : ((byte)0, (byte)0, (byte)0);
            int o = 2 + i * 4; p[o] = col.R; p[o + 1] = col.G; p[o + 2] = col.B;
        }
        return p;
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            if (!_inited) { _hid.SetFeature(Roccat.BuildControl(0x01)); _inited = true; }
            return _hid.SetFeature(BuildColor(colors));
        }
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var c = new (byte, byte, byte)[LedCount];
        for (int i = 0; i < LedCount; i++) c[i] = (r, g, b);
        return SetLeds(c);
    }
}

internal sealed class RoccatKoneAimoRgbController : IRgbController
{
    private readonly RoccatKoneAimoController _dev;
    public string Id => "roccat.koneaimo";
    public string Label => "Roccat Kone Aimo";
    public int LedCount => RoccatKoneAimoController.LedCount;
    public RgbZoneKind Kind => RgbZoneKind.Mouse;
    public RgbTransport Transport => RgbTransport.UsbHid;
    public RoccatKoneAimoRgbController(HidDevice hid) { _dev = new RoccatKoneAimoController(hid); }
    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}

/*-- Kone Pro: 2 LEDs, DIRECT feature report 0x0D (11 bytes), after control enable 0x0E --*/
internal sealed class RoccatKoneProController
{
    public const ushort ProductId = 0x2C88;
    public const int Interface = 3;
    public const ushort UsagePage = 0xFF01;
    public const ushort Usage = 0x0001;
    public const int LedCount = 2;
    private const int Len = 11;

    private readonly HidDevice _hid;
    private readonly object _io = new();
    private bool _inited;
    public RoccatKoneProController(HidDevice hid) { _hid = hid; }

    /// <summary>Direct color feature report: [0x0D, 0x0B, then 2×(R,G,B)].</summary>
    public static byte[] BuildColor(IReadOnlyList<(byte R, byte G, byte B)> c)
    {
        var p = new byte[Len];
        p[0] = 0x0D; p[1] = 0x0B;
        for (int i = 0; i < LedCount; i++)
        {
            (byte R, byte G, byte B) col = i < c.Count ? c[i] : ((byte)0, (byte)0, (byte)0);
            int o = 2 + i * 3; p[o] = col.R; p[o + 1] = col.G; p[o + 2] = col.B;
        }
        return p;
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            if (!_inited) { _hid.SetFeature(Roccat.BuildControl(0x01)); _inited = true; }
            return _hid.SetFeature(BuildColor(colors));
        }
    }

    public bool SetAll(byte r, byte g, byte b) => SetLeds(new[] { (r, g, b), (r, g, b) });
}

internal sealed class RoccatKoneProRgbController : IRgbController
{
    private readonly RoccatKoneProController _dev;
    public string Id => "roccat.konepro";
    public string Label => "Roccat Kone Pro";
    public int LedCount => RoccatKoneProController.LedCount;
    public RgbZoneKind Kind => RgbZoneKind.Mouse;
    public RgbTransport Transport => RgbTransport.UsbHid;
    public RoccatKoneProRgbController(HidDevice hid) { _dev = new RoccatKoneProController(hid); }
    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}
