namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| HyperX USB-HID peripherals (VID 0x0951 Kingston / 0x03F0 HP), reproduced as  |
| protocol FACTS. Board-independent, user-mode, opt-in (AllowHidRgb), reduced  |
| assurance. EVERY HyperX device reverts to its stored effect unless a color   |
| frame is re-sent on an interval, so all use the broker KEEPALIVE loop (these |
| are volatile — no flash write — so refreshing is wear-free).                  |
|                                                                            |
| Excluded (see docs/RGB-DEVICE-COVERAGE.md): Alloy Elite (uncertain extended- |
| LED scatter tables) and Alloy Origins Core (runtime keyboard-layout query).  |
\*---------------------------------------------------------------------------*/

/// <summary>Base: caches the last frame and re-sends it on Refresh() (keepalive). Subclass implements Send.</summary>
internal abstract class HyperXDevice : IRgbController
{
    protected readonly HidDevice Hid;
    private readonly object _io = new();
    private IReadOnlyList<(byte R, byte G, byte B)>? _last;

    protected HyperXDevice(HidDevice hid) { Hid = hid; }

    public abstract string Id { get; }
    public abstract string Label { get; }
    public abstract int LedCount { get; }
    public abstract RgbZoneKind Kind { get; }
    public RgbTransport Transport => RgbTransport.UsbHid;
    public virtual int KeepaliveIntervalMs => 50;

    protected abstract bool Send(IReadOnlyList<(byte R, byte G, byte B)> colors);

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io) { _last = new List<(byte R, byte G, byte B)>(colors); return Send(colors); }
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var c = new (byte, byte, byte)[LedCount];
        for (int i = 0; i < LedCount; i++) c[i] = (r, g, b);
        return SetLeds(c);
    }

    public bool Refresh() { lock (_io) return _last is null || Send(_last); }

    protected static (byte R, byte G, byte B) At(IReadOnlyList<(byte R, byte G, byte B)> c, int i)
        => i < c.Count ? c[i] : ((byte)0, (byte)0, (byte)0);
}

/*============================ MICE ============================*/

/// <summary>Pulsefire FPS Pro / Core: 1 LED, feature report 0x07 (264 bytes).</summary>
internal sealed class HyperXPulsefireFpsProController : HyperXDevice
{
    public const ushort UsagePage = 0xFF01;
    public const int Interface = 1;
    public static readonly (ushort Vid, ushort Pid)[] Ids = { (0x0951, 0x16D7), (0x0951, 0x16DE), (0x03F0, 0x0D8F) };
    private const int Len = 264;

    public HyperXPulsefireFpsProController(HidDevice hid) : base(hid) { }
    public override string Id => "hyperx.pulsefire.fpspro";
    public override string Label => "HyperX Pulsefire FPS Pro/Core";
    public override int LedCount => 1;
    public override RgbZoneKind Kind => RgbZoneKind.Mouse;

    public static byte[] BuildColor(byte r, byte g, byte b)
    {
        var p = new byte[Len]; p[0] = 0x07; p[1] = 0x0A; p[2] = r; p[3] = g; p[4] = b; p[8] = 0xA0; return p;
    }
    protected override bool Send(IReadOnlyList<(byte R, byte G, byte B)> c)
    { var x = At(c, 0); return Hid.SetFeature(BuildColor(x.R, x.G, x.B)); }
}

/// <summary>Pulsefire Raid: 2 LEDs (scroll, logo), feature 0x07 (264). Keepalive ~1 s.</summary>
internal sealed class HyperXPulsefireRaidController : HyperXDevice
{
    public const ushort Vid = 0x0951, Pid = 0x16E4, UsagePage = 0xFF01, Usage = 0x0001;
    public const int Interface = 1;
    private const int Len = 264;

    public HyperXPulsefireRaidController(HidDevice hid) : base(hid) { }
    public override string Id => "hyperx.pulsefire.raid";
    public override string Label => "HyperX Pulsefire Raid";
    public override int LedCount => 2;
    public override RgbZoneKind Kind => RgbZoneKind.Mouse;
    public override int KeepaliveIntervalMs => 1000;

    public static byte[] BuildColor(IReadOnlyList<(byte R, byte G, byte B)> c)
    {
        var p = new byte[Len]; p[0] = 0x07; p[1] = 0x0A;
        for (int i = 0; i < 2; i++) { var x = At(c, i); int o = 2 + i * 3; p[o] = x.R; p[o + 1] = x.G; p[o + 2] = x.B; }
        p[8] = 0xA0; return p;
    }
    protected override bool Send(IReadOnlyList<(byte R, byte G, byte B)> c) => Hid.SetFeature(BuildColor(c));
}

