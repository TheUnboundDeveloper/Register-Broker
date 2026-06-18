namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| MysticLightHidController                                                    |
|                                                                            |
|   IRgbController over MSI Mystic Light USB-HID (the working motherboard      |
|   header path on NCT6687-era MSI boards). Ported as a PUBLIC PROTOCOL        |
|   (documented facts, cross-checked, not copied code; see THIRD-PARTY-        |
|   NOTICES.md). Two write paths, chosen by zone kind:                         |
|                                                                            |
|   * ADDRESSABLE headers (RgbZoneKind.MbArgb, JRAINBOW/JARGB) use PER-LED     |
|     DIRECT mode — feature report 0x53 (FeaturePacket_PerLED_185):            |
|       [0]      report id 0x53                                               |
|       [1]      hdr0 = 0x25  (MSI_DIRECT_MODE)                               |
|       [2]      hdr1       <- PER-ZONE selector (JRAINBOW=4, JCORSAIR=5,      |
|       [3]      hdr2          onboard=6; JRAINBOW2 also sets hdr2=1)          |
|       [4]      hdr3 = 0x00                                                  |
|       [5..]    Color leds[240]  (this zone's LEDs, starting at index 0)     |
|       -> 5 + 240*3 = 725 bytes (matches the MS-7C92's featureLen 725).      |
|     Each addressable zone is its OWN 0x53 frame, addressed by hdr1/hdr2 —    |
|     NOT a flat slice of one shared array. The zone is first put into direct  |
|     mode (a 185-byte 0x52 packet with that zone's effect = 0x25 AND its      |
|     cycle_or_led_num set to the zone LED max), then 0x53 frames stream        |
|     literal RGB. Because direct mode bypasses the firmware sync/effect        |
|     engine, brightness is LINEAR in the RGB values — this fixes the "fold"    |
|     the 185-byte sync-static path showed on an addressable header, and lets   |
|     real per-LED color/gradients/effects through (SetLeds is no longer        |
|     collapsed to a single color).                                           |
|                                                                            |
|   * NON-ADDRESSABLE zones (12V JRGB etc.) use the 185-byte SYNC packet       |
|     (FeaturePacket_185, report id 0x52), static fixed color:                 |
|       ZoneData = effect[+0] R[+1] G[+2] B[+3] speedAndBrightness[+4]         |
|                  color2 RGB[+5..7] colorFlags[+8] padding[+9].               |
|     Static color: effect=MSI_MODE_STATIC, colorFlags bit7=1 (fixed color).   |
|                                                                            |
|   WHOLE-DEVICE PACKET: both reports carry every zone/LED, so a write seeds    |
|   from the device's CURRENT state (HidD_GetFeature) and edits only the        |
|   target zone/LED range — otherwise the other zones would go dark.           |
|                                                                            |
|   NO PER-FRAME RE-ASSERT: a held color/frame is written exactly once (we      |
|   cache the last write and suppress an identical re-send), so a consumer       |
|   driving rgb.set at frame rate doesn't restart the device every frame.       |
|                                                                            |
|   USER-MODE, reduced assurance (no kernel guard); opt-in (AllowHidRgb).      |
|   The 0x53 per-LED bring-up (JRAINBOW1 index range via HidLedOffset) is        |
|   confirmed empirically — see docs/RGB-BOARD-BRINGUP.md and --mystic-perled.  |
\*---------------------------------------------------------------------------*/
internal sealed class MysticLightHidController : IRgbController
{
    /* 185-byte SYNC packet (FeaturePacket_185) facts. */
    private const byte ReportId185   = 0x52;
    private const int  PacketLen185  = 185;
    private const byte MsiModeStatic = 0x01;   // MSI_MODE_STATIC

    /* ZoneData field offsets, relative to a zone's base byte in the 185-byte packet. */
    private const int ZEffect = 0, ZColorR = 1, ZColorG = 2, ZColorB = 3,
                      ZSpeedBright = 4, ZColorFlags = 8, ZRainbowLedCount = 10;
    private const byte StaticBrightnessFlags = 0x7C;   // (brightness 31 << 2) | speed 0
    private const byte ColorFlagsFixed = 0x80;         // colorFlags bit7 = fixed color (not rainbow)
    private const int  RainbowLedCountMax = 200;        // JRAINBOW1_MAX_LED_COUNT (185 sync-path fallback)

    /* PER-LED DIRECT frame (FeaturePacket_PerLED_185) facts. */
    private const byte PerLedReportId = 0x53;
    private const byte MsiDirectMode  = 0x25;   // hdr0, and the zone effect that enables direct mode
    private const int  PerLedHeaderLen = 5;     // report id + hdr0..hdr3
    /// <summary>LED capacity of the 0x53 per-LED direct frame (NUMOF_PER_LED_MODE_LEDS).</summary>
    internal const int PerLedMaxLeds = 240;
    private const int  PerLedFrameLen = PerLedHeaderLen + PerLedMaxLeds * 3;   // 725

    private readonly HidDevice _hid;
    private readonly int _zoneOffset;       // base byte of this zone's ZoneData in the 185-byte packet
    private readonly byte _perLedHdr1;      // per-zone selector in the 0x53 frame (JRAINBOW=4, ...)
    private readonly byte _perLedHdr2;      // per-zone sub-selector (JRAINBOW2 = 1)
    private readonly byte _rainbowLedCount; // cycle_or_led_num for the 185 sync-path fallback
    private readonly bool _supported;       // 185-byte variant present (sync path)
    private readonly bool _perLed;          // MbArgb + device advertises the 0x53 per-LED frame
    private readonly byte[] _packet = new byte[PacketLen185];
    private readonly byte[] _perLedFrame = new byte[PerLedFrameLen];
    private readonly object _io = new();
    private bool _seeded, _perLedSeeded, _directEnabled;

    /* Last write, to suppress an identical per-frame re-send. */
    private bool _hasLast;
    private byte _lastR, _lastG, _lastB;
    private (byte R, byte G, byte B)[]? _lastLeds;

    public string Id { get; }
    public string Label { get; }
    public int LedCount { get; }
    public RgbZoneKind Kind { get; }
    public RgbTransport Transport => RgbTransport.UsbHid;

    public MysticLightHidController(HidDevice hid, RgbZone zone)
    {
        _hid = hid;
        _zoneOffset = zone.HidZoneIndex;
        _perLedHdr1 = (byte)zone.HidPerLedHdr1;
        _perLedHdr2 = (byte)zone.HidPerLedHdr2;
        _rainbowLedCount = (byte)Math.Clamp(zone.LedCount, 1, RainbowLedCountMax);
        _supported = VariantSize(hid.FeatureReportByteLength) == PacketLen185;
        /* Drive an addressable header per-LED only when the device actually advertises a feature
           report large enough for the 0x53 frame (725 on the MS-7C92). Otherwise fall back to the
           185-byte sync path (folds, but better than nothing on an unmapped device). */
        _perLed = zone.Kind == RgbZoneKind.MbArgb && hid.FeatureReportByteLength >= PerLedFrameLen;
        Id = zone.Id;
        Label = zone.Label;
        LedCount = zone.LedCount;
        Kind = zone.Kind;
    }

    /// <summary>
    /// Map the device's max feature-report length (HidP_GetCaps reports the LARGEST report across all
    /// ids, e.g. 725 on MS-7C92) to the Mystic Light SYNC packet variant it uses (185 ≥ 162 ≥ 112).
    /// Only 185 is implemented today.
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
        if (_perLed)
        {
            int n = Math.Clamp(LedCount, 1, PerLedMaxLeds);
            var uniform = new (byte, byte, byte)[n];
            for (int i = 0; i < n; i++) uniform[i] = (r, g, b);
            return WritePerLed(uniform);
        }

        /* 185-byte sync-static fallback (non-addressable zones, or an addressable header on a device
           that does not advertise the 0x53 frame). MbArgb writes the 11-byte RainbowZoneData. */
        if (!_supported) return false;
        int lastField = Kind == RgbZoneKind.MbArgb ? ZRainbowLedCount : ZColorFlags;
        if (_zoneOffset < 1 || _zoneOffset + lastField >= PacketLen185) return false;

        lock (_io)
        {
            if (_hasLast && _lastR == r && _lastG == g && _lastB == b) return true;
            SeedSyncOnce();
            _packet[0] = ReportId185;
            BuildZonePacket(_packet, _zoneOffset, Kind, r, g, b, _rainbowLedCount);
            bool ok = _hid.SetFeature(_packet);
            if (ok) { _lastR = r; _lastG = g; _lastB = b; _hasLast = true; }
            return ok;
        }
    }

    /// <summary>
    /// Set per-LED colors. On an addressable header this writes literal per-LED RGB via the 0x53 direct
    /// frame (clamped to LedCount). On a non-addressable zone it collapses to the lead color.
    /// </summary>
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        if (colors.Count == 0) return false;
        if (_perLed) return WritePerLed(colors);
        (byte R, byte G, byte B) c = colors[0];
        return SetAll(c.R, c.G, c.B);
    }

    /// <summary>
    /// Write literal per-LED colors into the device's zone slice via the 0x53 direct frame. Puts the
    /// zone into direct mode once, seeds the frame from the device once (so LEDs outside our slice are
    /// preserved), and suppresses an identical consecutive frame.
    /// </summary>
    private bool WritePerLed(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        int n = Math.Min(colors.Count, Math.Min(LedCount, PerLedMaxLeds));
        if (n < 1) return false;

        lock (_io)
        {
            if (LedsUnchanged(colors, n)) return true;

            EnsureDirectModeEnabled();
            SeedPerLedOnce();
            BuildPerLedFrame(_perLedFrame, _perLedHdr1, _perLedHdr2, colors, n);

            bool ok = _hid.SetFeature(_perLedFrame);
            if (ok)
            {
                _lastLeds = new (byte, byte, byte)[n];
                for (int i = 0; i < n; i++) _lastLeds[i] = colors[i];
            }
            return ok;
        }
    }

    private bool LedsUnchanged(IReadOnlyList<(byte R, byte G, byte B)> colors, int n)
    {
        if (_lastLeds is null || _lastLeds.Length != n) return false;
        for (int i = 0; i < n; i++) if (_lastLeds[i] != colors[i]) return false;
        return true;
    }

    /// <summary>
    /// Lay the 0x53 per-LED direct frame for ONE addressable zone: report id, hdr0=0x25, the per-zone
    /// selector <paramref name="hdr1"/>/<paramref name="hdr2"/>, then literal RGB triplets for
    /// <paramref name="count"/> LEDs starting at the zone's first LED (index 0 of its own frame). LEDs
    /// past the written count are left as the caller seeded them. Pure/testable — no device I/O.
    /// </summary>
    internal static void BuildPerLedFrame(byte[] frame, byte hdr1, byte hdr2,
                                          IReadOnlyList<(byte R, byte G, byte B)> colors, int count)
    {
        frame[0] = PerLedReportId;
        frame[1] = MsiDirectMode;
        frame[2] = hdr1;
        frame[3] = hdr2;
        frame[4] = 0x00;
        for (int i = 0; i < count && i < PerLedMaxLeds; i++)
        {
            int p = PerLedHeaderLen + i * 3;
            frame[p + 0] = colors[i].R;
            frame[p + 1] = colors[i].G;
            frame[p + 2] = colors[i].B;
        }
    }

    /// <summary>
    /// Lay one zone's bytes into the 185-byte sync packet for a static fixed color. An MbArgb zone uses
    /// the 11-byte RainbowZoneData (ZoneData + the cycle_or_led_num LED-count byte at +10); every other
    /// zone uses the 10-byte ZoneData. Pure/testable. Returns the LED count written to +10 (else 0).
    /// </summary>
    internal static int BuildZonePacket(byte[] packet, int zoneOffset, RgbZoneKind kind,
                                        byte r, byte g, byte b, int rainbowLedCount)
    {
        int z = zoneOffset;
        packet[z + ZEffect]      = MsiModeStatic;
        packet[z + ZColorR]      = r;
        packet[z + ZColorG]      = g;
        packet[z + ZColorB]      = b;
        packet[z + ZSpeedBright] = StaticBrightnessFlags;
        packet[z + ZColorFlags]  = ColorFlagsFixed;

        if (kind == RgbZoneKind.MbArgb)
        {
            byte count = (byte)Math.Clamp(rainbowLedCount, 1, RainbowLedCountMax);
            packet[z + ZRainbowLedCount] = count;
            return count;
        }
        return 0;
    }

    /// <summary>
    /// Put the device into per-LED DIRECT mode once via the 185-byte (0x52) enable packet, then a short
    /// settle. The enable is a FULLY-FORMED packet (every zone present): the on_board_led zone carries
    /// the device-wide per-LED master flags (effect=0x25 + PER_LED_BASIC_SYNC_MODE), and j_rainbow_1/2
    /// are switched to MSI_DIRECT_MODE with cycle_or_led_num = the zone LED max. Without the full packet
    /// the firmware keeps running its effect engine (the "moving rainbow"), ignoring the 0x53 frames.
    /// </summary>
    private void EnsureDirectModeEnabled()
    {
        if (_directEnabled) return;
        _directEnabled = true;
        BuildDirectModeEnable(_packet);
        _hid.SetFeature(_packet);
        System.Threading.Thread.Sleep(15);   // let the firmware switch modes before the first 0x53 frame
    }

    /// <summary>
    /// Build the 185-byte (0x52) packet that switches the device into PER-LED DIRECT mode — a faithful
    /// reconstruction of the public protocol's enable_per_led_msg (non-sync / per-LED branch, used for
    /// strips &gt; 40 LEDs). Every ZoneData defaults to effect = MSI_MODE_STATIC (1); the overrides below
    /// match the documented field values. The on_board_led zone (offset 74) carries the device-wide
    /// per-LED master flags — it MUST be present. Pure/testable.
    /// </summary>
    internal static void BuildDirectModeEnable(byte[] p)
    {
        Array.Clear(p);
        p[0] = ReportId185;   // 0x52

        // ZoneData: effect@+0 (default MSI_MODE_STATIC=1), speedAndBrightness@+4, colorFlags@+8.
        void Zone(int b, byte effect, byte sb, byte cf) { p[b] = effect; p[b + 4] = sb; p[b + 8] = cf; }

        Zone(1,   0x01, 0x08, 0x80);                 // j_rgb_1
        Zone(11,  0x01, 0x2A, 0x80);                 // j_pipe_1
        Zone(21,  0x01, 0x2A, 0x80);                 // j_pipe_2
        Zone(31,  0x25, 0x29, 0x80); p[41] = 0xC8;   // j_rainbow_1: DIRECT, cycle=200 (JRAINBOW1_MAX)
        Zone(42,  0x25, 0x29, 0x80); p[52] = 0xF0;   // j_rainbow_2: DIRECT, cycle=240 (JRAINBOW2_MAX)
        // j_corsair (CorsairZoneData): effect@53, fan_flags@57, corsair_quantity@58, padding[2]@61, is_individual@63
        p[53] = 0x25; p[57] = 0x29; p[58] = 0x00; p[61] = 0x82; p[63] = 0xF0;
        Zone(64,  0x01, 0x28, 0x80);                 // j_corsair_outerll120
        Zone(74,  0x25, 0xA9, 0xB1);                 // on_board_led: DIRECT + PER_LED_BASIC_SYNC_MODE (master)
        Zone(84,  0x01, 0x28, 0x80);                 // on_board_led_1
        Zone(94,  0x01, 0x28, 0x80);                 // on_board_led_2
        Zone(104, 0x01, 0x28, 0x80);                 // on_board_led_3
        Zone(114, 0x01, 0x28, 0x80);                 // on_board_led_4
        Zone(124, 0x01, 0x28, 0x80);                 // on_board_led_5
        Zone(134, 0x01, 0x28, 0x80);                 // on_board_led_6
        Zone(144, 0x01, 0x28, 0x80);                 // on_board_led_7
        Zone(154, 0x01, 0x28, 0x80);                 // on_board_led_8
        Zone(164, 0x01, 0x28, 0x80);                 // on_board_led_9
        Zone(174, 0x01, 0x2A, 0x80);                 // j_rgb_2
        // save_data @184 = 0
    }

    private void SeedPerLedOnce()
    {
        if (_perLedSeeded) return;
        _perLedSeeded = true;
        Array.Clear(_perLedFrame);   // match the protocol: LEDs start zeroed; only driven LEDs are set
    }

    /// <summary>
    /// Seed the 185-byte packet from the device's current state once, so editing our zone preserves the
    /// other zones (they would otherwise be zeroed to MSI_MODE_DISABLE). Best-effort.
    /// </summary>
    private void SeedSyncOnce()
    {
        if (_seeded) return;
        _seeded = true;
        Array.Clear(_packet);
        _packet[0] = ReportId185;
        _hid.GetFeature(_packet);
        _packet[0] = ReportId185;
    }
}
