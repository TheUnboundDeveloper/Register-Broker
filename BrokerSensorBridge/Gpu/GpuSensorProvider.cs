namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| GpuSensorProvider — read-only GPU telemetry source (user-mode, opt-in)       |
|                                                                            |
|   GPU thermals are not a motherboard-SMBus device: a discrete GPU keeps its  |
|   sensors on-package, reported through the vendor's user-mode API. So unlike  |
|   the CPU/board sensors (kernel SMBus/SMU/Super-I/O, brick-guarded), GPU      |
|   sensors are served by a USER-MODE provider — the same reduced-assurance     |
|   posture as the USB-HID RGB transport. There is NO kernel driver and NO      |
|   write path: every value is a getter on the vendor library.                 |
|                                                                            |
|   The provider is a process-wide singleton (GpuSensorProvider.Current), set   |
|   at startup only when AllowGpuSensors. When it is null/unavailable the gpu.* |
|   channels report "not available" and are never served — identical           |
|   inert-when-absent behaviour to every other backend. The AMD impl uses ADL   |
|   PMLog (AdlInterop); NVIDIA/Intel would be a second IGpuSensorProvider       |
|   selected in TryCreate, with nothing else changing.                         |
\*---------------------------------------------------------------------------*/

/// <summary>The read-only GPU metrics the catalog can expose. Each maps to one vendor sensor.</summary>
internal enum GpuMetric
{
    TempEdge,       // GPU core/edge temperature (°C)
    TempHotspot,    // junction / hot-spot temperature (°C)
    TempMem,        // memory (VRAM) temperature (°C)
    FanRpm,         // fan tachometer (RPM)
    FanPercent,     // fan duty (%)
    PowerW,         // total board/ASIC power (W)
    ClockGfxMhz,    // graphics (core) clock (MHz)
    ClockMemMhz,    // memory clock (MHz)
    UtilGfx,        // graphics activity / utilization (%)
    UtilMem,        // memory-controller activity / utilization (%)
    MemUsedMb,      // dedicated VRAM in use (MB)
    VoltageGfx      // graphics core (GFX) voltage (V)
}

internal interface IGpuSensorProvider
{
    /// <summary>Human-readable adapter name (e.g. "AMD Radeon RX 7900 XTX"), for logs/labels.</summary>
    string Name { get; }
    /// <summary>True when a GPU answered and the telemetry path is usable.</summary>
    bool IsAvailable { get; }
    /// <summary>Reads one metric. Returns false when the GPU/driver does not expose that sensor.</summary>
    bool TryRead(GpuMetric metric, out double value);
}

/// <summary>
/// Process-wide GPU sensor source. Stays null until the sensor service opts in (AllowGpuSensors)
/// and a supported GPU is found; the gpu.* channels read it lazily at request time.
/// </summary>
internal static class GpuSensorProvider
{
    /// <summary>The active provider, or null when GPU sensors are off or no GPU was detected.</summary>
    public static IGpuSensorProvider? Current { get; set; }

    /// <summary>
    /// Probes for a supported GPU and returns the first provider that answers, or null (with logged
    /// reasons) when none is present. Order: AMD ADL, NVIDIA NVML, Intel Level Zero. Each backend is
    /// independent behind IGpuSensorProvider. (One GPU is served today; multi-GPU/mixed-vendor is a
    /// future extension.)
    /// </summary>
    public static IGpuSensorProvider? TryCreate(Action<string> log)
        => (IGpuSensorProvider?)AmdAdlGpuProvider.TryCreate(log)
        ?? (IGpuSensorProvider?)NvidiaNvmlGpuProvider.TryCreate(log)
        ?? IntelLevelZeroGpuProvider.TryCreate(log);
}

/*---------------------------------------------------------------------------*\
| AmdAdlGpuProvider — AMD (Radeon) telemetry via ADL PMLog                      |
\*---------------------------------------------------------------------------*/
internal sealed class AmdAdlGpuProvider : IGpuSensorProvider, IDisposable
{
    /* PMLOG sensor-type indices into ADLPMLogDataOutput.sensors[] (AMD ADL SDK
       ADL_PMLOG_SENSORTYPE, adl_structures.h; cross-checked against LibreHardwareMonitor's
       current ADLSensorType enum). Read-only telemetry indices only. Anchor-verified on an
       RX 7900 XTX (RDNA3): index 41 = BUS_LANES read 16 (PCIe x16) pins the numbering, and
       the temps decode edge < hotspot < mem as expected. A sensor the driver does not
       populate reports supported==0 and the channel stays "not available" (e.g. ASIC_POWER
       is absent on the current RDNA3 driver's PMLog set — we map it, never guess another). */
    private const int PMLOG_CLK_GFXCLK          = 1;
    private const int PMLOG_CLK_MEMCLK          = 2;
    private const int PMLOG_TEMPERATURE_EDGE    = 8;
    private const int PMLOG_TEMPERATURE_MEM     = 9;
    private const int PMLOG_FAN_RPM             = 14;
    private const int PMLOG_FAN_PERCENTAGE      = 15;
    private const int PMLOG_INFO_ACTIVITY_GFX   = 19;
    private const int PMLOG_INFO_ACTIVITY_MEM   = 20;   // ADL_PMLOG_INFO_ACTIVITY_MEM — memory-controller load (%)
    private const int PMLOG_GFX_VOLTAGE         = 21;   // ADL_PMLOG_GFX_VOLTAGE — reported in mV
    private const int PMLOG_ASIC_POWER          = 23;
    private const int PMLOG_TEMPERATURE_HOTSPOT = 27;
    /* Total Board Power (W). Anchored on the RX 7900 XTX (RDNA3): idx 73 read 77 W at idle, matching
       a labeled reference's "Total Board Power (TBP)" 78 W, while ASIC_POWER (23) is unpopulated on
       this driver. PowerW prefers board power and falls back to ASIC where 73 is absent — so the
       channel reports real power on RDNA3 instead of "not available". */
    private const int PMLOG_BOARD_POWER         = 73;

