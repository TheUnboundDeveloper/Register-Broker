namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| CorsairV2Controller                                                         |
|                                                                            |
|   Corsair iCUE V2 USB-HID peripherals (VID 0x1B1C, vendor collection on      |
|   interface 1, usage 0xFF42), reproduced as protocol FACTS. Board-           |
|   independent, user-mode, opt-in (AllowHidRgb), reduced assurance.           |
|                                                                            |
|   Only the FIXED-LED-count devices are driven here — the 11 mice, 2          |
|   mousepads and the K55 keyboard, whose LED count is layout-independent and  |
|   safe to hard-code. The matrix keyboards (K60/K70/K95/K100) need a runtime  |
|   key-layout query (ANSI/ISO/...) and are deliberately excluded (see         |
|   docs/RGB-DEVICE-COVERAGE.md).                                              |
|                                                                            |
|   Protocol: 65-byte OUTPUT reports (report id 0x00). byte[1] = write_cmd     |
|   (0x08 wired). Init = render-mode SW (0x01/0x03/.../0x02) + lighting-control |
|   (GET 0x5F). A DIRECT color frame is bracketed by Start/Stop-Transaction    |
|   and streamed as BLK_W1 (0x06, data at offset 8, 16-bit length at [4..5]) + |
|   BLK_WN (0x07, offset 4). Two color encodings: CTRL2 triplets (default,     |
|   2-byte 0x12/0x00 header) or CTRL1 planar (all R, all G, all B) — chosen by  |
|   an init probe (best-effort input-report read; defaults to CTRL2). Byte     |
|   order R,G,B; no CRC. HW-UNVALIDATED.                                       |
\*---------------------------------------------------------------------------*/
internal sealed class CorsairV2Controller
{
    public const ushort VendorId = 0x1B1C;
    public const int CommandInterface = 1;
    public const ushort Usage = 0xFF42;

    private const int Pkt = 65;                 // default packet size (fixed-count devices fit in one)
    private const byte WriteWired = 0x08;
    private const byte CtrlTriplet = 0x22;      // CTRL2 (default)
    private const byte CtrlPlanar  = 0x01;      // CTRL1

    // Command opcodes (byte[2]).
    private const byte CMD_SET = 0x01, CMD_GET = 0x02, CMD_STOP_TX = 0x05, CMD_BLK_W1 = 0x06, CMD_BLK_WN = 0x07, CMD_START_TX = 0x0D;

    public readonly record struct Model(string Id, string Label, ushort Pid, int Leds, RgbZoneKind Kind, int KeepaliveMs = 0);

    public static readonly Model[] KnownModels =
    {
        // Mice (fixed LED counts).
        new("corsair.darkcorese",   "Corsair Dark Core RGB SE",        0x1B4B, 12, RgbZoneKind.Mouse),
        new("corsair.darkcoreprose","Corsair Dark Core RGB Pro SE",    0x1B7E, 12, RgbZoneKind.Mouse),
        new("corsair.harpoonwl",    "Corsair Harpoon Wireless",        0x1B5E,  2, RgbZoneKind.Mouse),
        new("corsair.ironclawwl",   "Corsair Ironclaw Wireless",       0x1B4C,  6, RgbZoneKind.Mouse),
        new("corsair.katarpro",     "Corsair Katar Pro",               0x1B93,  2, RgbZoneKind.Mouse),
        new("corsair.katarprov2",   "Corsair Katar Pro V2",            0x1BBA,  2, RgbZoneKind.Mouse),
        new("corsair.katarproxt",   "Corsair Katar Pro XT",            0x1BAC,  2, RgbZoneKind.Mouse),
        new("corsair.m55pro",       "Corsair M55 RGB Pro",             0x1B70,  2, RgbZoneKind.Mouse),
        new("corsair.m65ultra",     "Corsair M65 RGB Ultra",           0x1B9E,  3, RgbZoneKind.Mouse),
        new("corsair.m65ultrawl",   "Corsair M65 RGB Ultra Wireless",  0x1BB5,  2, RgbZoneKind.Mouse),
        new("corsair.m75",          "Corsair M75",                     0x1BF0,  2, RgbZoneKind.Mouse),
        // Mousepads.
        new("corsair.mm700",        "Corsair MM700",                   0x1B9B,  3, RgbZoneKind.Mousepad),
        new("corsair.mm700_3xl",    "Corsair MM700 3XL",               0x1BC9,  3, RgbZoneKind.Mousepad),
        // The one fixed-count keyboard (LINEAR 6) — reverts to onboard rainbow without a ~60 s keepalive.
        new("corsair.k55pro",       "Corsair K55 RGB Pro",             0x1BA4,  6, RgbZoneKind.Keyboard, KeepaliveMs: 30000),
    };

    private readonly HidDevice _hid;
    private readonly int _leds;
    private readonly object _io = new();
    private byte _writeCmd = WriteWired;
    private byte _lightCtrl = CtrlTriplet;
    private bool _inited;
    private IReadOnlyList<(byte R, byte G, byte B)>? _last;   // for keepalive refresh

    public CorsairV2Controller(HidDevice hid, int leds) { _hid = hid; _leds = leds; }

    /// <summary>CTRL2 triplet color buffer: [0x12, 0x00, R0,G0,B0, R1,G1,B1, ...]. Length = count*3 + 2.</summary>
    public static byte[] BuildBufferTriplet(IReadOnlyList<(byte R, byte G, byte B)> colors, int count)
    {
        var buf = new byte[count * 3 + 2];
        buf[0] = 0x12; buf[1] = 0x00;
        for (int i = 0; i < count; i++)
        {
            (byte R, byte G, byte B) c = i < colors.Count ? colors[i] : ((byte)0, (byte)0, (byte)0);
            int o = 2 + i * 3; buf[o] = c.R; buf[o + 1] = c.G; buf[o + 2] = c.B;
        }
        return buf;
    }

