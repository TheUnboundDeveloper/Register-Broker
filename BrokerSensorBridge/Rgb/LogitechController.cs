namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| LogitechController                                                          |
|                                                                            |
|   Logitech G-series USB-HID RGB (VID 0x046D), reproduced as protocol FACTS.  |
|   Board-independent, user-mode, opt-in (AllowHidRgb), reduced assurance.     |
|   Two controllers here, both using the VALIDATED solid/direct paths only     |
|   (the per-key matrix and the source-flagged-unverified G815/G915 mode       |
|   bytes are deliberately NOT implemented — see the exclusion list):          |
|     * G203 Lightsync mouse — 3 addressable LEDs via DIRECT writes.           |
|     * G810 / G910 / G Pro keyboards — whole-keyboard SOLID color via the     |
|       firmware STATIC mode (no per-key table needed).                        |
|   Transport: 20-byte OUTPUT reports, HID report id 0x11.                     |
\*---------------------------------------------------------------------------*/
internal static class Logitech
{
    public const ushort VendorId = 0x046D;
}

/*-- G203 Lightsync mouse: 3 LEDs, DIRECT mode --*/
internal sealed class LogitechG203LController
{
    public static readonly ushort[] ProductIds = { 0xC092, 0xC09D };
    public const int CommandInterface = 1;
    public const ushort UsagePage = 0xFF00;
    public const ushort Usage = 0x0002;
    public const int LedCount = 3;

    private const int Len = 20;
    private const byte ReportId = 0x11;

    private readonly HidDevice _hid;
    private readonly object _io = new();
    private bool _enabled;

    public LogitechG203LController(HidDevice hid) { _hid = hid; }

    /// <summary>Software-control enable (0x0E/0x50 0x01 0x03 0x07).</summary>
    public static byte[] BuildEnable()
    {
        var p = new byte[Len];
        p[0] = ReportId; p[1] = 0xFF; p[2] = 0x0E; p[3] = 0x50; p[4] = 0x01; p[5] = 0x03; p[6] = 0x07;
        return p;
    }

    /// <summary>DIRECT write of all three LEDs (selectors 0x01/0x02/0x03), R,G,B each, end byte 0xFF@16.</summary>
    public static byte[] BuildDirect(IReadOnlyList<(byte R, byte G, byte B)> c)
    {
        var p = new byte[Len];
        p[0] = ReportId; p[1] = 0xFF; p[2] = 0x12; p[3] = 0x10;
        for (int i = 0; i < 3; i++)
        {
            (byte R, byte G, byte B) col = i < c.Count ? c[i] : ((byte)0, (byte)0, (byte)0);
            int o = 4 + i * 4;
            p[o] = (byte)(i + 1); p[o + 1] = col.R; p[o + 2] = col.G; p[o + 3] = col.B;
        }
        p[16] = 0xFF;
        return p;
    }

    /// <summary>Apply/commit (0x12/0x70).</summary>
    public static byte[] BuildApply()
    {
        var p = new byte[Len];
        p[0] = ReportId; p[1] = 0xFF; p[2] = 0x12; p[3] = 0x70;
        return p;
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            if (!_enabled) { if (!_hid.SetOutputReport(BuildEnable())) return false; _enabled = true; }
            return _hid.SetOutputReport(BuildDirect(colors)) && _hid.SetOutputReport(BuildApply());
        }
    }

    public bool SetAll(byte r, byte g, byte b) => SetLeds(new[] { (r, g, b), (r, g, b), (r, g, b) });
}

internal sealed class LogitechG203LRgbController : IRgbController
{
    private readonly LogitechG203LController _dev;
    public string Id => "logi.g203";
    public string Label => "Logitech G203 Lightsync";
    public int LedCount => LogitechG203LController.LedCount;
    public RgbZoneKind Kind => RgbZoneKind.Mouse;
    public RgbTransport Transport => RgbTransport.UsbHid;
    public LogitechG203LRgbController(HidDevice hid) { _dev = new LogitechG203LController(hid); }
    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}

/*-- G810 / G910 / G Pro keyboards: PER-KEY via report 0x12 direct frames + commit (report 0x11).
     The key table (zone,idx) is generated per layout from the HID-usage scheme; LEDs stream 14 per
     frame, flushed on zone change, then one commit latches the update. --*/
internal sealed class LogitechGKeyboardController
{
    public const int CommandInterface = 1;
    public const ushort UsagePage = 0xFF43;
    public const ushort Usage = 0x0602;

    public const int LayoutG810 = 0, LayoutG910 = 1, LayoutGPro = 2;
    private const int FrameLen = 64, CommitLen = 20, MaxPerFrame = 14;

    /// <summary>A keyboard model. DirectB2 = the report-0x12 command byte (0x0C G810/GPro, 0x0F G910);
    /// CommitB2/B3 = the report-0x11 commit bytes.</summary>
    public readonly record struct Model(string Id, string Label, ushort Pid, int Layout, byte DirectB2, byte CommitB2, byte CommitB3);

