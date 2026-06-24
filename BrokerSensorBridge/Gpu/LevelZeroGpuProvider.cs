using System.Runtime.InteropServices;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| LevelZeroInterop / IntelLevelZeroGpuProvider — read-only Intel GPU telemetry |
|                                                                            |
|   The Intel counterpart to the AMD/NVIDIA providers, behind the same         |
|   IGpuSensorProvider seam. Uses the oneAPI Level Zero **Sysman** API          |
|   (ze_loader.dll) — the documented Intel GPU telemetry interface — resolved   |
|   at runtime via minimal P/Invoke (no new NuGet dep). Read-only getters only. |
|                                                                            |
|   USER-MODE, reduced assurance (no kernel driver/brick-guard), opt-in         |
|   (AllowGpuSensors), strictly read-only — same posture as the AMD/NVIDIA      |
|   paths.                                                                      |
|                                                                            |
|   Provenance: Level Zero / Sysman is a published, first-party Intel/oneAPI    |
|   C API (ze_api.h / zes_api.h); the entry-point names, enum values, and       |
|   struct layouts are oneAPI facts. ze_loader.dll ships with the Intel         |
|   graphics driver / oneAPI runtime and is loaded at runtime, never            |
|   redistributed.                                                             |
|                                                                            |
|   STATUS: HW-UNVALIDATED — the dev box has no Intel GPU. Built for            |
|   completeness. Every call degrades gracefully: a sensor/domain that does     |
|   not enumerate, or a GetState/GetProperties that returns non-success,        |
|   makes that one metric report not-available (per-metric gating) rather than  |
|   failing the provider. The `stype` constants below (the oneAPI descriptor    |
|   version tags) are the values to double-check first against zes_api.h on a   |
|   real Intel GPU; a wrong tag just hides one metric, it does not crash.       |
|   Covered today: edge + memory temperature, GPU + memory clock, fan RPM.      |
|   Power and utilization are energy/activity COUNTER deltas (stateful) and are |
|   deferred — they report not-available until added.                          |
\*---------------------------------------------------------------------------*/
internal sealed class LevelZeroInterop : IDisposable
{
    private const int ZE_RESULT_SUCCESS = 0;

    /* zes_structure_type_t descriptor tags (zes_api.h). VERIFY on first Intel hardware. */
    private const int ZES_STRUCTURE_TYPE_TEMP_PROPERTIES = 0x14;
    private const int ZES_STRUCTURE_TYPE_FREQ_PROPERTIES = 0x9;
    private const int ZES_STRUCTURE_TYPE_FREQ_STATE      = 0x1c;

    /* zes_temp_sensors_t */
    private const int ZES_TEMP_SENSORS_GLOBAL = 0;
    private const int ZES_TEMP_SENSORS_GPU    = 1;
    private const int ZES_TEMP_SENSORS_MEMORY = 2;

    /* zes_freq_domain_t */
    private const int ZES_FREQ_DOMAIN_GPU    = 0;
    private const int ZES_FREQ_DOMAIN_MEMORY = 1;

    /* zes_fan_speed_units_t */
    private const int ZES_FAN_SPEED_UNITS_RPM = 0;

    private readonly zesTemperatureGetState_t _tempGetState;
    private readonly zesFrequencyGetState_t _freqGetState;
    private readonly zesFanGetState_t _fanGetState;

    private readonly IntPtr _gpuTemp, _memTemp;     // zes_temp_handle_t (IntPtr.Zero if none)
    private readonly IntPtr _gpuFreq, _memFreq;     // zes_freq_handle_t
    private readonly IntPtr _fan;                   // zes_fan_handle_t
    private bool _disposed;

    public string DeviceName => "Intel GPU";

    private LevelZeroInterop(zesTemperatureGetState_t tempGetState, zesFrequencyGetState_t freqGetState,
                             zesFanGetState_t fanGetState, IntPtr gpuTemp, IntPtr memTemp,
                             IntPtr gpuFreq, IntPtr memFreq, IntPtr fan)
    {
        _tempGetState = tempGetState; _freqGetState = freqGetState; _fanGetState = fanGetState;
        _gpuTemp = gpuTemp; _memTemp = memTemp; _gpuFreq = gpuFreq; _memFreq = memFreq; _fan = fan;
    }

