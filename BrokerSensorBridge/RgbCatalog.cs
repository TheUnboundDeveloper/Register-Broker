namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| RgbCatalog                                                                 |
|                                                                            |
|   The control service's BAKED map of named RGB devices to their physical   |
|   SMBus (bus, address). Clients name a logical device ("ram0") and a       |
|   color; they never supply or discover an address (the write-safety rule). |
|   The kernel brick-guard independently confirms the address is an RGB      |
|   controller window, so even this map cannot reach SPD.                     |
|                                                                            |
|   Dev box (MSI B550I + G.Skill DDR4): the ENE/Aura DRAM controllers are at  |
|   bus 0 (primary FCH SMBus), addresses 0x39 and 0x3A.                       |
\*---------------------------------------------------------------------------*/
internal sealed record RgbDevice(string Id, string Label, int Bus, int Address, int LedCount);

internal static class RgbCatalog
{
    public static readonly IReadOnlyList<RgbDevice> Devices = new[]
    {
        new RgbDevice("ram0", "GSkill RGB (DIMM 0)", 0, 0x39, 5),
        new RgbDevice("ram1", "GSkill RGB (DIMM 1)", 0, 0x3A, 5),
    };

    public static RgbDevice? Find(string id) =>
        Devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sets a named device to one color via the ENE protocol. Returns false on any write error.</summary>
    public static bool Set(ISmbusBackend backend, RgbDevice dev, byte r, byte g, byte b)
    {
        var ene = new EneController(backend, dev.Bus, dev.Address);
        return ene.SetAllDirect(r, g, b, dev.LedCount);
    }

    /// <summary>
    /// Sets a named device's LEDs to per-LED colors via the ENE protocol. The list is clamped to
    /// the device's baked LedCount (never writes past it). Returns false on any write error.
    /// </summary>
    public static bool SetLeds(ISmbusBackend backend, RgbDevice dev, IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        var ene = new EneController(backend, dev.Bus, dev.Address);
        IReadOnlyList<(byte R, byte G, byte B)> clamped =
            colors.Count > dev.LedCount ? colors.Take(dev.LedCount).ToList() : colors;
        return ene.SetDirect(clamped);
    }
}
