namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| RawChannel                                                                 |
|                                                                            |
|   A RawChannel is a chip backend's view of one sensor BEFORE any board      |
|   knowledge: a stable raw id (`{chip}.{kind}.{index}`), a native unit, and  |
|   a base value produced by the chip decoder. It carries NO label and NO     |
|   per-rail scale — those are board calibration, applied later in            |
|   SensorCatalog. Raw ids are the stable persistence key (see                |
|   CALIBRATION-AND-REGISTRY-PLAN.md, decision #1); legacy semantic ids       |
|   resolve to them via the alias map.                                       |
|                                                                            |
|   IMPORTANT: the register/EC/SMN knowledge stays in the kernel driver and   |
|   in SensorDecode — a RawChannel only names a backend operation, never an   |
|   address. The channels themselves are declared per backend in              |
|   ChannelRegistry (adding a chip = one registry entry + a decoder).         |
\*---------------------------------------------------------------------------*/
internal readonly record struct RawReading(bool Ok, double Value, string Status);

internal sealed class RawChannel
{
    public string RawId { get; }
    public string DefaultUnit { get; }
    public int Round { get; }                       // decimal places for the final (scaled) value
    /// <summary>
    /// True for a channel whose source can be physically unplugged at runtime (e.g. an off-board
    /// USB-HID controller like the Aquacomputer Quadro). Such a channel legitimately appears and
    /// disappears as the controller is hot-plugged: consumers should render "not connected" on
    /// absence rather than treat it as an error. Static, board-fixed sources (SMBus/Super-I/O/SMU)
    /// leave this false. Surfaced in sensor.list so a consumer can distinguish hot-plug absence
    /// from a sensor that never existed.
    /// </summary>
    public bool Removable { get; }
    private readonly Func<ISmbusBackend, bool> _available;
    private readonly Func<ISmbusBackend, RawReading> _read;

    public RawChannel(string rawId, string unit, int round,
                      Func<ISmbusBackend, bool> available, Func<ISmbusBackend, RawReading> read,
                      bool removable = false)
    {
        RawId = rawId; DefaultUnit = unit; Round = round; _available = available; _read = read;
        Removable = removable;
    }

    public bool IsAvailable(ISmbusBackend b) => _available(b);
    public RawReading ReadBase(ISmbusBackend b) => _read(b);
}
