namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| MysticLightEcController                                                     |
|                                                                            |
|   IRgbController over the NCT6687 EC RGB register write path (kernel         |
|   IOCTL_BROKER_SUPERIO_RGB_WRITE). Drives a non-addressable 12V motherboard  |
|   header (JRGB) as a single solid-color zone — every LED shows one color, so |
|   LedCount is 1 and per-LED frames collapse to the first color.             |
|                                                                            |
|   HW-UNVALIDATED: the NCT6687 RGB register layout is not yet confirmed, so   |
|   the kernel keeps this path inert (CAP_SUPERIO_RGB off → this controller    |
|   is never instantiated). The color write here is the expected shape (R,G,B  |
|   to the zone's EC base); the exact register order is a bring-up item.       |
|   One persistent instance per zone (RgbRegistry), all writes serialized.     |
\*---------------------------------------------------------------------------*/
internal sealed class MysticLightEcController : IRgbController
{
    private readonly ISmbusBackend _backend;
    private readonly int _ecAddress;
    private readonly object _io = new();

    public string Id { get; }
    public string Label { get; }
    public int LedCount { get; }
    public RgbZoneKind Kind { get; }
    public RgbTransport Transport => RgbTransport.SuperioEc;

    public MysticLightEcController(ISmbusBackend backend, RgbZone zone)
    {
        _backend = backend;
        _ecAddress = zone.EcAddress;
        Id = zone.Id;
        Label = zone.Label;
        LedCount = zone.LedCount;
        Kind = zone.Kind;
    }

    public bool SetAll(byte r, byte g, byte b)
    {
        lock (_io)
        {
            Span<byte> rgb = stackalloc byte[3] { r, g, b };
            return _backend.TrySuperioRgbWrite(_ecAddress, rgb, out _);
        }
    }

    /// <summary>A solid-color header collapses a per-LED frame to its first color.</summary>
    public bool SetLeds(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        if (colors.Count == 0) return false;
        (byte R, byte G, byte B) c = colors[0];
        return SetAll(c.R, c.G, c.B);
    }
}