/// <summary>Pulsefire Haste: 1 LED, feature 0x00 (65). Setup (0x04/0xF2) then color (0x81).</summary>
internal sealed class HyperXPulsefireHasteController : HyperXDevice
{
    public const ushort UsagePage = 0xFF90;
    public const int Interface = 3;
    public static readonly (ushort Vid, ushort Pid)[] Ids = { (0x0951, 0x1727), (0x03F0, 0x0F8F) };
    private const int Len = 65;

    public HyperXPulsefireHasteController(HidDevice hid) : base(hid) { }
    public override string Id => "hyperx.pulsefire.haste";
    public override string Label => "HyperX Pulsefire Haste";
    public override int LedCount => 1;
    public override RgbZoneKind Kind => RgbZoneKind.Mouse;

    public static byte[] BuildSetup() { var p = new byte[Len]; p[1] = 0x04; p[2] = 0xF2; p[8] = 0x02; return p; }
    public static byte[] BuildColor(byte r, byte g, byte b)
    { var p = new byte[Len]; p[1] = 0x81; p[2] = r; p[3] = g; p[4] = b; p[8] = 0x02; return p; }

    protected override bool Send(IReadOnlyList<(byte R, byte G, byte B)> c)
    { var x = At(c, 0); return Hid.SetFeature(BuildSetup()) && Hid.SetFeature(BuildColor(x.R, x.G, x.B)); }
}

/// <summary>Pulsefire Dart: 2 LEDs (logo 0x00 / scroll 0x10), output 0x00 (65), per-LED. Holds (no keepalive).</summary>
internal sealed class HyperXPulsefireDartController : HyperXDevice
{
    // Wired: iface 1, usage FF13. Wireless: iface 2, usage FF00.
    public static readonly (ushort Vid, ushort Pid, int Iface, ushort UsagePage)[] Ids =
        { (0x0951, 0x16E2, 1, 0xFF13), (0x03F0, 0x088E, 1, 0xFF13), (0x0951, 0x16E1, 2, 0xFF00), (0x03F0, 0x068E, 2, 0xFF00) };
    private static readonly byte[] LedSel = { 0x00, 0x10 };   // logo, scroll
    private const int Len = 65;

    public HyperXPulsefireDartController(HidDevice hid) : base(hid) { }
    public override string Id => "hyperx.pulsefire.dart";
    public override string Label => "HyperX Pulsefire Dart";
    public override int LedCount => 2;
    public override RgbZoneKind Kind => RgbZoneKind.Mouse;
    public override int KeepaliveIntervalMs => 0;   // Dart holds its color (onboard)

    public static byte[] BuildLed(byte led, byte r, byte g, byte b)
    {
        var p = new byte[Len];
        p[1] = 0xD2; p[2] = led; p[3] = 0x00 /*static*/; p[4] = 0x08;
        p[5] = r; p[6] = g; p[7] = b; p[8] = r; p[9] = g; p[10] = b; p[11] = 0x64 /*brightness*/; p[12] = 0x00 /*speed*/;
        return p;
    }
    protected override bool Send(IReadOnlyList<(byte R, byte G, byte B)> c)
    {
        for (int i = 0; i < 2; i++) { var x = At(c, i); if (!Hid.SetOutputReport(BuildLed(LedSel[i], x.R, x.G, x.B))) return false; }
        return true;
    }
}

/// <summary>Pulsefire Surge: 33 LEDs (32 strip + logo), feature 0x07 (264), planar.</summary>
internal sealed class HyperXPulsefireSurgeController : HyperXDevice
{
    public const ushort UsagePage = 0xFF01;
    public const int Interface = 1;
    public static readonly (ushort Vid, ushort Pid)[] Ids = { (0x0951, 0x16D3), (0x03F0, 0x0490) };
    private const int Len = 264, Strip = 32;

    public HyperXPulsefireSurgeController(HidDevice hid) : base(hid) { }
    public override string Id => "hyperx.pulsefire.surge";
    public override string Label => "HyperX Pulsefire Surge";
    public override int LedCount => 33;
    public override RgbZoneKind Kind => RgbZoneKind.Mouse;

    public static byte[] BuildColor(IReadOnlyList<(byte R, byte G, byte B)> c)
    {
        var p = new byte[Len]; p[0] = 0x07; p[1] = 0x14; p[3] = 0xA0;
        for (int i = 0; i < Strip; i++) { var x = At(c, i); p[0x08 + i] = x.R; p[0x28 + i] = x.G; p[0x48 + i] = x.B; }
        var logo = At(c, 32); p[0x6C] = logo.R; p[0x6D] = logo.G; p[0x6E] = logo.B;
        return p;
    }
    protected override bool Send(IReadOnlyList<(byte R, byte G, byte B)> c) => Hid.SetFeature(BuildColor(c));
}

