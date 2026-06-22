namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| SteelSeriesController                                                       |
|                                                                            |
|   SteelSeries USB-HID mice (VID 0x1038), reproduced as protocol FACTS. Board-|
|   independent, user-mode, opt-in (AllowHidRgb), reduced assurance. Two zone- |
|   based protocols (the clean, validated ones — the per-key Apex keyboards    |
|   need large key tables and are NOT implemented here, see the exclusion      |
|   list): Rival 3 (cmd 0x05, 8-byte) and Aerox 3/5 (cmd 0x21, 65-byte + a     |
|   one-time software-mode enable 0x2D). All OUTPUT reports, report id 0x00,   |
|   color order R,G,B. No CRC. Zones are single LEDs; SetAll colors them all.  |
\*---------------------------------------------------------------------------*/
internal static class SteelSeries
{
    public const ushort VendorId = 0x1038;
    public const int CommandInterface = 3;
    public const ushort UsagePage = 0xFFC0;
    public const ushort Usage = 0x0001;
    public const byte FullBrightness = 0x64;
}

/*-- Rival 3 (cmd 0x05): per-zone 8-byte packet [00,05,00,zoneVal,R,G,B,brightness] --*/
internal sealed class SteelSeriesRival3Controller
{
    public static readonly ushort[] ProductIds = { 0x184C, 0x1824 };
    public const int ZoneCount = 4;             // Front, Middle, Rear, Logo (zone values 1..4)
    private const int Len = 8;

    private readonly HidDevice _hid;
    private readonly object _io = new();
    public SteelSeriesRival3Controller(HidDevice hid) { _hid = hid; }

    /// <summary>Per-zone color packet. zoneValue is 1-based (Front=1 .. Logo=4); brightness 0..0x64.</summary>
    public static byte[] BuildZone(int zoneValue, byte r, byte g, byte b, byte brightness)
    {
        var p = new byte[Len];
        p[0] = 0x00; p[1] = 0x05; p[2] = 0x00; p[3] = (byte)zoneValue;
        p[4] = r; p[5] = g; p[6] = b; p[7] = brightness;
        return p;
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            for (int z = 0; z < ZoneCount; z++)
            {
                (byte R, byte G, byte B) c = z < colors.Count ? colors[z] : ((byte)0, (byte)0, (byte)0);
                if (!_hid.SetOutputReport(BuildZone(z + 1, c.R, c.G, c.B, SteelSeries.FullBrightness))) return false;
            }
            return true;
        }
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var c = new (byte, byte, byte)[ZoneCount];
        for (int i = 0; i < ZoneCount; i++) c[i] = (r, g, b);
        return SetLeds(c);
    }
}

internal sealed class SteelSeriesRival3RgbController : IRgbController
{
    private readonly SteelSeriesRival3Controller _dev;
    public string Id => "ss.rival3";
    public string Label => "SteelSeries Rival 3";
    public int LedCount => SteelSeriesRival3Controller.ZoneCount;
    public RgbZoneKind Kind => RgbZoneKind.Mouse;
    public RgbTransport Transport => RgbTransport.UsbHid;
    public SteelSeriesRival3RgbController(HidDevice hid) { _dev = new SteelSeriesRival3Controller(hid); }
    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}

/*-- Aerox 3 / 5 (cmd 0x21): 65-byte per-zone, R,G,B at offset 3 + zone*3; software-mode enable 0x2D --*/
internal sealed class SteelSeriesAeroxController
{
    public readonly record struct Model(string Id, string Label, ushort Pid, int Zones);

    public static readonly Model[] KnownModels =
    {
        new("ss.aerox3", "SteelSeries Aerox 3", 0x1836, 3),
        new("ss.aerox3", "SteelSeries Aerox 3 Wireless", 0x1838, 3),
        new("ss.aerox3", "SteelSeries Aerox 3 Wireless", 0x183A, 3),
        new("ss.aerox5", "SteelSeries Aerox 5", 0x1850, 3),
        new("ss.aerox5", "SteelSeries Aerox 5 Wireless", 0x1852, 3),
        new("ss.aerox5", "SteelSeries Aerox 5 Wireless", 0x1854, 3),
    };

    private const int Len = 65;
    private readonly HidDevice _hid;
    private readonly int _zones;
    private readonly object _io = new();
    private bool _enabled;

    public SteelSeriesAeroxController(HidDevice hid, int zones) { _hid = hid; _zones = zones; }
    public int ZoneCount => _zones;

    /// <summary>Software-mode enable feature report ([00,2D]).</summary>
    public static byte[] BuildEnable() { var p = new byte[Len]; p[0] = 0x00; p[1] = 0x2D; return p; }

