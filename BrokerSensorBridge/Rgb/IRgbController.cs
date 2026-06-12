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

    /// <summary>Set every LED to one color. Returns false on any transport error.</summary>
    bool SetAll(byte r, byte g, byte b);

    /// <summary>
    /// Set per-LED colors (clamped to LedCount). A solid-color device collapses the frame to a
    /// single color. Returns false on any transport error.
    /// </summary>
    bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors);
}
