namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| RedragonMouseController                                                     |
|                                                                            |
|   Redragon mice over USB-HID (VID 0x04D9, vendor collection interface 2,    |
|   usage page 0xFFA0), reproduced as protocol FACTS. One protocol covers all  |
|   10 models; all are FIXED 1 LED / 1 zone. FEATURE report id 0x02, 16 bytes. |
|   A color update = a register WRITE (0xF3) to address 0x0449 (R,G,B at byte  |
|   8) followed by an APPLY (0xF1). Profile select (0x002C) + apply runs once. |
|   Board-independent, user-mode, opt-in (AllowHidRgb), reduced assurance.     |
|                                                                            |
|   The device auto-persists each write to its onboard profile (flash), so     |
|   identical colors are DE-DUPLICATED to avoid flash wear.                    |
\*---------------------------------------------------------------------------*/
internal sealed class RedragonMouseController
{
    public const ushort VendorId = 0x04D9;
    public const int Interface = 2;
    public const ushort UsagePage = 0xFFA0;
    public const int LedCount = 1;

    public static readonly ushort[] ProductIds =
    {
        0xFC30, 0xFC39, 0xFC3A, 0xFC4D, 0xFC38, 0xFC5F, 0xFC58, 0xFA7E, 0xFC69, 0xFC40,
    };

    private const byte ReportId = 0x02;
    private const int Len = 16;
    private const int AddrProfile = 0x002C, AddrColor = 0x0449;

    private readonly HidDevice _hid;
    private readonly object _io = new();
    private bool _inited;
    private (byte R, byte G, byte B)? _last;

    public RedragonMouseController(HidDevice hid) { _hid = hid; }

    /// <summary>Register-write feature report (0xF3): addr at [2..3], data length at [4], data from [8].</summary>
    public static byte[] BuildWrite(int addr, ReadOnlySpan<byte> data)
    {
        var p = new byte[Len];
        p[0] = ReportId; p[1] = 0xF3; p[2] = (byte)(addr & 0xFF); p[3] = (byte)(addr >> 8); p[4] = (byte)data.Length;
        data.CopyTo(p.AsSpan(8));
        return p;
    }

    /// <summary>Apply/commit feature report (0xF1).</summary>
    public static byte[] BuildApply()
    {
        var p = new byte[Len];
        p[0] = ReportId; p[1] = 0xF1; p[2] = 0x02; p[3] = 0x04;
        return p;
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        lock (_io)
        {
            if (_last is { } p && p.R == r && p.G == g && p.B == b) return true;   // skip flash re-write
            if (!_inited)
            {
                _hid.SetFeature(BuildWrite(AddrProfile, stackalloc byte[1] { 0x00 }));   // select profile 0
                _hid.SetFeature(BuildApply());
                _inited = true;
            }
            bool ok = _hid.SetFeature(BuildWrite(AddrColor, stackalloc byte[3] { r, g, b })) && _hid.SetFeature(BuildApply());
            if (ok) _last = (r, g, b);
            return ok;
        }
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        (byte R, byte G, byte B) c = colors.Count > 0 ? colors[0] : ((byte)0, (byte)0, (byte)0);
        return SetAll(c.R, c.G, c.B);
    }
}

internal sealed class RedragonMouseRgbController : IRgbController
{
    private readonly RedragonMouseController _dev;
    public string Id => "redragon.mouse";
    public string Label { get; }
    public int LedCount => RedragonMouseController.LedCount;
    public RgbZoneKind Kind => RgbZoneKind.Mouse;
    public RgbTransport Transport => RgbTransport.UsbHid;
    public RedragonMouseRgbController(HidDevice hid, ushort pid) { _dev = new RedragonMouseController(hid); Label = $"Redragon Mouse (PID 0x{pid:X4})"; }
    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}
