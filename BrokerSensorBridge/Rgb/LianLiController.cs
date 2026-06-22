namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| LianLiUniHubSlV2Controller                                                  |
|                                                                            |
|   Lian Li Uni Hub SL V2 / AL V2 / SL V2 v0.5 over USB-HID (VID 0x0CF2, PIDs  |
|   0xA103/0xA104/0xA105, interface 1, usage FF72:00A1), reproduced as          |
|   protocol FACTS. OUTPUT reports, report id 0xE0, all packets 353 bytes.      |
|   4 channels, 16 LEDs per fan. Per channel: START (set fan count) -> COLOR    |
|   data -> COMMIT (mode/speed/dir/brightness). Color byte order is R,B,G       |
|   (NOT RGB). A total-power LED limiter scales any pixel whose R+B+G > 460.    |
|                                                                            |
|   IMPORTANT: a hub's LED count = (configured fans per channel) x 16 — it is   |
|   NOT read from the device. With no per-channel fan-count config yet, this    |
|   drives ONE fan per channel (a safe baseline that never overruns; the first  |
|   fan of each channel lights). Multi-fan support is a future config item.     |
|   The AL / SL / SL-Infinity variants (different packet sizes / sub-rings /     |
|   firmware gates) are queued — see docs/RGB-DEVICE-COVERAGE.md.               |
\*---------------------------------------------------------------------------*/
internal sealed class LianLiUniHubSlV2Controller
{
    public const ushort VendorId = 0x0CF2;
    public const int Interface = 1;
    public const ushort UsagePage = 0xFF72, Usage = 0x00A1;
    public static readonly ushort[] ProductIds = { 0xA103, 0xA104, 0xA105 };

    public const int Channels = 4;
    public const int LedsPerFan = 16;
    private const int FansPerChannel = 1;          // safe baseline (no per-channel config yet)
    private const int PowerCap = 460;              // R+B+G total-power limiter
    private const int Len = 353;
    private const byte ReportId = 0xE0;

    private readonly HidDevice _hid;
    private readonly object _io = new();
    public LianLiUniHubSlV2Controller(HidDevice hid) { _hid = hid; }

    public int LedCount => Channels * FansPerChannel * LedsPerFan;

    /// <summary>Total-power LED limiter: if R+B+G &gt; 460, scale all three down by 460/sum. Pure/testable.</summary>
    public static (byte R, byte B, byte G) Limit(byte r, byte b, byte g)
    {
        int sum = r + b + g;
        if (sum <= PowerCap) return (r, b, g);
        return ((byte)(r * PowerCap / sum), (byte)(b * PowerCap / sum), (byte)(g * PowerCap / sum));
    }

    public static byte[] BuildStart(int channel, int numFans)
    { var p = new byte[Len]; p[0] = ReportId; p[1] = 0x10; p[2] = 0x60; p[3] = (byte)((channel << 4) | numFans); return p; }

    /// <summary>COLOR data packet for one channel: [E0, 0x30+ch] then numLeds × (R,B,G).</summary>
    public static byte[] BuildColor(int channel, IReadOnlyList<(byte R, byte G, byte B)> c, int offset, int numLeds)
    {
        var p = new byte[Len]; p[0] = ReportId; p[1] = (byte)(0x30 + channel);
        for (int i = 0; i < numLeds; i++)
        {
            (byte R, byte G, byte B) col = (offset + i) < c.Count ? c[offset + i] : ((byte)0, (byte)0, (byte)0);
            (byte R, byte B, byte G) lim = Limit(col.R, col.B, col.G);
            int o = 2 + i * 3; p[o] = lim.R; p[o + 1] = lim.B; p[o + 2] = lim.G;   // R, B, G wire order
        }
        return p;
    }

    public static byte[] BuildCommit(int channel)
    {
        var p = new byte[Len]; p[0] = ReportId; p[1] = (byte)(0x10 + channel);
        p[2] = 0x01;   // static effect
        p[3] = 0x02;   // speed code
        p[4] = 0x00;   // direction
        p[5] = 0x00;   // brightness 0x00 = 100%
        return p;
    }

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        lock (_io)
        {
            int ledsPerChannel = FansPerChannel * LedsPerFan;
            for (int ch = 0; ch < Channels; ch++)
            {
                if (!_hid.SetOutputReport(BuildStart(ch, FansPerChannel))) return false;
                if (!_hid.SetOutputReport(BuildColor(ch, colors, ch * ledsPerChannel, ledsPerChannel))) return false;
                if (!_hid.SetOutputReport(BuildCommit(ch))) return false;
            }
            return true;
        }
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        var c = new (byte, byte, byte)[LedCount];
        for (int i = 0; i < LedCount; i++) c[i] = (r, g, b);
        return SetLeds(c);
    }
}

internal sealed class LianLiUniHubSlV2RgbController : IRgbController
{
    private readonly LianLiUniHubSlV2Controller _dev;
    public string Id => "lianli.unihub.slv2";
    public string Label => "Lian Li Uni Hub SL V2";
    public int LedCount => _dev.LedCount;
    public RgbZoneKind Kind => RgbZoneKind.Cooler;
    public RgbTransport Transport => RgbTransport.UsbHid;
    public LianLiUniHubSlV2RgbController(HidDevice hid) { _dev = new LianLiUniHubSlV2Controller(hid); }
    public bool SetAll(byte r, byte g, byte b) => _dev.SetAll(r, g, b);
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> c) => _dev.SetLeds(c);
}