    /// <summary>Per-zone color (cmd 0x21, bitmask 1&lt;&lt;zone, R,G,B at 3 + zone*3).</summary>
    public static byte[] BuildZone(int zone, byte r, byte g, byte b)
    {
        var p = new byte[Len];
        p[0] = 0x00; p[1] = 0x21; p[2] = (byte)(1 << zone);
        int o = 3 + zone * 3;
        p[o] = r; p[o + 1] = g; p[o + 2] = b;
        return p;
    }

    /// <summary>Brightness (cmd 0x23).</summary>
    public static byte[] BuildBrightness(byte brightness)
    {
        var p = new byte[Len]; p[0] = 0x00; p[1] = 0x23; p[2] = brightness; return p;
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            if (!_enabled)
            {
                if (!_hid.SetFeature(BuildEnable())) return false;          // 0x2D init is a feature report
                _hid.SetOutputReport(BuildBrightness(SteelSeries.FullBrightness));
                _enabled = true;
            }
            for (int z = 0; z < _zones; z++)
            {
                (byte R, byte G, byte B) c = z < colors.Count ? colors[z] : ((byte)0, (byte)0, (byte)0);
                if (!_hid.SetOutputReport(BuildZone(z, c.R, c.G, c.B))) return false;
            }
            return true;
        }
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var c = new (byte, byte, byte)[_zones];
        for (int i = 0; i < _zones; i++) c[i] = (r, g, b);
        return SetLeds(c);
    }
}

internal sealed class SteelSeriesAeroxRgbController : IRgbController
{
    private readonly SteelSeriesAeroxController _dev;
    public string Id { get; }
    public string Label { get; }
    public int LedCount { get; }
    public RgbZoneKind Kind => RgbZoneKind.Mouse;
    public RgbTransport Transport => RgbTransport.UsbHid;

    public SteelSeriesAeroxRgbController(HidDevice hid, SteelSeriesAeroxController.Model m)
    {
        _dev = new SteelSeriesAeroxController(hid, m.Zones); Id = m.Id; Label = m.Label; LedCount = m.Zones;
    }

    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}

/*-- Apex 3 keyboards: 8-zone (cmd 0x21, one 65-byte packet) and T-zone (cmd 0x0A brightness + 0x0B
     color, 33-byte). Both are plain zone arrays (no per-key table). OUTPUT reports, report id 0x00. --*/
internal sealed class SteelSeriesApex3Controller
{
    /// <summary>A keyboard model and which zone protocol + interface match it uses.</summary>
    public readonly record struct Model(string Id, string Label, ushort Pid, bool TZone, int Zones,
                                        int Interface, ushort UsagePage, ushort Usage);

    public static readonly Model[] KnownModels =
    {
        // Apex 3 TKL = 8-zone (iface 1, usage FFC0:1). Apex 3 full = T-zone, 10 zones (iface 3).
        new("ss.apex3tkl", "SteelSeries Apex 3 TKL", 0x1622, false, 8,  1, 0xFFC0, 0x0001),
        new("ss.apex3",    "SteelSeries Apex 3",     0x161A, true,  10, 3, 0xFFC0, 0x0001),
    };

    private const int EightLen = 65, TZoneLen = 33;
    private const byte EightMaxBright = 0x10, TZoneMaxBright = 0x64;

    private readonly HidDevice _hid;
    private readonly Model _m;
    private readonly object _io = new();

    public SteelSeriesApex3Controller(HidDevice hid, Model m) { _hid = hid; _m = m; }
    public int ZoneCount => _m.Zones;

    /// <summary>8-zone color: [00,21,FF, R,G,B ×8] (bitmask 0xFF = all zones).</summary>
    public static byte[] BuildEightZone(IReadOnlyList<(byte R, byte G, byte B)> c)
    {
        var p = new byte[EightLen];
        p[0] = 0x00; p[1] = 0x21; p[2] = 0xFF;
        for (int i = 0; i < 8; i++)
        {
            (byte R, byte G, byte B) col = i < c.Count ? c[i] : ((byte)0, (byte)0, (byte)0);
            int o = 3 + i * 3; p[o] = col.R; p[o + 1] = col.G; p[o + 2] = col.B;
        }
        return p;
    }

    public static byte[] BuildEightBrightness(byte b) { var p = new byte[EightLen]; p[1] = 0x23; p[2] = b; return p; }

    /// <summary>T-zone color: [00,0B, _, R,G,B ×zones].</summary>
    public static byte[] BuildTZoneColor(IReadOnlyList<(byte R, byte G, byte B)> c, int zones)
    {
        var p = new byte[TZoneLen];
        p[0] = 0x00; p[1] = 0x0B;
        for (int i = 0; i < zones; i++)
        {
            (byte R, byte G, byte B) col = i < c.Count ? c[i] : ((byte)0, (byte)0, (byte)0);
            int o = 3 + i * 3; p[o] = col.R; p[o + 1] = col.G; p[o + 2] = col.B;
        }
        return p;
    }

