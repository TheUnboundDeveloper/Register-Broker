namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| UpsSensorProvider — read-only UPS telemetry (user-mode, opt-in, removable)   |
|                                                                            |
|   A UPS is a standard USB HID Power Device (top-level collection usage      |
|   page 0x84): it reports battery/line status through FEATURE reports keyed  |
|   by HID usages, not a vendor protocol at fixed offsets. So this is the      |
|   SAME user-mode, no-driver, read-only, reduced-assurance posture as the     |
|   Aquacomputer provider, and likewise REMOVABLE — a background poller        |
|   (re)opens the device and polls GetFeature, with a staleness window so one  |
|   missed sample never flaps the sensors and a sustained loss marks the group |
|   absent and keeps retrying for re-plug. The ups.* channels are flagged      |
|   Removable. Opt-in (AllowUpsSensors) — see SECURITY.md.                     |
|                                                                            |
|   Identified by HID class (usage page 0x84), NOT a vendor id, so any         |
|   compliant UPS works. The usage map is discovered from the device's value  |
|   caps at init (HidDevice.GetValueCaps), so report ids / link collections    |
|   are not hard-coded.                                                       |
|                                                                            |
|   Provenance: the usages are USB-IF facts — "Universal Serial Bus Usage      |
|   Tables for HID Power Devices" (Power Device page 0x84, Battery System      |
|   page 0x85): RemainingCapacity 0x85/0x66, RunTimeToEmpty 0x85/0x68 (s),     |
|   PercentLoad 0x84/0x35, Voltage 0x84/0x30. Values are read as the RAW       |
|   logical value: the HID UnitExponent on consumer UPS descriptors is         |
|   routinely garbage, so (like Network UPS Tools) we do not apply it.          |
\*---------------------------------------------------------------------------*/

/// <summary>The read-only UPS metrics the catalog exposes. The integer order is the slot index into
/// the provider's snapshot arrays — do not reorder.</summary>
internal enum UpsMetric
{
    ChargePercent = 0,   // battery remaining capacity (%)
    RuntimeMin    = 1,   // estimated runtime on battery (minutes)
    LoadPercent   = 2,   // output load (% of capacity)
    VoltageIn     = 3,   // input (mains) voltage (V)
    VoltageOut    = 4    // output voltage (V)
}

internal interface IUpsSensorProvider
{
    /// <summary>Human-readable device name (e.g. "UPS (HID Power Device)"), for logs/labels.</summary>
    string Name { get; }
    /// <summary>True when the UPS is currently present and a fresh sample exists; goes false (after the
    /// staleness window) when it is unplugged/stops responding.</summary>
    bool IsAvailable { get; }
    /// <summary>Reads one metric. Returns false when stale/absent or the device does not expose it.</summary>
    bool TryRead(UpsMetric metric, out double value);
}

/// <summary>
/// Process-wide UPS sensor source. Stays null until the sensor service opts in (AllowUpsSensors) and a
/// HID Power Device is found at startup; the ups.* channels read it lazily. Survives hot-unplug/re-plug.
/// </summary>
internal static class UpsSensorProvider
{
    /// <summary>The active provider, or null when UPS sensors are off or no device was detected.</summary>
    public static IUpsSensorProvider? Current { get; set; }

    /// <summary>Probes for a HID Power Device and returns a provider, or null (logged) when none present.</summary>
    public static IUpsSensorProvider? TryCreate(Action<string> log)
        => HidPowerDeviceProvider.TryCreate(log);
}

/*---------------------------------------------------------------------------*\
| HidPowerDeviceProvider — the live UPS source (background GetFeature poller)   |
\*---------------------------------------------------------------------------*/
internal sealed class HidPowerDeviceProvider : IUpsSensorProvider, IDisposable
{
    private const ushort PagePowerDevice = 0x84;
    private const ushort PageBatterySystem = 0x85;
    private const ushort UsageVoltage = 0x30;          // Power Device: Voltage
    private const ushort UsagePercentLoad = 0x35;      // Power Device: PercentLoad
    private const ushort UsageRemainingCapacity = 0x66;// Battery System: RemainingCapacity (%)
    private const ushort UsageRunTimeToEmpty = 0x68;   // Battery System: RunTimeToEmpty (seconds)

    private const int SlotCount = 5;                   // == number of UpsMetric values
    private const long StaleMs = 8000;                 // a UPS polls slowly; generous staleness window
    private const int PollIntervalMs = 2000;
    private const int ReopenBackoffMs = 4000;
    private const int MaxFailuresBeforeReopen = 3;

    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly double[] _vals = new double[SlotCount];
    private readonly bool[] _ok = new bool[SlotCount];
    private long _lastTick = long.MinValue;
    private bool _haveSample;

