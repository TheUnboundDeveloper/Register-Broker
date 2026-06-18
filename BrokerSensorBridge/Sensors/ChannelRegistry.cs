namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| ChannelRegistry — the broker-side backend/channel table                     |
|                                                                            |
|   The Phase-3 registry (CALIBRATION-AND-REGISTRY-PLAN.md): every sensor     |
|   backend the broker knows is ONE declarative entry here — a name matching  |
|   the kernel's registry (IOCTL_BROKER_ENUM_BACKENDS), the raw-id prefix it  |
|   owns, its availability gate, and its channel list. Adding a chip =        |
|   adding one entry (plus its decoder in SensorDecode).                     |
|                                                                            |
|   This is deliberately a C# CODE table, not a data file: which channels     |
|   exist is part of the signed, reviewed surface (register maps live in      |
|   signed code, never in data). Calibration JSON can relabel/rescale/hide    |
|   served entries — it can never add one.                                   |
|                                                                            |
|   Gates key off the driver's CAP_* bits and detected chip id (NOT off the   |
|   enumeration op), so behavior is identical on a driver that predates       |
|   ENUM_BACKENDS. DriverBackends ties each entry to the kernel-registry      |
|   names it rides, and Validate() + selftest enforce the table's integrity   |
|   (unique names/ids, prefix ownership, known driver names).                |
\*---------------------------------------------------------------------------*/
internal sealed class ChannelBackendDef
{
    /// <summary>Display name of this channel group (used in logs / diagnostics).</summary>
    public string Name { get; }
    /// <summary>Kernel registry (ENUM_BACKENDS) backend names this group rides on.</summary>
    public IReadOnlyList<string> DriverBackends { get; }
    /// <summary>The stable raw-id prefix this entry owns (every channel id starts with it).</summary>
    public string IdPrefix { get; }
    /// <summary>Backend-level availability (per-channel predicates may narrow further).</summary>
    public Func<ISmbusBackend, bool> Gate { get; }
    public IReadOnlyList<RawChannel> Channels { get; }

    public ChannelBackendDef(string name, string[] driverBackends, string idPrefix,
                             Func<ISmbusBackend, bool> gate, IReadOnlyList<RawChannel> channels)
    {
        Name = name; DriverBackends = driverBackends; IdPrefix = idPrefix;
        Gate = gate; Channels = channels;
    }
}

/// <summary>
/// Chip-family membership from the detected SIO chip id — shared by the channel gates
/// and the mock backend so the family logic exists exactly once. The two Nuvoton
/// families are disjoint under the 0xFFF0 mask, so no chip ever lights both channel
/// sets. (0xFFF0 is enough HERE because 6796/0xD420 and 6798/0xD428 expose identical
/// channels; the kernel's detect keeps the load-bearing 0xFFF8.)
/// </summary>
internal static class ChipFamilies
{
    private static readonly int[] NctEcIds   = { 0xC730, 0xD440, 0xD590 };                            // NCT6683 / 6686 / 6687D
    private static readonly int[] Nct6775Ids = { 0xC560, 0xC800, 0xC910, 0xD120, 0xD350, 0xD420, 0xD450 }; // 6779/6791/6792/6793/6795/6796+6798/6797

    public static bool IsNctEc(int chipId)   => Array.IndexOf(NctEcIds,   chipId & 0xFFF0) >= 0;
    public static bool IsNct6775(int chipId) => Array.IndexOf(Nct6775Ids, chipId & 0xFFF0) >= 0;
}

internal static class ChannelRegistry
{
    /* Super-I/O kind constants (mirror the driver's BROKER_SUPERIO_KIND_*). */
    private const uint Temp = 0, Fan = 1, Voltage = 2;

    /* Chip-family gates. The chipId == 0 fallback keeps an NCT-era driver that predates
       the INFO SuperioChipId field serving the validated NCT6687D channels. */
    private static bool IsNct(ISmbusBackend b)
        => b.SuperioAvailable && (ChipFamilies.IsNctEc(b.SuperioChipId) || b.SuperioChipId == 0);
    private static bool IsNct6775(ISmbusBackend b)
        => b.SuperioAvailable && ChipFamilies.IsNct6775(b.SuperioChipId);

    /// <summary>The registry. Order is serving order (sensor.list is stable across releases).</summary>
    public static readonly IReadOnlyList<ChannelBackendDef> Backends = Build();