    public static byte[] BuildTZoneBrightness(byte b) { var p = new byte[TZoneLen]; p[1] = 0x0A; p[3] = b; return p; }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            if (_m.TZone)
                return _hid.SetOutputReport(BuildTZoneBrightness(TZoneMaxBright))
                    && _hid.SetOutputReport(BuildTZoneColor(colors, _m.Zones));
            return _hid.SetOutputReport(BuildEightBrightness(EightMaxBright))
                && _hid.SetOutputReport(BuildEightZone(colors));
        }
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var c = new (byte, byte, byte)[_m.Zones];
        for (int i = 0; i < _m.Zones; i++) c[i] = (r, g, b);
        return SetLeds(c);
    }
}

internal sealed class SteelSeriesApex3RgbController : IRgbController
{
    private readonly SteelSeriesApex3Controller _dev;
    public string Id { get; }
    public string Label { get; }
    public int LedCount { get; }
    public RgbZoneKind Kind => RgbZoneKind.Keyboard;
    public RgbTransport Transport => RgbTransport.UsbHid;

    public SteelSeriesApex3RgbController(HidDevice hid, SteelSeriesApex3Controller.Model m)
    {
        _dev = new SteelSeriesApex3Controller(hid, m); Id = m.Id; Label = m.Label; LedCount = m.Zones;
    }

    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}

/*-- OldApex 5-zone RGBA keyboards (Apex OG / 350): output report, cmd 0x07, R,G,B,Alpha per zone.
     Final wire buffer prepends the 0x00 report id (so cmd is at index 1). --*/
internal sealed class SteelSeriesOldApexController
{
    public static readonly ushort[] ProductIds = { 0x1202, 0x1206 };
    public const int Interface = 0;
    public const int ZoneCount = 5;             // QWERTY, Tenkey, Function, MX, Logo
    private const int Len = 33;
    private const byte Alpha = 0xFF;

    private readonly HidDevice _hid;
    private readonly object _io = new();
    public SteelSeriesOldApexController(HidDevice hid) { _hid = hid; }

    /// <summary>Wire buffer: [00,07,00, (R,G,B,A)×5]. Color order R,G,B,Alpha.</summary>
    public static byte[] BuildColor(IReadOnlyList<(byte R, byte G, byte B)> c)
    {
        var p = new byte[Len];
        p[0] = 0x00; p[1] = 0x07; p[2] = 0x00;
        for (int i = 0; i < ZoneCount; i++)
        {
            (byte R, byte G, byte B) col = i < c.Count ? c[i] : ((byte)0, (byte)0, (byte)0);
            int o = 3 + i * 4; p[o] = col.R; p[o + 1] = col.G; p[o + 2] = col.B; p[o + 3] = Alpha;
        }
        return p;
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io) return _hid.SetOutputReport(BuildColor(colors));
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var c = new (byte, byte, byte)[ZoneCount];
        for (int i = 0; i < ZoneCount; i++) c[i] = (r, g, b);
        return SetLeds(c);
    }
}

internal sealed class SteelSeriesOldApexRgbController : IRgbController
{
    private readonly SteelSeriesOldApexController _dev;
    public string Id => "ss.apexog";
    public string Label => "SteelSeries Apex (OG/350)";
    public int LedCount => SteelSeriesOldApexController.ZoneCount;
    public RgbZoneKind Kind => RgbZoneKind.Keyboard;
    public RgbTransport Transport => RgbTransport.UsbHid;
    public SteelSeriesOldApexRgbController(HidDevice hid) { _dev = new SteelSeriesOldApexController(hid); }
    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}

/*-- Sensei / Rival 310 2-zone mice: output report, cmd 0x5B, one packet per zone (color duplicated). --*/
internal sealed class SteelSeriesSenseiController
{
    public static readonly ushort[] ProductIds = { 0x1832, 0x1834, 0x1722, 0x1720, 0x171E, 0x1736 };
    public const int Interface = 0;
    public const int ZoneCount = 2;             // Logo, Scroll Wheel
    private const int Len = 66;

    private readonly HidDevice _hid;
    private readonly object _io = new();
    public SteelSeriesSenseiController(HidDevice hid) { _hid = hid; }