    private readonly Thread _poller;
    private volatile bool _stop;
    private volatile bool _disposed;
    private HidDevice? _dev;

    /* Resolved usage refs (report id + link collection) discovered from the device's value caps. */
    private UsageRef[] _refs = Array.Empty<UsageRef>();

    public string Name { get; private set; } = "UPS (HID Power Device)";

    public bool IsAvailable
    {
        get { lock (_lock) return !_disposed && _haveSample && (Environment.TickCount64 - _lastTick) <= StaleMs; }
    }

    private HidPowerDeviceProvider(HidDevice dev, UsageRef[] refs, string name, Action<string> log)
    {
        _dev = dev; _refs = refs; Name = name; _log = log;
        _poller = new Thread(PollLoop) { IsBackground = true, Name = "ups-hid-poll" };
    }

    public static HidPowerDeviceProvider? TryCreate(Action<string> log)
    {
        if (!TryOpen(log, out HidDevice? dev, out UsageRef[] refs, out string name) || dev is null)
        {
            log("[ups] no HID Power Device (UPS) present — ups.* stay absent.");
            return null;
        }

        var p = new HidPowerDeviceProvider(dev, refs, name, log);
        p._poller.Start();
        if (p.WaitForFirstSample(2500))
            log($"[ups] {name} online — read-only, removable, reduced assurance.");
        else
            log($"[ups] {name} opened but no sample yet (will keep polling in the background).");
        return p;
    }

    /// <summary>Find a HID Power Device and resolve the usage map. Returns false when no compliant
    /// device exposes at least the battery-capacity usage (so a non-UPS 0x84 device is rejected).</summary>
    private static bool TryOpen(Action<string> log, out HidDevice? device, out UsageRef[] refs, out string name)
    {
        device = null; refs = Array.Empty<UsageRef>(); name = "UPS (HID Power Device)";
        IReadOnlyList<HidDevice> devs = HidDevice.OpenByUsagePage(PagePowerDevice, log);
        try
        {
            foreach (HidDevice d in devs)
            {
                UsageRef[] map = ResolveUsages(d);
                if (map.Length == 0) continue;                 // not a readable UPS — keep looking
                device = d;
                refs = map;
                name = $"UPS (HID Power Device {d.VendorId:X4}:{d.ProductId:X4})";
                return true;
            }
        }
        finally
        {
            foreach (HidDevice d in devs) if (!ReferenceEquals(d, device)) d.Dispose();
        }
        return false;
    }

    /// <summary>Map each UpsMetric to a (report id, link collection) from the device's feature value
    /// caps. The two input/output voltages are the 0x84/0x30 caps under flow sub-collections (link
    /// collection >= 2), in ascending order. Requires at least RemainingCapacity to qualify.</summary>
    private static UsageRef[] ResolveUsages(HidDevice d)
    {
        IReadOnlyList<HidDevice.HidValueCap> caps = d.GetValueCaps(feature: true);
        if (caps.Count == 0) return Array.Empty<UsageRef>();

        var slots = new UsageRef[SlotCount];

        HidDevice.HidValueCap? Find(ushort page, ushort usage)
        {
            foreach (var c in caps) if (c.UsagePage == page && c.Usage == usage) return c;
            return null;
        }

        var charge  = Find(PageBatterySystem, UsageRemainingCapacity);
        var runtime = Find(PageBatterySystem, UsageRunTimeToEmpty);
        var load    = Find(PagePowerDevice, UsagePercentLoad);

        if (charge is null) return Array.Empty<UsageRef>();   // a UPS must report battery capacity
        slots[(int)UpsMetric.ChargePercent] = UsageRef.From(charge.Value, 1.0);
        if (runtime is { } rt) slots[(int)UpsMetric.RuntimeMin] = UsageRef.From(rt, 1.0 / 60.0); // s -> min
        if (load is { } ld)    slots[(int)UpsMetric.LoadPercent] = UsageRef.From(ld, 1.0);

        /* Voltages: 0x84/0x30 under flow collections (skip the device-root collection's config copy). */
        var volts = new List<HidDevice.HidValueCap>();
        foreach (var c in caps)
            if (c.UsagePage == PagePowerDevice && c.Usage == UsageVoltage && c.LinkCollection >= 2)
                volts.Add(c);
        volts.Sort((a, b) => a.LinkCollection.CompareTo(b.LinkCollection));
        if (volts.Count > 0) slots[(int)UpsMetric.VoltageIn]  = UsageRef.From(volts[0], 1.0);
        if (volts.Count > 1) slots[(int)UpsMetric.VoltageOut] = UsageRef.From(volts[1], 1.0);

        return slots;
    }