    /// <summary>Every raw channel, in registry order — what SensorCatalog assembles from.</summary>
    public static readonly IReadOnlyList<RawChannel> All =
        Backends.SelectMany(d => d.Channels).ToList();

    private static IReadOnlyList<ChannelBackendDef> Build()
    {
        var defs = new List<ChannelBackendDef>();

        /*-- AMD SMU: CPU die temp + per-CCD (only CCDs reporting valid appear). --*/
        {
            var list = new List<RawChannel>();
            list.Add(new RawChannel("smu.cpu.temp", "°C", 2,
                b => b.SmuAvailable,
                b => b.TryReadSmuRaw(0, out uint raw, out SmbusStatus st)
                    ? new RawReading(true, SensorDecode.AmdCpuTctlC(raw), "Ok")
                    : new RawReading(false, 0, st.ToString())));

            for (int ccd = 0; ccd < 8; ccd++)
            {
                int c = ccd;
                list.Add(new RawChannel($"smu.ccd.{c}", "°C", 1,
                    b => b.CcdTempPresent(c),
                    b => b.TryReadSmuRaw((uint)(1 + c), out uint raw, out SmbusStatus st) && (raw & SensorDecode.AmdCcdValid) != 0
                        ? new RawReading(true, SensorDecode.AmdCcdTempC(raw), "Ok")
                        : new RawReading(false, 0, st == SmbusStatus.Ok ? "Invalid" : st.ToString())));
            }

            /* SVI2 voltage telemetry (zenpower). Sensor ids 9/10 = BrokerSmuCoreVoltage/SocVoltage;
               present only on CPU models whose plane addresses the kernel knows (Matisse/Vermeer). */
            list.Add(new RawChannel("smu.cpu.vcore", "V", 3,
                b => b.SmuVoltagePresent,
                b => b.TryReadSmuRaw(9, out uint raw, out SmbusStatus st)
                    ? new RawReading(true, SensorDecode.AmdSviVoltageV(raw), "Ok")
                    : new RawReading(false, 0, st.ToString())));
            list.Add(new RawChannel("smu.soc.voltage", "V", 3,
                b => b.SmuVoltagePresent,
                b => b.TryReadSmuRaw(10, out uint raw, out SmbusStatus st)
                    ? new RawReading(true, SensorDecode.AmdSviVoltageV(raw), "Ok")
                    : new RawReading(false, 0, st.ToString())));

            defs.Add(new ChannelBackendDef("AMD SMU", new[] { "AMD SMU" }, "smu.",
                b => b.SmuAvailable, list));
        }

        /*-- NCT668x EC family (MSI: 6683/6686/6687D). nct6687d.* is the stable key for
             the whole register-identical family. Present only on a Nuvoton EC chip. --*/
        {
            var list = new List<RawChannel>();
            for (int i = 0; i < 6; i++)
            {
                int idx = i;
                list.Add(new RawChannel($"nct6687d.temp.{idx}", "°C", 1, IsNct,
                    b => b.TryReadSuperioRaw(Temp, (uint)idx, out uint raw, out SmbusStatus st)
                        ? new RawReading(true, SensorDecode.NctTempC(raw), "Ok") : new RawReading(false, 0, st.ToString())));
            }
            for (int i = 0; i < 8; i++)
            {
                int idx = i;
                list.Add(new RawChannel($"nct6687d.fan.{idx}", "RPM", 0, IsNct,
                    b => b.TryReadSuperioRaw(Fan, (uint)idx, out uint raw, out SmbusStatus st)
                        ? new RawReading(true, SensorDecode.NctFanRpm(raw), "Ok") : new RawReading(false, 0, st.ToString())));
            }
            for (int i = 0; i < 15; i++)
            {
                int idx = i;
                list.Add(new RawChannel($"nct6687d.volt.{idx}", "V", 3, IsNct,
                    b => b.TryReadSuperioRaw(Voltage, (uint)idx, out uint raw, out SmbusStatus st)
                        ? new RawReading(true, SensorDecode.NctVoltageMv(raw) / 1000.0, "Ok") : new RawReading(false, 0, st.ToString())));
            }

            defs.Add(new ChannelBackendDef("NCT668x EC", new[] { "NCT668x EC" }, "nct6687d.",
                IsNct, list));
        }

        /*-- NCT6775 "classic" family (ASUS/ASRock/Gigabyte/EVGA boards). Present only on a
             modern-group chip (docs/SUPERIO-NCT6775-FAMILY.md). Same {kind,index} IOCTL;
             the kernel's bank-select backend bakes the registers. Temps/fans reuse the NCT
             decode (identical packing); voltages are a single ADC byte at 8 mV/LSB. --*/
        {
            var list = new List<RawChannel>();
            for (int i = 0; i < 6; i++)
            {
                int idx = i;
                list.Add(new RawChannel($"nct6775.temp.{idx}", "°C", 1, IsNct6775,
                    b => b.TryReadSuperioRaw(Temp, (uint)idx, out uint raw, out SmbusStatus st)
                        ? new RawReading(true, SensorDecode.NctTempC(raw), "Ok") : new RawReading(false, 0, st.ToString())));
            }
            for (int i = 0; i < 7; i++)
            {
                int idx = i;
                list.Add(new RawChannel($"nct6775.fan.{idx}", "RPM", 0, IsNct6775,
                    b => b.TryReadSuperioRaw(Fan, (uint)idx, out uint raw, out SmbusStatus st)
                        ? new RawReading(true, SensorDecode.NctFanRpm(raw), "Ok") : new RawReading(false, 0, st.ToString())));
            }
            for (int i = 0; i < 16; i++)
            {
                int idx = i;
                list.Add(new RawChannel($"nct6775.volt.{idx}", "V", 3, IsNct6775,
                    b => b.TryReadSuperioRaw(Voltage, (uint)idx, out uint raw, out SmbusStatus st)
                        ? new RawReading(true, SensorDecode.Nct6775VoltageMv(raw) / 1000.0, "Ok") : new RawReading(false, 0, st.ToString())));
            }

            defs.Add(new ChannelBackendDef("NCT6775", new[] { "NCT6775" }, "nct6775.",
                IsNct6775, list));
        }

        /*-- DIMM thermal sensors (JC42 over SMBus). Present only at populated slots; rides
             whichever SMBus host backend is active. --*/
        {
            var list = new List<RawChannel>();
            for (int i = 0; i < 8; i++)
            {
                int idx = i;
                list.Add(new RawChannel($"dimm.{idx}", "°C", 1,
                    b => b.DimmTempPresent(idx),
                    b => b.TryReadDimmTempRaw(idx, out uint raw, out SmbusStatus st)
                        ? new RawReading(true, SensorDecode.Jc42TempC(raw), "Ok") : new RawReading(false, 0, st.ToString())));
            }

            defs.Add(new ChannelBackendDef("JC42 DIMM temps", new[] { "AMD FCH", "Intel i801" }, "dimm.",
                b => b.Available, list));
        }

        return defs;
    }

