namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| WraithPrismController                                                       |
|                                                                            |
|   AMD Wraith Prism CPU cooler over USB-HID (VID 0x2516, PID 0x0051, the      |
|   vendor collection on interface 1), reproduced as protocol FACTS. Board-    |
|   independent (matched by USB id, not the DMI profile); user-mode, opt-in    |
|   (AllowHidRgb), reduced assurance (no kernel brick-guard — HID is user      |
|   space). 17 LEDs: Logo (id 0x00), Fan (id 0x01), Ring (15, fixed id order). |
|                                                                            |
|   Transport: 65-byte OUTPUT reports ([0]=0x00 report id + 64 payload). DIRECT |
|   per-LED mode is latched once (enable 0x41/0x80/0x03 + apply 0x51/0x28),     |
|   then each frame is one or two 0xC0 packets of {led_id, R, G, B} entries     |
|   (max 15/packet). Byte order R,G,B; no CRC. Ported as facts from OpenRGB     |
|   (GPL-2.0); re-expressed here. HW-UNVALIDATED on this dev box.              |
\*---------------------------------------------------------------------------*/
internal sealed class WraithPrismController
{
    public const ushort UsbVendorId  = 0x2516;
    public const ushort UsbProductId = 0x0051;
    public const int    CommandInterface = 1;
    public const ushort VendorUsagePage  = 0xFF00;

    private const int ReportLen = 65;               // 1 report-id byte + 64 payload
    private const int MaxEntriesPerPacket = 15;     // 15*4 + 5 header bytes = 65

    /// <summary>LED id bytes in OpenRGB display order: Logo, Fan, then the 15 ring LEDs.</summary>
    public static readonly byte[] LedIds =
    {
        0x00,                                                       // Logo
        0x01,                                                       // Fan
        0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02,                   // Ring 0..6
        0x10, 0x0F, 0x0E, 0x0D, 0x0C, 0x0B, 0x0A, 0x09,            // Ring 7..14
    };

    public static int LedCount => LedIds.Length;    // 17

    private readonly HidDevice _hid;
    private readonly object _io = new();
    private bool _directLatched;

    public WraithPrismController(HidDevice hid) { _hid = hid; }

    /// <summary>Direct-mode enable packet (0x41 0x80, mode byte 0x03 = direct).</summary>
    public static byte[] BuildEnableDirect()
    {
        var p = new byte[ReportLen];
        p[1] = 0x41; p[2] = 0x80; p[3] = 0x03;
        return p;
    }

    /// <summary>Apply packet (0x51 0x28; byte index 5 = 0xE0).</summary>
    public static byte[] BuildApply()
    {
        var p = new byte[ReportLen];
        p[1] = 0x51; p[2] = 0x28; p[5] = 0xE0;
        return p;
    }

    /// <summary>
    /// Builds one 0xC0 direct packet for up to 15 (id, R, G, B) entries. Pure/testable: header
    /// [0x00, 0xC0, 0x01, size, 0x00], then 4 bytes per entry at offset 5 + i*4 (id, R, G, B).
    /// </summary>
    public static byte[] BuildDirectPacket(IReadOnlyList<(byte Id, byte R, byte G, byte B)> entries)
    {
        int size = Math.Min(entries.Count, MaxEntriesPerPacket);
        var p = new byte[ReportLen];
        p[1] = 0xC0; p[2] = 0x01; p[3] = (byte)size; p[4] = 0x00;
        for (int i = 0; i < size; i++)
        {
            int o = 5 + i * 4;
            p[o] = entries[i].Id; p[o + 1] = entries[i].R; p[o + 2] = entries[i].G; p[o + 3] = entries[i].B;
        }
        return p;
    }

    private bool EnsureDirect()
    {
        if (_directLatched) return true;
        if (!_hid.SetOutputReport(BuildEnableDirect())) return false;
        if (!_hid.SetOutputReport(BuildApply())) return false;
        _directLatched = true;
        return true;
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            if (!EnsureDirect()) { _directLatched = false; return false; }

            // Packet 1: Logo + Fan (LedIds[0..1]). Packet 2: the 15 ring LEDs (LedIds[2..16]).
            var head = new List<(byte, byte, byte, byte)>(2);
            var ring = new List<(byte, byte, byte, byte)>(MaxEntriesPerPacket);
            for (int i = 0; i < LedIds.Length; i++)
            {
                (byte R, byte G, byte B) c = i < colors.Count ? colors[i] : ((byte)0, (byte)0, (byte)0);
                (i < 2 ? head : ring).Add((LedIds[i], c.R, c.G, c.B));
            }
            if (!_hid.SetOutputReport(BuildDirectPacket(head))) { _directLatched = false; return false; }
            if (!_hid.SetOutputReport(BuildDirectPacket(ring))) { _directLatched = false; return false; }
            return true;
        }
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var colors = new (byte, byte, byte)[LedCount];
        for (int i = 0; i < LedCount; i++) colors[i] = (r, g, b);
        return SetLeds(colors);
    }
}

internal sealed class WraithPrismRgbController : IRgbController
{
    private readonly WraithPrismController _dev;
    public string Id => "amd.wraithprism";
    public string Label => "AMD Wraith Prism";
    public int LedCount => WraithPrismController.LedCount;
    public RgbZoneKind Kind => RgbZoneKind.Cooler;
    public RgbTransport Transport => RgbTransport.UsbHid;

    public WraithPrismRgbController(HidDevice hid) { _dev = new WraithPrismController(hid); }

    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors) => _dev.SetLeds(colors);
}
