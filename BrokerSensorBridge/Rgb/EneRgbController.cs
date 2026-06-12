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

    public EneRgbController(ISmbusBackend backend, RgbDevice dev)
    {
        _ene = new EneController(backend, dev.Bus, dev.Address);
        Id = dev.Id;
        Label = dev.Label;
        LedCount = dev.LedCount;
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