/*============================ KEYBOARDS ============================*/

/// <summary>
/// Alloy FPS RGB: 106 LEDs, feature 0x07 (264). Three channel packets (R/G/B), each scattering the
/// channel value into the 264-byte buffer at the fixed key-offset table.
/// </summary>
internal sealed class HyperXAlloyFpsController : HyperXDevice
{
    public const ushort Vid = 0x0951, Pid = 0x16DC, UsagePage = 0xFF01;
    public const int Interface = 2;
    private const int Len = 264, Keys = 106;

    private static readonly int[] KeyOffset =
    {
        0x08,0x09,0x0A,0x0B,0x0C,0x0D,0x0E,0x0F,0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18,0x19,0x1A,0x1B,
        0x1C,0x1D,0x1E,0x20,0x21,0x22,0x23,0x24,0x26,0x27,0x28,0x29,0x2A,0x2B,0x2C,0x2D,0x2E,0x2F,0x30,0x31,
        0x32,0x33,0x34,0x37,0x38,0x39,0x3A,0x3B,0x3C,0x3E,0x3F,0x41,0x44,0x45,0x48,0x49,0x4A,0x4B,0x4C,0x4D,
        0x4E,0x4F,0x51,0x54,0x55,0x58,0x59,0x5A,0x5B,0x5C,0x5E,0x5F,0x61,0x64,0x65,0x68,0x69,0x6A,0x6B,0x6C,
        0x6E,0x6F,0x74,0x75,0x78,0x79,0x7A,0x7B,0x7C,0x7D,0x7E,0x7F,0x81,0x84,0x85,0x88,0x89,0x8A,0x8B,0x8C,
        0x8D,0x8E,0x8F,0x91,0x94,0x95,
    };

    public HyperXAlloyFpsController(HidDevice hid) : base(hid) { }
    public override string Id => "hyperx.alloy.fps";
    public override string Label => "HyperX Alloy FPS RGB";
    public override int LedCount => Keys;
    public override RgbZoneKind Kind => RgbZoneKind.Keyboard;

    /// <summary>One channel packet: [0x07, 0x16, channel(1=R/2=G/3=B), 0xA0, then value scattered by KeyOffset].</summary>
    public static byte[] BuildChannel(byte channel, IReadOnlyList<(byte R, byte G, byte B)> c)
    {
        var p = new byte[Len]; p[0] = 0x07; p[1] = 0x16; p[2] = channel; p[3] = 0xA0;
        for (int i = 0; i < Keys; i++)
        {
            var x = At(c, i);
            p[KeyOffset[i]] = channel == 0x01 ? x.R : channel == 0x02 ? x.G : x.B;
        }
        return p;
    }
    protected override bool Send(IReadOnlyList<(byte R, byte G, byte B)> c)
        => Hid.SetFeature(BuildChannel(0x01, c)) && Hid.SetFeature(BuildChannel(0x02, c)) && Hid.SetFeature(BuildChannel(0x03, c));
}

/// <summary>
/// Alloy Origins / Origins 60 / Origins 65 / Elite2: feature 0x00 (65), 4-byte color groups
/// ([0x81,R,G,B]) streamed in a fixed packet count, after inserting blanks at the skip indices.
/// </summary>
internal sealed class HyperXOriginsController : HyperXDevice
{
    public readonly record struct Model(string Id, string Label, ushort Vid, ushort Pid, int Interface,
                                        ushort UsagePage, int Leds, int Packets, byte Init9, int[] Skip, int Keepalive);

    public static readonly Model[] KnownModels =
    {
        new("hyperx.alloy.origins",    "HyperX Alloy Origins",     0x0951, 0x16E5, 3, 0,      107, 9, 0x09,
            new[] {23,29,41,47,59,70,71,87,88,93,99,100,102,108,113,114,120,123,124}, 50),
        new("hyperx.alloy.origins",    "HyperX Alloy Origins",     0x03F0, 0x0591, 3, 0,      107, 9, 0x09,
            new[] {23,29,41,47,59,70,71,87,88,93,99,100,102,108,113,114,120,123,124}, 50),
        new("hyperx.alloy.origins60",  "HyperX Alloy Origins 60",  0x0951, 0x1734, 3, 0,      71,  5, 0x05, new int[0], 50),
        new("hyperx.alloy.origins60",  "HyperX Alloy Origins 60",  0x03F0, 0x0C8E, 3, 0,      71,  5, 0x05, new int[0], 50),
        new("hyperx.alloy.origins65",  "HyperX Alloy Origins 65",  0x03F0, 0x038F, 3, 0,      77,  5, 0x05, new int[0], 50),
        new("hyperx.alloy.elite2",     "HyperX Alloy Elite 2",     0x0951, 0x1711, 3, 0xFF90, 128, 9, 0x00,
            new[] {23,29,41,47,70,71,76,77,87,88,93,99,100,102,108,113}, 1000),
        new("hyperx.alloy.elite2",     "HyperX Alloy Elite 2",     0x03F0, 0x058F, 3, 0xFF90, 128, 9, 0x00,
            new[] {23,29,41,47,70,71,76,77,87,88,93,99,100,102,108,113}, 1000),
    };