    /// <summary>
    /// Enables Sysman, initializes Level Zero, selects the first device, and caches its temperature/
    /// frequency/fan handles classified by type. Returns null with a logged reason when Level Zero is
    /// absent (no Intel GPU / oneAPI runtime), an entry point is missing, or no device is present.
    /// </summary>
    public static LevelZeroInterop? TryCreate(Action<string> log)
    {
        /* Sysman must be enabled BEFORE zeInit. We are the only Level Zero user in this process. */
        Environment.SetEnvironmentVariable("ZES_ENABLE_SYSMAN", "1");

        if (!NativeLib.TryLoadSystem("ze_loader.dll", out IntPtr lib))   // System32 only — hijack-safe
        {
            log("[gpu] ze_loader.dll not present (no Intel GPU / oneAPI runtime) — Intel GPU sensors unavailable.");
            return null;
        }

        if (!TryGet(lib, "zeInit", out zeInit_t? zeInit) ||
            !TryGet(lib, "zeDriverGet", out zeDriverGet_t? driverGet) ||
            !TryGet(lib, "zeDeviceGet", out zeDeviceGet_t? deviceGet) ||
            !TryGet(lib, "zesDeviceEnumTemperatureSensors", out zesDeviceEnumTemperatureSensors_t? enumTemp) ||
            !TryGet(lib, "zesTemperatureGetProperties", out zesTemperatureGetProperties_t? tempProps) ||
            !TryGet(lib, "zesTemperatureGetState", out zesTemperatureGetState_t? tempState) ||
            !TryGet(lib, "zesDeviceEnumFrequencyDomains", out zesDeviceEnumFrequencyDomains_t? enumFreq) ||
            !TryGet(lib, "zesFrequencyGetProperties", out zesFrequencyGetProperties_t? freqProps) ||
            !TryGet(lib, "zesFrequencyGetState", out zesFrequencyGetState_t? freqState) ||
            !TryGet(lib, "zesDeviceEnumFans", out zesDeviceEnumFans_t? enumFans) ||
            !TryGet(lib, "zesFanGetState", out zesFanGetState_t? fanState))
        {
            log("[gpu] ze_loader.dll is missing a required Level Zero Sysman entry point — Intel GPU sensors unavailable.");
            return null;
        }

        try
        {
            if (zeInit!(0) != ZE_RESULT_SUCCESS) { log("[gpu] zeInit failed — Intel GPU sensors unavailable."); return null; }

            IntPtr device = FirstDevice(driverGet!, deviceGet!);
            if (device == IntPtr.Zero) { log("[gpu] Level Zero found no GPU device — Intel GPU sensors unavailable."); return null; }

            /* Classify temperature sensors (GPU/GLOBAL -> edge, MEMORY -> mem). */
            IntPtr gpuTemp = IntPtr.Zero, memTemp = IntPtr.Zero;
            foreach (IntPtr h in Enumerate(c => enumTemp!(device, ref c, null), (a, c) => enumTemp!(device, ref c, a)))
            {
                var props = new zes_temp_properties_t { stype = ZES_STRUCTURE_TYPE_TEMP_PROPERTIES };
                int type = tempProps!(h, ref props) == ZE_RESULT_SUCCESS ? props.type : ZES_TEMP_SENSORS_GLOBAL;
                if (type is ZES_TEMP_SENSORS_GPU or ZES_TEMP_SENSORS_GLOBAL) { if (gpuTemp == IntPtr.Zero) gpuTemp = h; }
                else if (type == ZES_TEMP_SENSORS_MEMORY) { if (memTemp == IntPtr.Zero) memTemp = h; }
            }

            /* Classify frequency domains (GPU core vs memory). */
            IntPtr gpuFreq = IntPtr.Zero, memFreq = IntPtr.Zero;
            foreach (IntPtr h in Enumerate(c => enumFreq!(device, ref c, null), (a, c) => enumFreq!(device, ref c, a)))
            {
                var props = new zes_freq_properties_t { stype = ZES_STRUCTURE_TYPE_FREQ_PROPERTIES };
                int type = freqProps!(h, ref props) == ZE_RESULT_SUCCESS ? props.type : ZES_FREQ_DOMAIN_GPU;
                if (type == ZES_FREQ_DOMAIN_GPU) { if (gpuFreq == IntPtr.Zero) gpuFreq = h; }
                else if (type == ZES_FREQ_DOMAIN_MEMORY) { if (memFreq == IntPtr.Zero) memFreq = h; }
            }

            /* First fan (if any). */
            IntPtr fan = IntPtr.Zero;
            foreach (IntPtr h in Enumerate(c => enumFans!(device, ref c, null), (a, c) => enumFans!(device, ref c, a)))
            { fan = h; break; }

            log("[gpu] Intel GPU: Level Zero Sysman telemetry online (read-only, HW-unvalidated).");
            return new LevelZeroInterop(tempState!, freqState!, fanState!, gpuTemp, memTemp, gpuFreq, memFreq, fan);
        }
        catch (Exception ex)
        {
            log($"[gpu] Level Zero initialization threw ({ex.Message}) — Intel GPU sensors unavailable.");
            return null;
        }
    }

