namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| MysticLightHidController                                                    |
|                                                                            |
|   IRgbController over MSI Mystic Light USB-HID (the working motherboard      |
|   header path on NCT6687-era MSI boards). Implements the 185-byte protocol   |
|   variant (FeaturePacket_185, report id 0x52) — ported as a PUBLIC PROTOCOL  |
|   (documented facts, cross-checked, not copied code; see THIRD-PARTY-        |
|   NOTICES.md). Layout (byte offsets into the 185-byte feature report):       |
|     [0]        report id 0x52                                               |
|     [1]   j_rgb_1        (ZoneData, 10 bytes)                               |
|     [11]  j_pipe_1/2     (ZoneData)                                         |
|     [31]  j_rainbow_1    (RainbowZoneData, 11 bytes)  <- JRAINBOW ARGB       |
|     [42]  j_rainbow_2    (RainbowZoneData)                                  |
|     [74]  on_board_led_* (ZoneData)                                         |
|     [174] j_rgb_2        (ZoneData)                                         |
|     [184] save_data                                                        |
|   ZoneData = effect[+0] R[+1] G[+2] B[+3] speedAndBrightness[+4]            |
|              color2 RGB[+5..7] colorFlags[+8] padding[+9].                  |
|   Static color: effect=MSI_MODE_STATIC, RGB=color, brightness in            |
|   speedAndBrightness, colorFlags bit7=1 (fixed color, not rainbow).         |
|                                                                            |
|   WHOLE-DEVICE PACKET: the report carries every zone, so a write seeds from  |
|   the device's CURRENT state (HidD_GetFeature) and edits only the target     |
|   zone — otherwise the other zones would be zeroed to MSI_MODE_DISABLE.      |
|                                                                            |
|   USER-MODE, reduced assurance (no kernel guard); opt-in (AllowHidRgb).      |
|   HW-VALIDATED 2026-06-13 on MSI MPG B550I (MS-7C92, PID 0x7C92, 185 variant):|
|   solid color on JRAINBOW1 confirmed, other zones preserved. StaticBrightness |
|   Flags is the brightness tunable; per-LED (report 0x53) is a future item     |
|   (SetLeds drives the lead color for now).                                   |
\*---------------------------------------------------------------------------*/
internal sealed class MysticLightHidController : IRgbController
{
    /* FeaturePacket_185 facts (public Mystic Light protocol). */
    private const byte ReportId185   = 0x52;
    private const int  PacketLen185  = 185;
    private const byte MsiModeStatic = 0x01;   // MSI_MODE_STATIC

    /* ZoneData field offsets, relative to a zone's base byte in the packet. */
    private const int ZEffect = 0, ZColorR = 1, ZColorG = 2, ZColorB = 3,
                      ZSpeedBright = 4, ZColorFlags = 8;

    /* speedAndBrightnessFlags = (brightness << 2) | (speed & 0x03). 0x7C = brightness 31 (max of the
       5-bit field, bits 2..6), speed 0. The primary HW-validate tunable for "are the LEDs bright?". */
    private const byte StaticBrightnessFlags = 0x7C;
    private const byte ColorFlagsFixed = 0x80;   // colorFlags bit7 = fixed color (not rainbow)

    private readonly HidDevice _hid;
    private readonly int _zoneOffset;       // base byte of this zone's ZoneData in the packet
    private readonly bool _supported;       // only the 185-byte variant is implemented today
    private readonly byte[] _packet = new byte[PacketLen185];
    private readonly object _io = new();
    private bool _seeded;

    public string Id { get; }
    public string Label { get; }
    public int LedCount { get; }
    public RgbZoneKind Kind { get; }
    public RgbTransport Transport => RgbTransport.UsbHid;

    public MysticLightHidController(HidDevice hid, RgbZone zone)
    {
        _hid = hid;
        _zoneOffset = zone.HidZoneIndex;     // a fixed packet byte offset (e.g. 31 = j_rainbow_1)
        _supported = VariantSize(hid.FeatureReportByteLength) == PacketLen185;
        Id = zone.Id;
        Label = zone.Label;
        LedCount = zone.LedCount;
        Kind = zone.Kind;
    }

    /// <summary>
    /// Map the device's max feature-report length (HidP_GetCaps reports the LARGEST report across all
    /// ids, e.g. 725 on MS-7C92) to the Mystic Light packet variant it uses (185 ≥ 162 ≥ 112), the way
    /// the public protocol classifies it. Only 185 is implemented today.
    /// </summary>
    private static int VariantSize(int featureReportByteLength) => featureReportByteLength switch
    {
        >= 185 => 185,
        >= 162 => 162,
        >= 112 => 112,
        _ => 185,
    };

    public bool SetAll(byte r, byte g, byte b)
    {
        if (!_supported) return false;                                  // 162/112 variants not yet ported
        if (_zoneOffset < 1 || _zoneOffset + ZColorFlags >= PacketLen185) return false;

        lock (_io)
        {
            SeedFromDeviceOnce();

            _packet[0] = ReportId185;
            int z = _zoneOffset;
            _packet[z + ZEffect]      = MsiModeStatic;
            _packet[z + ZColorR]      = r;
            _packet[z + ZColorG]      = g;
            _packet[z + ZColorB]      = b;
            _packet[z + ZSpeedBright] = StaticBrightnessFlags;
            _packet[z + ZColorFlags]  = ColorFlagsFixed;
            return _hid.SetFeature(_packet);
        }
    }

    /// <summary>
    /// Per-LED on an addressable header. The per-LED direct frame (report 0x53) is a future item; until
    /// it is ported this drives the zone to the lead color (still useful, never wrong-colored).
    /// </summary>
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        if (colors.Count == 0) return false;
        (byte R, byte G, byte B) c = colors[0];
        return SetAll(c.R, c.G, c.B);
    }

    /// <summary>
    /// Seed the packet from the device's CURRENT feature report once, so editing our zone preserves the
    /// other zones (they would otherwise be zeroed to MSI_MODE_DISABLE). Best-effort: if the read fails
    /// we proceed with a zeroed packet (only our zone is driven; others may go dark) — logged nowhere
    /// here, but a failed GetFeature is rare on a device we just opened R/W.
    /// </summary>
    private void SeedFromDeviceOnce()
    {
        if (_seeded) return;
        _seeded = true;
        Array.Clear(_packet);
        _packet[0] = ReportId185;
        _hid.GetFeature(_packet);    // fills current per-zone state; failure leaves the zeroed packet
        _packet[0] = ReportId185;    // ensure the report id survived the read
    }
}