    private const long CacheTtlMs = 250;   // one PMLog query serves a whole sensor.readall burst

    private readonly AdlInterop _adl;
    private readonly object _lock = new();
    private readonly int[] _supported = new int[256];
    private readonly int[] _value = new int[256];
    private long _lastSampleTick = long.MinValue;
    private bool _haveSample;
    private bool _disposed;

    public string Name { get; }
    public bool IsAvailable => !_disposed;

    private AmdAdlGpuProvider(AdlInterop adl)
    {
        _adl = adl;
        Name = adl.AdapterName;
    }

    public static AmdAdlGpuProvider? TryCreate(Action<string> log)
    {
        AdlInterop? adl = AdlInterop.TryCreate(log);
        if (adl == null) return null;

        var provider = new AmdAdlGpuProvider(adl);
        /* Prime the cache once so a missing PMLog path is caught at startup, not first read. */
        if (!provider.Refresh())
        {
            log($"[gpu] {adl.AdapterName}: ADL present but PMLog telemetry is unavailable on this GPU/driver.");
            provider.Dispose();
            return null;
        }
        log($"[gpu] {adl.AdapterName}: ADL PMLog telemetry online ({provider.SupportedCount()} sensors).");
        log($"[gpu] PMLOG supported index=value: {provider.DumpSupported()}");
        return provider;
    }

    public bool TryRead(GpuMetric metric, out double value)
    {
        value = 0;
        if (_disposed) return false;

        /* VRAM usage is a direct ADL call, not part of the cached PMLog block. */
        if (metric == GpuMetric.MemUsedMb)
        {
            lock (_lock)
            {
                if (_adl.TryGetVramUsageMb(out int mb)) { value = mb; return true; }
                return false;
            }
        }

        int index = metric switch
        {
            GpuMetric.TempEdge    => PMLOG_TEMPERATURE_EDGE,
            GpuMetric.TempHotspot => PMLOG_TEMPERATURE_HOTSPOT,
            GpuMetric.TempMem     => PMLOG_TEMPERATURE_MEM,
            GpuMetric.FanRpm      => PMLOG_FAN_RPM,
            GpuMetric.FanPercent  => PMLOG_FAN_PERCENTAGE,
            GpuMetric.ClockGfxMhz => PMLOG_CLK_GFXCLK,
            GpuMetric.ClockMemMhz => PMLOG_CLK_MEMCLK,
            GpuMetric.UtilGfx     => PMLOG_INFO_ACTIVITY_GFX,
            GpuMetric.UtilMem     => PMLOG_INFO_ACTIVITY_MEM,
            GpuMetric.VoltageGfx  => PMLOG_GFX_VOLTAGE,
            _ => -1
        };

        lock (_lock)
        {
            EnsureFresh();
            if (!_haveSample) return false;

            /* GPU power: prefer Total Board Power, fall back to ASIC power where the board sensor
               is absent. Resolved against the live sample so we pick whichever the driver populates. */
            if (metric == GpuMetric.PowerW)
            {
                if (_supported[PMLOG_BOARD_POWER] != 0)      index = PMLOG_BOARD_POWER;
                else if (_supported[PMLOG_ASIC_POWER] != 0)  index = PMLOG_ASIC_POWER;
                else return false;
            }
            if (index < 0 || _supported[index] == 0) return false;

            value = _value[index];
            if (metric == GpuMetric.VoltageGfx) value /= 1000.0;   // ADL reports GFX voltage in mV → V
            return true;
        }
    }

    /// <summary>Re-query PMLog if the cached sample is older than the TTL. Caller holds _lock.</summary>
    private void EnsureFresh()
    {
        long now = Environment.TickCount64;
        if (_haveSample && now - _lastSampleTick < CacheTtlMs) return;
        Refresh();
    }

    /// <summary>Take a fresh PMLog sample. Returns true on success; thread-safe (takes _lock).</summary>
    private bool Refresh()
    {
        lock (_lock)
        {
            if (_disposed) return false;
            bool ok = _adl.TryQueryPmLog(_supported, _value);
            if (ok) { _haveSample = true; _lastSampleTick = Environment.TickCount64; }
            return ok;
        }
    }

    private int SupportedCount()
    {
        int n = 0;
        for (int i = 0; i < _supported.Length; i++) if (_supported[i] != 0) n++;
        return n;
    }

    private string DumpSupported()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _supported.Length; i++)
            if (_supported[i] != 0) sb.Append(i).Append('=').Append(_value[i]).Append(' ');
        return sb.ToString().TrimEnd();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _adl.Dispose();
    }
}

/// <summary>
/// A fixed in-memory GPU provider for --selftest: reports a supplied value for the metrics in the
/// map and "not available" for everything else, so the gpu.* gates/decoders can be verified
/// without a real GPU. Never used in production.
/// </summary>
internal sealed class FixedGpuProvider : IGpuSensorProvider
{
    private readonly IReadOnlyDictionary<GpuMetric, double> _values;
    public string Name { get; }
    public bool IsAvailable => true;

    public FixedGpuProvider(string name, IReadOnlyDictionary<GpuMetric, double> values)
    {
        Name = name; _values = values;
    }

    public bool TryRead(GpuMetric metric, out double value) => _values.TryGetValue(metric, out value);
}