    /// <summary>Wire buffer for one zone: [00,5B,_,zone, ... 0x14=01, 0x1C=01, RGB@1D, RGB@20].</summary>
    public static byte[] BuildZone(int zone, byte r, byte g, byte b)
    {
        var p = new byte[Len];
        p[0] = 0x00; p[1] = 0x5B; p[3] = (byte)zone;
        p[0x14] = 0x01;          // static-color flag
        p[0x1C] = 0x01;          // number of colors
        p[0x1D] = r; p[0x1E] = g; p[0x1F] = b;   // color copy #1
        p[0x20] = r; p[0x21] = g; p[0x22] = b;   // color copy #2
        return p;
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            for (int z = 0; z < ZoneCount; z++)
            {
                (byte R, byte G, byte B) c = z < colors.Count ? colors[z] : ((byte)0, (byte)0, (byte)0);
                if (!_hid.SetOutputReport(BuildZone(z, c.R, c.G, c.B))) return false;
            }
            return true;
        }
    }

    public bool SetAll(byte r, byte g, byte b) => SetLeds(new[] { (r, g, b), (r, g, b) });
}

internal sealed class SteelSeriesSenseiRgbController : IRgbController
{
    private readonly SteelSeriesSenseiController _dev;
    public string Id => "ss.sensei";
    public string Label => "SteelSeries Sensei / Rival 310";
    public int LedCount => SteelSeriesSenseiController.ZoneCount;
    public RgbZoneKind Kind => RgbZoneKind.Mouse;
    public RgbTransport Transport => RgbTransport.UsbHid;
    public SteelSeriesSenseiRgbController(HidDevice hid) { _dev = new SteelSeriesSenseiController(hid); }
    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}

/*-- Rival 100 / 300 legacy mice: output report (0x00 prefix), per-zone color. Rival 100 = 1 zone,
     cmd 0x05, zone byte 0x00. Rival 300 = 2 zones, cmd 0x08, zone byte = zone+1. A DIRECT effect
     (cmd 0x07, 0x01) is latched once. NOTE: Rival 600 stays excluded (anomalous report framing). --*/
internal sealed class SteelSeriesRivalLegacyController
{
    public readonly record struct Model(string Id, string Label, ushort[] Pids, int Zones, byte ColorCmd, bool ZonePlusOne);

    public static readonly Model[] KnownModels =
    {
        new("ss.rival100", "SteelSeries Rival 100", new ushort[] { 0x1702, 0x170C, 0x1814, 0x1816, 0x1729 }, 1, 0x05, false),
        new("ss.rival300", "SteelSeries Rival 300", new ushort[] { 0x1710, 0x1714, 0x1394, 0x1716, 0x171A, 0x1392, 0x1718 }, 2, 0x08, true),
    };

    public const int Interface = 0;
    private const int Len = 10;

    private readonly HidDevice _hid;
    private readonly Model _m;
    private readonly object _io = new();
    private bool _latched;
    public SteelSeriesRivalLegacyController(HidDevice hid, Model m) { _hid = hid; _m = m; }
    public int ZoneCount => _m.Zones;

    private byte ZoneByte(int zone) => _m.ZonePlusOne ? (byte)(zone + 1) : (byte)0x00;

    /// <summary>DIRECT-effect latch packet (cmd 0x07, effect 0x01) for one zone.</summary>
    public static byte[] BuildModeDirect(byte zoneByte)
    { var p = new byte[Len]; p[1] = 0x07; p[2] = zoneByte; p[3] = 0x01; return p; }

    /// <summary>Per-zone color packet ([00, colorCmd, zoneByte, R, G, B]).</summary>
    public static byte[] BuildColor(byte colorCmd, byte zoneByte, byte r, byte g, byte b)
    { var p = new byte[Len]; p[1] = colorCmd; p[2] = zoneByte; p[3] = r; p[4] = g; p[5] = b; return p; }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            if (!_latched)
            {
                for (int z = 0; z < _m.Zones; z++) if (!_hid.SetOutputReport(BuildModeDirect(ZoneByte(z)))) return false;
                _latched = true;
            }
            for (int z = 0; z < _m.Zones; z++)
            {
                (byte R, byte G, byte B) c = z < colors.Count ? colors[z] : ((byte)0, (byte)0, (byte)0);
                if (!_hid.SetOutputReport(BuildColor(_m.ColorCmd, ZoneByte(z), c.R, c.G, c.B))) { _latched = false; return false; }
            }
            return true;
        }
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var c = new (byte, byte, byte)[_m.Zones];
        for (int i = 0; i < _m.Zones; i++) c[i] = (r, g, b);
        return SetLeds(c);
    }
}

internal sealed class SteelSeriesRivalLegacyRgbController : IRgbController
{
    private readonly SteelSeriesRivalLegacyController _dev;
    public string Id { get; }
    public string Label { get; }
    public int LedCount { get; }
    public RgbZoneKind Kind => RgbZoneKind.Mouse;
    public RgbTransport Transport => RgbTransport.UsbHid;

    public SteelSeriesRivalLegacyRgbController(HidDevice hid, SteelSeriesRivalLegacyController.Model m)
    {
        _dev = new SteelSeriesRivalLegacyController(hid, m); Id = m.Id; Label = m.Label; LedCount = m.Zones;
    }

    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}
