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
    private readonly Func<ISmbusBackend, bool> _available;
    private readonly Func<ISmbusBackend, RawReading> _read;

    public RawChannel(string rawId, string unit, int round,
                      Func<ISmbusBackend, bool> available, Func<ISmbusBackend, RawReading> read)
    {
        RawId = rawId; DefaultUnit = unit; Round = round; _available = available; _read = read;
    }

    public bool IsAvailable(ISmbusBackend b) => _available(b);
    public RawReading ReadBase(ISmbusBackend b) => _read(b);
}
