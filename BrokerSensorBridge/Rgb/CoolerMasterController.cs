namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| CoolerMasterMp750Controller                                                 |
|                                                                            |
|   Cooler Master MP750 RGB mousepad over USB-HID (VID 0x2516, usage page      |
|   0xFF00 usage 1), reproduced as protocol FACTS. Single LED. OUTPUT report,  |
|   65-byte wire ([0]=0x00 report id + payload). A static color is one packet: |
|   [1]=0x01 (static mode), [2]=0x04, [3..5]=R,G,B, [6]=speed. No CRC, no       |
|   apply, no enable. Board-independent, user-mode, opt-in, reduced assurance. |
|                                                                            |
|   NOTE: the other Cooler Master devices (mice with init/apply sequences,     |
|   addressable strips with runtime-set LED counts, keyboards with per-model   |
|   key tables) are NOT here — see docs/RGB-DEVICE-COVERAGE.md.                |
\*---------------------------------------------------------------------------*/
internal sealed class CoolerMasterMp750Controller
{
    public const ushort VendorId = 0x2516;
    public const ushort UsagePage = 0xFF00;
    public const ushort Usage = 0x0001;
    public const int LedCount = 1;

    public static readonly ushort[] ProductIds = { 0x0105, 0x0107, 0x0109 };   // Medium, Large, XL

    private const int Len = 65;
    private readonly HidDevice _hid;
    private readonly object _io = new();
    public CoolerMasterMp750Controller(HidDevice hid) { _hid = hid; }

    /// <summary>Static-color wire buffer: [00, 01(static), 04, R, G, B, speed].</summary>
    public static byte[] BuildStatic(byte r, byte g, byte b)
    {
        var p = new byte[Len];
        p[1] = 0x01; p[2] = 0x04; p[3] = r; p[4] = g; p[5] = b; p[6] = 0x00;
        return p;
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        lock (_io) return _hid.SetOutputReport(BuildStatic(r, g, b));
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        (byte R, byte G, byte B) c = colors.Count > 0 ? colors[0] : ((byte)0, (byte)0, (byte)0);
        return SetAll(c.R, c.G, c.B);
    }
}

internal sealed class CoolerMasterMp750RgbController : IRgbController
{
    private readonly CoolerMasterMp750Controller _dev;
    public string Id => "cm.mp750";
    public string Label => "Cooler Master MP750";
    public int LedCount => CoolerMasterMp750Controller.LedCount;
    public RgbZoneKind Kind => RgbZoneKind.Mousepad;
    public RgbTransport Transport => RgbTransport.UsbHid;
    public CoolerMasterMp750RgbController(HidDevice hid) { _dev = new CoolerMasterMp750Controller(hid); }
    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}

/*---------------------------------------------------------------------------*\
| CoolerMasterMouseController                                                 |
|                                                                            |
|   Cooler Master MM-series mice over USB-HID (VID 0x2516, iface 1, usage      |
|   FF00:0001), reproduced as protocol FACTS. OUTPUT report, 65-byte wire      |
|   ([0]=0x00 + payload). A one-time init ([1]=0x41,[2]=0x80), then a DIRECT    |
|   color packet seeded [1]=0x51,[2]=0xA8 with each zone's R,G,B at its own     |
|   byte offset. Zone byte-offsets differ between the MM5xx (3 LED) and MM7xx/  |
|   711 (2 LED) families — the load-bearing fact. Fixed LED counts.            |
|   (MM531/MM712 are excluded: MM531's map is unconfirmed upstream and MM712    |
|   uses a separate NORMAL/DIRECT state machine — see the coverage doc.)        |
\*---------------------------------------------------------------------------*/
internal sealed class CoolerMasterMouseController
{
    /// <summary>A model: its PID and the wire byte-offset of R for each zone (G=+1, B=+2).</summary>
    public readonly record struct Model(string Id, string Label, ushort Pid, int[] ZoneOffsets);

    public static readonly Model[] KnownModels =
    {
        new("cm.mm530", "Cooler Master MM530", 0x0065, new[] { 11, 5, 8 }),   // wheel, buttons, logo
        new("cm.mm711", "Cooler Master MM711", 0x0101, new[] { 5, 8 }),       // wheel, logo
        new("cm.mm720", "Cooler Master MM720", 0x0141, new[] { 5, 8 }),       // wheel, logo
        new("cm.mm730", "Cooler Master MM730", 0x0165, new[] { 5, 8 }),       // wheel, logo
    };

    public const ushort VendorId = 0x2516, UsagePage = 0xFF00, Usage = 0x0001;
    public const int Interface = 1;
    private const int Len = 65;

    private readonly HidDevice _hid;
    private readonly Model _m;
    private readonly object _io = new();
    private bool _inited;
    public CoolerMasterMouseController(HidDevice hid, Model m) { _hid = hid; _m = m; }
    public int LedCount => _m.ZoneOffsets.Length;

    public static byte[] BuildInit() { var p = new byte[Len]; p[1] = 0x41; p[2] = 0x80; return p; }

    /// <summary>DIRECT color packet: seed [51,A8], each zone's R,G,B at its byte offset.</summary>
    public static byte[] BuildDirect(IReadOnlyList<(byte R, byte G, byte B)> c, int[] offsets)
    {
        var p = new byte[Len]; p[1] = 0x51; p[2] = 0xA8;
        for (int z = 0; z < offsets.Length; z++)
        {
            (byte R, byte G, byte B) col = z < c.Count ? c[z] : ((byte)0, (byte)0, (byte)0);
            int o = offsets[z]; p[o] = col.R; p[o + 1] = col.G; p[o + 2] = col.B;
        }
        return p;
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            if (!_inited) { if (!_hid.SetOutputReport(BuildInit())) return false; _inited = true; }
            return _hid.SetOutputReport(BuildDirect(colors, _m.ZoneOffsets));
        }
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var c = new (byte, byte, byte)[LedCount];
        for (int i = 0; i < LedCount; i++) c[i] = (r, g, b);
        return SetLeds(c);
    }
}

internal sealed class CoolerMasterMouseRgbController : IRgbController
{
    private readonly CoolerMasterMouseController _dev;
    public string Id { get; }
    public string Label { get; }
    public int LedCount { get; }
    public RgbZoneKind Kind => RgbZoneKind.Mouse;
    public RgbTransport Transport => RgbTransport.UsbHid;

    public CoolerMasterMouseRgbController(HidDevice hid, CoolerMasterMouseController.Model m)
    {
        _dev = new CoolerMasterMouseController(hid, m); Id = m.Id; Label = m.Label; LedCount = m.ZoneOffsets.Length;
    }

    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}
