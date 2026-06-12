namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| SensorCatalog                                                              |
|                                                                            |
|   The broker's served set of named, read-only sensors — now ASSEMBLED from  |
|   two layers (see CALIBRATION-AND-REGISTRY-PLAN.md):                        |
|     * raw channels (ChipChannels) — stable ids + base values, from the       |
|       trusted chip backends; carry no labels.                               |
|     * board calibration (CalibrationStore) — labels + per-rail scales,       |
|       DATA keyed by board DMI; can only rename/rescale/hide, never address. |
|                                                                            |
|   Clients still request data by logical id and cannot scan hardware. The    |
|   public id is the stable raw id (e.g. nct6687d.volt.0); legacy semantic    |
|   ids (board.12v.volt) still resolve via the calibration alias map, so      |
|   saved consumer selections keep working.                                   |
\*---------------------------------------------------------------------------*/
internal sealed record SensorReading(bool Ok, double Value, string Unit, string Status);

internal sealed class SensorCatalogEntry
{
    public string Id { get; }
    public string Label { get; }
    public string Unit { get; }
    private readonly Func<ISmbusBackend, bool> _available;
    private readonly Func<ISmbusBackend, SensorReading> _read;

    public SensorCatalogEntry(string id, string label, string unit,
                              Func<ISmbusBackend, bool> available,
                              Func<ISmbusBackend, SensorReading> read)
    {
        Id = id; Label = label; Unit = unit; _available = available; _read = read;
    }

    public bool IsAvailable(ISmbusBackend b) => _available(b);
    public SensorReading Read(ISmbusBackend b) => _read(b);
}

internal static class SensorCatalog
{
    private static CalibrationStore _store = CalibrationStore.Builtin;
    private static IReadOnlyList<SensorCatalogEntry> _entries = BuildEntries(_store);

    /// <summary>Install the loaded calibration and rebuild the served catalog. Call once at startup.</summary>
    public static void Configure(CalibrationStore store)
    {
        _store = store;
        _entries = BuildEntries(store);
    }

    private static IReadOnlyList<SensorCatalogEntry> BuildEntries(CalibrationStore store)
    {
        var list = new List<SensorCatalogEntry>(ChipChannels.All.Count);
        foreach (RawChannel ch in ChipChannels.All)
        {
            ChannelOverride cal = store.Resolve(ch.RawId);
            if (cal.Hidden) continue;                                   // calibration can hide a channel

            string label = cal.Label ?? ch.RawId;                       // passthrough = the raw id
            string unit = cal.Unit ?? ch.DefaultUnit;
            double scale = cal.Scale;
            double offset = cal.Offset;
            int round = ch.Round;

            list.Add(new SensorCatalogEntry(ch.RawId, label, unit,
                available: ch.IsAvailable,
                read: b =>
                {
                    RawReading r = ch.ReadBase(b);
                    return r.Ok
                        ? new SensorReading(true, Math.Round(r.Value * scale + offset, round), unit, "Ok")
                        : new SensorReading(false, 0, unit, r.Status);
                }));
        }
        return list;
    }

    /// <summary>Find by stable raw id, or by a legacy/semantic id via the calibration alias map.</summary>
    public static SensorCatalogEntry? Find(string id)
    {
        SensorCatalogEntry? e = _entries.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (e != null) return e;
        string? raw = _store.ResolveAlias(id);
        return raw == null ? null : _entries.FirstOrDefault(x => string.Equals(x.Id, raw, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<SensorCatalogEntry> Available(ISmbusBackend b) => _entries.Where(e => e.IsAvailable(b));

    /// <summary>All configured entries (regardless of availability) — for the --calibration inspector.</summary>
    public static IReadOnlyList<SensorCatalogEntry> All => _entries;
}
