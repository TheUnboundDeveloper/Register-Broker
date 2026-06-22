namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| IRgbController                                                             |
|                                                                            |
|   A single named RGB device the control service can drive, independent of  |
|   its transport. This is the seam that lets the validated ENE/Aura SMBus    |
|   DRAM path and any future transport (USB-HID, EC, ...) coexist behind one  |
|   broker contract (rgb.list / rgb.set). Clients still name a logical device |
|   and a color — never an address, a register, or a transport.              |
\*---------------------------------------------------------------------------*/
internal interface IRgbController
{
    /// <summary>Stable logical id (persistence key for consumers); never an address.</summary>
    string Id { get; }

    /// <summary>Human-readable label for rgb.list.</summary>
    string Label { get; }

    /// <summary>Number of independently-addressable LEDs (1 for a solid-color zone).</summary>
    int LedCount { get; }

    /// <summary>Capability class of this zone (DRAM / 12V header / addressable header), for rgb.list grouping.</summary>
    RgbZoneKind Kind { get; }

    /// <summary>The transport this zone is driven over (diagnostic; reported in rgb.list).</summary>
    RgbTransport Transport { get; }

    /// <summary>Set every LED to one color. Returns false on any transport error.</summary>
    bool SetAll(byte r, byte g, byte b);

    /// <summary>
    /// Set per-LED colors (clamped to LedCount). A solid-color device collapses the frame to a
    /// single color. Returns false on any transport error.
    /// </summary>
    bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors);

    /// <summary>
    /// Milliseconds between keepalive refreshes, or 0 (the default) when the device holds its color
    /// with no refresh. Some firmware (HyperX, Thermaltake Quad, Corsair K55, …) reverts to its
    /// stored effect unless a color frame is re-sent within this interval. Such devices are always
    /// the VOLATILE ones (no flash write), so refreshing is wear-free.
    /// </summary>
    int KeepaliveIntervalMs => 0;

    /// <summary>
    /// Re-send the last color (keepalive). Default no-op for devices that hold their color. A
    /// keepalive device caches its last frame in SetAll/SetLeds and re-sends it here; returns true
    /// if nothing has been set yet. Returns false only on a transport error.
    /// </summary>
    bool Refresh() => true;
}