    public static readonly Model[] KnownModels =
    {
        new("logi.g810", "Logitech G810",     0xC331, LayoutG810, 0x0C, 0x0C, 0x5D),
        new("logi.g810", "Logitech G810",     0xC337, LayoutG810, 0x0C, 0x0C, 0x5D),
        new("logi.g610", "Logitech G610",     0xC333, LayoutG810, 0x0C, 0x0C, 0x5D),
        new("logi.g610", "Logitech G610",     0xC338, LayoutG810, 0x0C, 0x0C, 0x5D),
        new("logi.g512", "Logitech G512",     0xC342, LayoutG810, 0x0C, 0x0C, 0x5D),
        new("logi.g512", "Logitech G512 RGB", 0xC33C, LayoutG810, 0x0C, 0x0C, 0x5D),
        new("logi.g910", "Logitech G910",     0xC32B, LayoutG910, 0x0F, 0x0F, 0x5F),
        new("logi.g910", "Logitech G910",     0xC335, LayoutG910, 0x0F, 0x0F, 0x5F),
        new("logi.gpro", "Logitech G Pro Keyboard", 0xC339, LayoutGPro, 0x0C, 0x0C, 0x5D),
    };

    /// <summary>Builds the per-key table as (zone&lt;&lt;8)|idx values, in OpenRGB order, for a layout.</summary>
    public static ushort[] BuildKeyTable(int layout)
    {
        var t = new List<ushort>(117);
        void Add(byte zone, byte idx) => t.Add((ushort)((zone << 8) | idx));
        void Range(byte zone, byte from, byte to) { for (int i = from; i <= to; i++) Add(zone, (byte)i); }

        // KEYBOARD zone (0x01): HID-usage block.
        if (layout == LayoutGPro)
        {
            Range(0x01, 0x04, 0x52);            // alphanumerics, no numpad (TKL)
            Add(0x01, 0x64); Add(0x01, 0x65);   // ISO backslash, Menu
            Range(0x01, 0xE0, 0xE7);            // modifiers (E7 = Right Function)
        }
        else
        {
            Range(0x01, 0x04, 0x65);            // full alphanumeric + numpad block
            Range(0x01, 0xE0, 0xE7);            // modifiers
        }

        switch (layout)
        {
            case LayoutG810:                    // MEDIA + LOGO + INDICATORS
                Add(0x02, 0xB5); Add(0x02, 0xB6); Add(0x02, 0xB7); Add(0x02, 0xCD); Add(0x02, 0xE2);
                Add(0x10, 0x01);
                Range(0x40, 0x01, 0x05);
                break;
            case LayoutG910:                    // GKEYS + LOGO(2)
                Range(0x04, 0x01, 0x09);
                Add(0x10, 0x01); Add(0x10, 0x02);
                break;
            default:                            // GPro: LOGO + INDICATORS(4)
                Add(0x10, 0x01);
                Range(0x40, 0x01, 0x04);
                break;
        }
        return t.ToArray();
    }

    private readonly HidDevice _hid;
    private readonly Model _m;
    private readonly ushort[] _keys;
    private readonly object _io = new();

    public LogitechGKeyboardController(HidDevice hid, Model m) { _hid = hid; _m = m; _keys = BuildKeyTable(m.Layout); }
    public int LedCount => _keys.Length;

    /// <summary>A report-0x12 direct frame for up to 14 {idx,R,G,B} entries of one zone.</summary>
    public static byte[] BuildDirectFrame(byte directB2, byte zone, IReadOnlyList<(byte Idx, byte R, byte G, byte B)> e)
    {
        var p = new byte[FrameLen];
        p[0] = 0x12; p[1] = 0xFF; p[2] = directB2; p[3] = 0x3D; p[4] = 0x00; p[5] = zone; p[6] = 0x00; p[7] = (byte)e.Count;
        for (int i = 0; i < e.Count; i++) { int o = 8 + i * 4; p[o] = e[i].Idx; p[o + 1] = e[i].R; p[o + 2] = e[i].G; p[o + 3] = e[i].B; }
        return p;
    }

    public static byte[] BuildCommit(byte commitB2, byte commitB3)
    { var p = new byte[CommitLen]; p[0] = 0x11; p[1] = 0xFF; p[2] = commitB2; p[3] = commitB3; return p; }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            var batch = new List<(byte, byte, byte, byte)>(MaxPerFrame);
            byte curZone = (byte)(_keys.Length > 0 ? _keys[0] >> 8 : 0);
            bool Flush() { bool ok = batch.Count == 0 || _hid.SetOutputReport(BuildDirectFrame(_m.DirectB2, curZone, batch)); batch.Clear(); return ok; }

            for (int i = 0; i < _keys.Length; i++)
            {
                byte zone = (byte)(_keys[i] >> 8), idx = (byte)(_keys[i] & 0xFF);
                if (zone != curZone || batch.Count == MaxPerFrame) { if (!Flush()) return false; curZone = zone; }
                (byte R, byte G, byte B) c = i < colors.Count ? colors[i] : ((byte)0, (byte)0, (byte)0);
                batch.Add((idx, c.R, c.G, c.B));
            }
            if (!Flush()) return false;
            return _hid.SetOutputReport(BuildCommit(_m.CommitB2, _m.CommitB3));
        }
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var c = new (byte, byte, byte)[_keys.Length];
        for (int i = 0; i < _keys.Length; i++) c[i] = (r, g, b);
        return SetLeds(c);
    }
}

internal sealed class LogitechGKeyboardRgbController : IRgbController
{
    private readonly LogitechGKeyboardController _dev;
    public string Id { get; }
    public string Label { get; }
    public int LedCount { get; }
    public RgbZoneKind Kind => RgbZoneKind.Keyboard;
    public RgbTransport Transport => RgbTransport.UsbHid;

    public LogitechGKeyboardRgbController(HidDevice hid, LogitechGKeyboardController.Model m)
    {
        _dev = new LogitechGKeyboardController(hid, m); Id = m.Id; Label = m.Label; LedCount = _dev.LedCount;
    }

    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}