    /// <summary>CTRL1 planar color buffer: [R0..Rn, G0..Gn, B0..Bn]. Length = count*3.</summary>
    public static byte[] BuildBufferPlanar(IReadOnlyList<(byte R, byte G, byte B)> colors, int count)
    {
        var buf = new byte[count * 3];
        for (int i = 0; i < count; i++)
        {
            (byte R, byte G, byte B) c = i < colors.Count ? colors[i] : ((byte)0, (byte)0, (byte)0);
            buf[i] = c.R; buf[count + i] = c.G; buf[2 * count + i] = c.B;
        }
        return buf;
    }

    /// <summary>
    /// Builds the FIRST stream packet (BLK_W1 0x06): write_cmd at [1], total length at [4..5], data at
    /// offset 8. Returns the number of data bytes consumed (min(dataLen, Pkt-8)). Pure/testable.
    /// </summary>
    public static byte[] BuildBlockFirst(byte writeCmd, ReadOnlySpan<byte> data, out int consumed)
    {
        var p = new byte[Pkt];
        p[0] = 0x00; p[1] = writeCmd; p[2] = CMD_BLK_W1; p[3] = 0x00;
        p[4] = (byte)(data.Length & 0xFF); p[5] = (byte)(data.Length >> 8);
        consumed = Math.Min(data.Length, Pkt - 8);
        data.Slice(0, consumed).CopyTo(p.AsSpan(8));
        return p;
    }

    private byte[] BuildBlockNext(ReadOnlySpan<byte> data, int offset, out int consumed)
    {
        var p = new byte[Pkt];
        p[0] = 0x00; p[1] = _writeCmd; p[2] = CMD_BLK_WN;
        consumed = Math.Min(data.Length - offset, Pkt - 4);
        data.Slice(offset, consumed).CopyTo(p.AsSpan(4));
        return p;
    }

    private byte[] Cmd(byte cmd, byte b3 = 0, byte b4 = 0, byte b5 = 0)
    {
        var p = new byte[Pkt];
        p[0] = 0x00; p[1] = _writeCmd; p[2] = cmd; p[3] = b3; p[4] = b4; p[5] = b5;
        return p;
    }

    private void EnsureInit()
    {
        if (_inited) return;
        // Render mode = software (required for direct streaming): SET 0x03 value 0x02.
        _hid.SetOutputReport(Cmd(CMD_SET, b3: 0x03, b4: 0x00, b5: 0x02));
        // Lighting-control enable handshake: GET 0x5F.
        _hid.SetOutputReport(Cmd(CMD_GET, b3: 0x5F));
        // Best-effort encoding probe: StartTransaction(0); if the reply's status byte is non-zero the
        // device only supports CTRL1 planar. If the read fails we keep the CTRL2 default.
        _hid.SetOutputReport(Cmd(CMD_START_TX, b3: 0x00, b4: _lightCtrl));
        var reply = new byte[Pkt];
        if (_hid.GetInputReport(reply) && reply[2] != 0) _lightCtrl = CtrlPlanar;
        _hid.SetOutputReport(Cmd(CMD_STOP_TX, b3: 0x01));
        _inited = true;
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            _last = new List<(byte R, byte G, byte B)>(colors);   // snapshot for keepalive
            return SendLocked(colors);
        }
    }

    /// <summary>Re-sends the last frame (keepalive). No-op (true) if nothing has been set yet.</summary>
    public bool Refresh()
    {
        lock (_io) return _last is null || SendLocked(_last);
    }

    private bool SendLocked(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        EnsureInit();
        byte[] data = _lightCtrl == CtrlPlanar
            ? BuildBufferPlanar(colors, _leds)
            : BuildBufferTriplet(colors, _leds);

        if (!_hid.SetOutputReport(Cmd(CMD_START_TX, b3: 0x00, b4: _lightCtrl))) return false;
        byte[] first = BuildBlockFirst(_writeCmd, data, out int sent);
        if (!_hid.SetOutputReport(first)) return false;
        while (sent < data.Length)
        {
            byte[] next = BuildBlockNext(data, sent, out int n);
            if (!_hid.SetOutputReport(next)) return false;
            sent += n;
        }
        return _hid.SetOutputReport(Cmd(CMD_STOP_TX, b3: 0x01));
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var c = new (byte, byte, byte)[_leds];
        for (int i = 0; i < _leds; i++) c[i] = (r, g, b);
        return SetLeds(c);
    }
}

internal sealed class CorsairV2RgbController : IRgbController
{
    private readonly CorsairV2Controller _dev;
    public string Id { get; }
    public string Label { get; }
    public int LedCount { get; }
    public RgbZoneKind Kind { get; }
    public RgbTransport Transport => RgbTransport.UsbHid;
    public int KeepaliveIntervalMs { get; }

    public CorsairV2RgbController(HidDevice hid, CorsairV2Controller.Model m)
    {
        _dev = new CorsairV2Controller(hid, m.Leds); Id = m.Id; Label = m.Label; LedCount = m.Leds; Kind = m.Kind;
        KeepaliveIntervalMs = m.KeepaliveMs;
    }

    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
    public bool Refresh() => _dev.Refresh();
}