    /// <summary>
    /// Registry integrity check (run by --selftest): unique backend names, unique channel
    /// ids, every channel id owned by its entry's prefix, no prefix owned twice, and every
    /// entry declares at least one driver backend it rides. Returns null when clean,
    /// otherwise the first violation found.
    /// </summary>
    public static string? Validate()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ChannelBackendDef def in Backends)
        {
            if (!names.Add(def.Name)) return $"duplicate backend name '{def.Name}'";
            if (string.IsNullOrEmpty(def.IdPrefix) || !prefixes.Add(def.IdPrefix))
                return $"missing/duplicate id prefix '{def.IdPrefix}' ({def.Name})";
            if (def.DriverBackends.Count == 0) return $"'{def.Name}' declares no driver backend";
            if (def.Channels.Count == 0) return $"'{def.Name}' has no channels";

            foreach (RawChannel ch in def.Channels)
            {
                if (!ch.RawId.StartsWith(def.IdPrefix, StringComparison.Ordinal))
                    return $"channel '{ch.RawId}' does not start with its owner's prefix '{def.IdPrefix}'";
                if (!ids.Add(ch.RawId)) return $"duplicate channel id '{ch.RawId}'";
            }
        }

        /* No channel id may match a FOREIGN entry's prefix (prefix = exclusive ownership). */
        foreach (ChannelBackendDef def in Backends)
            foreach (RawChannel ch in def.Channels)
                foreach (ChannelBackendDef other in Backends)
                    if (!ReferenceEquals(def, other) &&
                        ch.RawId.StartsWith(other.IdPrefix, StringComparison.Ordinal))
                        return $"channel '{ch.RawId}' ({def.Name}) also matches prefix '{other.IdPrefix}' ({other.Name})";

        return null;
    }
}
