namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| RgbRegistry                                                                |
|                                                                            |
|   The control service's live set of drivable RGB devices, built at startup  |
|   from whatever transports are actually present:                            |
|     * ENE/Aura SMBus DRAM (RgbCatalog) — only when the kernel driver's       |
|       brick-guarded write path is available (unchanged from before).        |
|                                                                            |
|   This is the auto-detect merge point: each transport contributes devices   |
|   only when its hardware is found, so nothing appears on the wrong board.    |
|                                                                              |
|   The Gigabyte IT8297 USB-HID transport was retired 2026-06-11 after expert  |
|   corrections (design record: docs/GIGABYTE-SUPPORT.md); the registry stays  |
|   transport-agnostic so a corrected version can plug back in.                |
\*---------------------------------------------------------------------------*/
internal sealed class RgbRegistry
{
    private readonly List<IRgbController> _devices;

    private RgbRegistry(List<IRgbController> devices) { _devices = devices; }

    public IReadOnlyList<IRgbController> Devices => _devices;
    public bool Any => _devices.Count > 0;

    public IRgbController? Find(string id) =>
        _devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    public static RgbRegistry Build(ISmbusBackend smbus, Action<string> log)
    {
        var list = new List<IRgbController>();

        // ENE/Aura DRAM over the kernel driver (same condition as before: needs CAP_WRITE).
        if (smbus.WriteAvailable)
            foreach (RgbDevice d in RgbCatalog.Devices)
                list.Add(new EneRgbController(smbus, d));

        log($"[rgb] registry: {list.Count} device(s) [{string.Join(", ", list.Select(d => d.Id))}]");
        return new RgbRegistry(list);
    }
}