    public bool TryGetEdgeTempC(out double v)   => TryTemp(_gpuTemp, out v);
    public bool TryGetMemTempC(out double v)     => TryTemp(_memTemp, out v);
    public bool TryGetGpuClockMhz(out double v)  => TryFreq(_gpuFreq, out v);
    public bool TryGetMemClockMhz(out double v)  => TryFreq(_memFreq, out v);

    public bool TryGetFanRpm(out double v)
    {
        v = 0;
        if (_disposed || _fan == IntPtr.Zero) return false;
        if (_fanGetState(_fan, ZES_FAN_SPEED_UNITS_RPM, out int rpm) != ZE_RESULT_SUCCESS || rpm < 0) return false;
        v = rpm; return true;
    }

    private bool TryTemp(IntPtr handle, out double v)
    {
        v = 0;
        if (_disposed || handle == IntPtr.Zero) return false;
        if (_tempGetState(handle, out double t) != ZE_RESULT_SUCCESS) return false;
        v = t; return true;
    }

    private bool TryFreq(IntPtr handle, out double v)
    {
        v = 0;
        if (_disposed || handle == IntPtr.Zero) return false;
        var state = new zes_freq_state_t { stype = ZES_STRUCTURE_TYPE_FREQ_STATE };
        if (_freqGetState(handle, ref state) != ZE_RESULT_SUCCESS || state.actual <= 0) return false;
        v = state.actual; return true;   // MHz
    }

    public void Dispose() => _disposed = true;   // Level Zero has no teardown call we own here

    /*-- helpers --*/
    private static IntPtr FirstDevice(zeDriverGet_t driverGet, zeDeviceGet_t deviceGet)
    {
        uint dcount = 0;
        if (driverGet(ref dcount, null) != ZE_RESULT_SUCCESS || dcount == 0) return IntPtr.Zero;
        var drivers = new IntPtr[dcount];
        if (driverGet(ref dcount, drivers) != ZE_RESULT_SUCCESS) return IntPtr.Zero;

        foreach (IntPtr drv in drivers)
        {
            uint vcount = 0;
            if (deviceGet(drv, ref vcount, null) != ZE_RESULT_SUCCESS || vcount == 0) continue;
            var devices = new IntPtr[vcount];
            if (deviceGet(drv, ref vcount, devices) != ZE_RESULT_SUCCESS) continue;
            if (devices.Length > 0 && devices[0] != IntPtr.Zero) return devices[0];
        }
        return IntPtr.Zero;
    }

    /// <summary>Two-call enumerate (count, then fill) → the array of handles, or empty on any error.</summary>
    private static IReadOnlyList<IntPtr> Enumerate(Func<uint, int> getCount, Func<IntPtr[], uint, int> fill)
    {
        uint count = 0;
        if (getCount(count) != ZE_RESULT_SUCCESS || count == 0) return Array.Empty<IntPtr>();
        var arr = new IntPtr[count];
        return fill(arr, count) == ZE_RESULT_SUCCESS ? arr : Array.Empty<IntPtr>();
    }

