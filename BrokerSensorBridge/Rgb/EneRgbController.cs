namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| EneRgbController                                                           |
|                                                                            |
|   IRgbController wrapper over the existing ENE/Aura SMBus DRAM path. This   |
|   preserves the validated behavior exactly — it just routes through the     |
|   same EneController register protocol the broker used before the           |
|   IRgbController seam existed (see RgbCatalog / EneController).             |
\*---------------------------------------------------------------------------*/
internal sealed class EneRgbController : IRgbController
{
    /* ONE persistent controller per device: EneController latches DIRECT mode on the
       first frame and skips it afterwards (the blink fix) — constructing a fresh
       instance per call would re-latch every frame and bring the blink back. */
    private readonly EneController _ene;

    public string Id { get; }
    public string Label { get; }
    public int LedCount { get; }
    public RgbZoneKind Kind { get; }
    public RgbTransport Transport => RgbTransport.SmbusEne;

    public EneRgbController(ISmbusBackend backend, RgbZone zone)
    {
        _ene = new EneController(backend, zone.Bus, zone.Address);
        Id = zone.Id;
        Label = zone.Label;
        LedCount = zone.LedCount;
        Kind = zone.Kind;
    }

    public bool SetAll(byte r, byte g, byte b)
        => _ene.SetAllDirect(r, g, b, LedCount);

    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        IReadOnlyList<(byte R, byte G, byte B)> clamped =
            colors.Count > LedCount ? colors.Take(LedCount).ToList() : colors;
        return _ene.SetDirect(clamped);
    }
}