    private const int Len = 65;
    private readonly Model _m;
    public HyperXOriginsController(HidDevice hid, Model m) : base(hid) { _m = m; }

    public override string Id => _m.Id;
    public override string Label => _m.Label;
    public override int LedCount => _m.Leds;
    public override RgbZoneKind Kind => RgbZoneKind.Keyboard;
    public override int KeepaliveIntervalMs => _m.Keepalive;

    public static byte[] BuildInit(byte init9)
    { var p = new byte[Len]; p[1] = 0x04; p[2] = 0xF2; if (init9 != 0) p[9] = init9; return p; }

    /// <summary>Builds the full packet list: blanks inserted at skip indices, 16 groups/packet, padded to packetCount.</summary>
    public static List<byte[]> BuildColorPackets(IReadOnlyList<(byte R, byte G, byte B)> c, int[] skip, int packets)
    {
        var seq = new List<(byte R, byte G, byte B)>(c);
        foreach (int s in skip) if (s <= seq.Count) seq.Insert(s, ((byte)0, (byte)0, (byte)0));

        var list = new List<byte[]>(packets);
        for (int p = 0; p < packets; p++)
        {
            var buf = new byte[Len];
            for (int g = 0; g < 16; g++)
            {
                int li = p * 16 + g;
                var x = li < seq.Count ? seq[li] : ((byte)0, (byte)0, (byte)0);
                int o = 1 + g * 4; buf[o] = 0x81; buf[o + 1] = x.Item1; buf[o + 2] = x.Item2; buf[o + 3] = x.Item3;
            }
            list.Add(buf);
        }
        return list;
    }

    protected override bool Send(IReadOnlyList<(byte R, byte G, byte B)> c)
    {
        if (!Hid.SetFeature(BuildInit(_m.Init9))) return false;
        foreach (byte[] pkt in BuildColorPackets(c, _m.Skip, _m.Packets))
            if (!Hid.SetFeature(pkt)) return false;
        return true;
    }
}

/// <summary>
/// Eve 1800 / Origins 2 65: output reports with report id 0x44. Init (0x44/0x01/0x04) then color
/// packets (0x44/0x02, seq) with contiguous R,G,B triplets from offset 4.
/// </summary>
internal sealed class HyperX44KeyboardController : HyperXDevice
{
    public readonly record struct Model(string Id, string Label, ushort Vid, ushort Pid, int Interface,
                                        int Leds, int PerPacket, int Packets);

    public static readonly Model[] KnownModels =
    {
        new("hyperx.eve1800",     "HyperX Eve 1800",      0x03F0, 0x08C2, 2, 10, 10, 1),
        new("hyperx.origins2_65", "HyperX Origins 2 65",  0x03F0, 0x0CC2, 3, 74, 20, 4),
    };

    private const int Len = 65;
    private readonly Model _m;
    public HyperX44KeyboardController(HidDevice hid, Model m) : base(hid) { _m = m; }

    public override string Id => _m.Id;
    public override string Label => _m.Label;
    public override int LedCount => _m.Leds;
    public override RgbZoneKind Kind => RgbZoneKind.Keyboard;

    public static byte[] BuildInit() { var p = new byte[Len]; p[0] = 0x44; p[1] = 0x01; p[2] = 0x04; return p; }

    public static byte[] BuildColorPacket(byte seq, IReadOnlyList<(byte R, byte G, byte B)> c, int start, int count)
    {
        var p = new byte[Len]; p[0] = 0x44; p[1] = 0x02; p[2] = seq;
        for (int i = 0; i < count; i++) { var x = At(c, start + i); int o = 4 + i * 3; p[o] = x.R; p[o + 1] = x.G; p[o + 2] = x.B; }
        return p;
    }

    protected override bool Send(IReadOnlyList<(byte R, byte G, byte B)> c)
    {
        if (!Hid.SetOutputReport(BuildInit())) return false;
        for (int pkt = 0; pkt < _m.Packets; pkt++)
        {
            int start = pkt * _m.PerPacket;
            int count = Math.Min(_m.PerPacket, Math.Max(0, _m.Leds - start));
            if (!Hid.SetOutputReport(BuildColorPacket((byte)pkt, c, start, count))) return false;
        }
        return true;
    }
}