    private bool WaitForFirstSample(int ms)
    {
        long deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            if (IsAvailable) return true;
            Thread.Sleep(50);
        }
        return IsAvailable;
    }

    private void PollLoop()
    {
        int failures = 0;
        while (!_stop)
        {
            HidDevice? dev = _dev;
            UsageRef[] refs = _refs;
            if (dev == null)
            {
                if (!TryOpen(_log, out dev, out refs, out string name) || dev is null)
                {
                    Thread.Sleep(ReopenBackoffMs);
                    continue;
                }
                _dev = dev; _refs = refs; Name = name; failures = 0;
                _log("[ups] UPS (re)connected.");
            }

            try
            {
                double[] lv = new double[SlotCount];
                bool[] lo = new bool[SlotCount];
                if (ReadSample(dev, refs, lv, lo))
                {
                    lock (_lock)
                    {
                        Array.Copy(lv, _vals, SlotCount);
                        Array.Copy(lo, _ok, SlotCount);
                        _haveSample = true;
                        _lastTick = Environment.TickCount64;
                    }
                    failures = 0;
                }
                else if (++failures >= MaxFailuresBeforeReopen)
                {
                    _log("[ups] UPS stopped responding — closing; will retry for re-plug.");
                    dev.Dispose(); _dev = null; failures = 0;
                }
            }
            catch (Exception ex)
            {
                _log($"[ups] poll error: {ex.Message}");
                try { dev.Dispose(); } catch { /* already gone */ }
                _dev = null; failures = 0;
            }

            Thread.Sleep(PollIntervalMs);
        }
    }

    /// <summary>Fetch each distinct feature report once, extract every mapped usage from it.</summary>
    private static bool ReadSample(HidDevice dev, UsageRef[] refs, double[] vals, bool[] ok)
    {
        var reports = new Dictionary<byte, byte[]?>();
        bool any = false;
        for (int i = 0; i < refs.Length; i++)
        {
            UsageRef r = refs[i];
            if (!r.Valid) { ok[i] = false; continue; }

            if (!reports.TryGetValue(r.ReportId, out byte[]? rep))
            {
                rep = new byte[Math.Max(dev.FeatureReportByteLength, 2)];
                rep[0] = r.ReportId;
                if (!dev.GetFeature(rep)) rep = null;
                reports[r.ReportId] = rep;
            }
            if (rep is null) { ok[i] = false; continue; }

            if (dev.TryGetUsageValue(rep, r.Page, r.LinkCollection, r.Usage, out uint raw) && raw != 0xFFFF)
            {
                vals[i] = raw * r.Scale;
                ok[i] = true;
                any = true;
            }
            else ok[i] = false;
        }
        return any;
    }

    public bool TryRead(UpsMetric metric, out double value)
    {
        value = 0;
        int i = (int)metric;
        lock (_lock)
        {
            if (_disposed || !_haveSample) return false;
            if (Environment.TickCount64 - _lastTick > StaleMs) return false;
            if (i < 0 || i >= SlotCount || !_ok[i]) return false;
            value = _vals[i];
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop = true;
        try { _dev?.Dispose(); } catch { /* already gone */ }
        if (_poller.IsAlive) _poller.Join(1500);
        _dev = null;
    }

    /// <summary>A resolved usage: which (page,usage) at which report id / link collection, and the
    /// scale applied to the raw logical value.</summary>
    private readonly struct UsageRef
    {
        public readonly bool Valid;
        public readonly byte ReportId;
        public readonly ushort Page;
        public readonly ushort Usage;
        public readonly ushort LinkCollection;
        public readonly double Scale;
        private UsageRef(byte rid, ushort page, ushort usage, ushort lc, double scale)
        { Valid = true; ReportId = rid; Page = page; Usage = usage; LinkCollection = lc; Scale = scale; }
        public static UsageRef From(HidDevice.HidValueCap c, double scale)
            => new(c.ReportId, c.UsagePage, c.Usage, c.LinkCollection, scale);
    }
}

/// <summary>
/// A fixed in-memory UPS provider for --selftest: reports a supplied value for the metrics in the map
/// and "not available" for everything else, so the ups.* gates/decoders can be verified without real
/// hardware. Never used in production.
/// </summary>
internal sealed class FixedUpsProvider : IUpsSensorProvider
{
    private readonly IReadOnlyDictionary<UpsMetric, double> _values;
    public string Name { get; }
    public bool IsAvailable => true;

    public FixedUpsProvider(string name, IReadOnlyDictionary<UpsMetric, double> values)
    { Name = name; _values = values; }

    public bool TryRead(UpsMetric metric, out double value) => _values.TryGetValue(metric, out value);
}
