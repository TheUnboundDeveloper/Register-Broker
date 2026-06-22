namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| NzxtLiftController                                                          |
|                                                                            |
|   NZXT Lift mouse over USB-HID (VID 0x1E71, PID 0x2100, vendor collection    |
|   interface 0, usage 0xFFCA:0001), reproduced as protocol FACTS. 6 LEDs in   |
|   2 zones of 3. OUTPUT report, 65-byte wire ([0]=0x00 report id + 64 bytes). |
|   Color order R,G,B; the LED slot order is REMAPPED (the first zone is       |
|   reversed): packet slot order is colors 2,1,0,3,4,5, each occupying 4 wire  |
|   bytes (3 color + 1 pad) from offset 26. No CRC, no enable packet.          |
|   Board-independent, user-mode, opt-in (AllowHidRgb), reduced assurance.     |
|                                                                            |
|   NOTE: the NZXT Hue 2 channel controllers are NOT here — their LED count is  |
|   discovered at runtime (device-info read), not fixed (see coverage doc).    |
\*---------------------------------------------------------------------------*/
internal sealed class NzxtLiftController
{
    public const ushort VendorId = 0x1E71;
    public const ushort ProductId = 0x2100;
    public const int Interface = 0;
    public const ushort UsagePage = 0xFFCA;
    public const ushort Usage = 0x0001;
    public const int LedCount = 6;

    private const int Len = 65;
    // The packet slot order (the first zone of 3 is reversed): logical colors 2,1,0,3,4,5.
    private static readonly int[] SlotOrder = { 2, 1, 0, 3, 4, 5 };

    private readonly HidDevice _hid;
    private readonly object _io = new();
    public NzxtLiftController(HidDevice hid) { _hid = hid; }

    /// <summary>
    /// Wire buffer ([0]=0x00 report id, then the 64-byte payload). Fixed header bytes + 6 LEDs at
    /// stride 4 from offset 26, in the remapped slot order, R,G,B each. Pure/testable.
    /// </summary>
    public static byte[] BuildColor(IReadOnlyList<(byte R, byte G, byte B)> c)
    {
        var p = new byte[Len];
        // payload index N -> wire index N+1 (the 0x00 report-id prefix).
        p[1] = 0x43; p[2] = 0xAE; p[3] = 0x00; p[4] = 0x10; p[5] = 0x02; p[6] = 0x3F;
        p[25] = 0x06;                              // payload[24] count/marker
        for (int slot = 0; slot < LedCount; slot++)
        {
            int li = SlotOrder[slot];
            (byte R, byte G, byte B) col = li < c.Count ? c[li] : ((byte)0, (byte)0, (byte)0);
            int o = 26 + slot * 4;                 // payload 25 + slot*4, +1 for prefix
            p[o] = col.R; p[o + 1] = col.G; p[o + 2] = col.B;
        }
        return p;
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io) return _hid.SetOutputReport(BuildColor(colors));
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var c = new (byte, byte, byte)[LedCount];
        for (int i = 0; i < LedCount; i++) c[i] = (r, g, b);
        return SetLeds(c);
    }
}

internal sealed class NzxtLiftRgbController : IRgbController
{
    private readonly NzxtLiftController _dev;
    public string Id => "nzxt.lift";
    public string Label => "NZXT Lift";
    public int LedCount => NzxtLiftController.LedCount;
    public RgbZoneKind Kind => RgbZoneKind.Mouse;
    public RgbTransport Transport => RgbTransport.UsbHid;
    public NzxtLiftRgbController(HidDevice hid) { _dev = new NzxtLiftController(hid); }
    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}