    private static bool TryGet<T>(IntPtr lib, string name, out T? fn) where T : Delegate
    {
        if (NativeLibrary.TryGetExport(lib, name, out IntPtr p) && p != IntPtr.Zero)
        {
            fn = Marshal.GetDelegateForFunctionPointer<T>(p);
            return true;
        }
        fn = null;
        return false;
    }

    /*-- Level Zero / Sysman signatures (ze_api.h / zes_api.h). ze_result_t (int; 0 == success);
         handles are opaque pointers (IntPtr). x64 ABI is unified, so Cdecl is a no-op label. --*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int zeInit_t(int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int zeDriverGet_t(ref uint count, IntPtr[]? drivers);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int zeDeviceGet_t(IntPtr driver, ref uint count, IntPtr[]? devices);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int zesDeviceEnumTemperatureSensors_t(IntPtr device, ref uint count, IntPtr[]? handles);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int zesTemperatureGetProperties_t(IntPtr temp, ref zes_temp_properties_t props);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int zesTemperatureGetState_t(IntPtr temp, out double temperature);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int zesDeviceEnumFrequencyDomains_t(IntPtr device, ref uint count, IntPtr[]? handles);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int zesFrequencyGetProperties_t(IntPtr freq, ref zes_freq_properties_t props);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int zesFrequencyGetState_t(IntPtr freq, ref zes_freq_state_t state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int zesDeviceEnumFans_t(IntPtr device, ref uint count, IntPtr[]? handles);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int zesFanGetState_t(IntPtr fan, int units, out int speed);

    /* Only the leading fields up to the one we read are load-bearing; the descriptor tag (stype) must
       be set by the caller. Pack=8 matches the C ABI on x64 (int tag, padded, then the void* pNext). */
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct zes_temp_properties_t
    {
        public int stype; public IntPtr pNext;
        public int type;                 // zes_temp_sensors_t
        public byte onSubdevice; public uint subdeviceId;
        public double maxTemperature;
        public byte isCriticalTempSupported, isThreshold1Supported, isThreshold2Supported;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct zes_freq_properties_t
    {
        public int stype; public IntPtr pNext;
        public int type;                 // zes_freq_domain_t
        public byte onSubdevice; public uint subdeviceId;
        public byte canControl, isThrottleEventSupported;
        public double min, max;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct zes_freq_state_t
    {
        public int stype; public IntPtr pNext;
        public double currentVoltage, request, tdp, efficient, actual;
        public uint throttleReasons;
    }
}

/// <summary>Intel GPU telemetry via Level Zero Sysman, behind the common seam. HW-unvalidated.</summary>
internal sealed class IntelLevelZeroGpuProvider : IGpuSensorProvider, IDisposable
{
    private readonly LevelZeroInterop _ze;
    private readonly object _lock = new();
    private bool _disposed;

    public string Name { get; }
    public bool IsAvailable => !_disposed;

    private IntelLevelZeroGpuProvider(LevelZeroInterop ze) { _ze = ze; Name = ze.DeviceName; }

    public static IntelLevelZeroGpuProvider? TryCreate(Action<string> log)
    {
        LevelZeroInterop? ze = LevelZeroInterop.TryCreate(log);
        return ze == null ? null : new IntelLevelZeroGpuProvider(ze);
    }

    public bool TryRead(GpuMetric metric, out double value)
    {
        value = 0;
        if (_disposed) return false;
        lock (_lock)
        {
            return metric switch
            {
                GpuMetric.TempEdge    => _ze.TryGetEdgeTempC(out value),
                GpuMetric.TempMem     => _ze.TryGetMemTempC(out value),
                GpuMetric.FanRpm      => _ze.TryGetFanRpm(out value),
                GpuMetric.ClockGfxMhz => _ze.TryGetGpuClockMhz(out value),
                GpuMetric.ClockMemMhz => _ze.TryGetMemClockMhz(out value),
                /* Hot-spot, fan %, power (energy-counter delta) and utilization (activity-counter
                   delta) are not provided in this read-only single-shot pass — not-available. */
                _ => false
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ze.Dispose();
    }
}
