using System.Runtime.InteropServices;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| NvmlInterop / NvidiaNvmlGpuProvider — read-only NVIDIA GPU telemetry         |
|                                                                            |
|   The NVIDIA counterpart to the AMD ADL provider, behind the same           |
|   IGpuSensorProvider seam. Uses NVML (the NVIDIA Management Library,         |
|   nvml.dll — the same interface `nvidia-smi` is built on), resolved at       |
|   runtime via minimal P/Invoke (no new NuGet dep). Read-only getters only;   |
|   no clock/power/fan SET entry point is resolved.                            |
|                                                                            |
|   USER-MODE, reduced assurance (no kernel driver, no brick-guard), opt-in    |
|   (AllowGpuSensors), strictly read-only — same posture as the AMD path.      |
|                                                                            |
|   Provenance: NVML is a published, first-party NVIDIA C API (nvml.h); the    |
|   entry-point names, enum values, and the nvmlUtilization struct are NVML    |
|   facts. nvml.dll ships with the NVIDIA driver and is loaded at runtime,     |
|   never redistributed.                                                       |
|                                                                            |
|   NOTE: HW-UNVALIDATED — the dev box has no NVIDIA GPU. Built for            |
|   completeness; the mappings (temp/fan%/power/clocks/utilization) are the    |
|   stable public NVML getters. Metrics NVML's base API does not expose        |
|   (hot-spot, memory temp, fan RPM) report not-available via per-metric       |
|   gating, exactly like an ADL sensor a driver does not populate.             |
\*---------------------------------------------------------------------------*/
internal sealed class NvmlInterop : IDisposable
{
    private const int NVML_SUCCESS = 0;
    private const int NVML_TEMPERATURE_GPU = 0;
    private const int NVML_CLOCK_GRAPHICS = 0;
    private const int NVML_CLOCK_MEM = 2;
    private const int NameBufferSize = 96;     // NVML_DEVICE_NAME_V2_BUFFER_SIZE

    private readonly nvmlShutdown_t _shutdown;
    private readonly nvmlDeviceGetTemperature_t _getTemp;
    private readonly nvmlDeviceGetFanSpeed_t _getFan;
    private readonly nvmlDeviceGetPowerUsage_t _getPower;
    private readonly nvmlDeviceGetClockInfo_t _getClock;
    private readonly nvmlDeviceGetUtilizationRates_t _getUtil;
    private readonly IntPtr _device;
    private bool _disposed;

    /// <summary>The GPU's marketing name (e.g. "NVIDIA GeForce RTX 4080").</summary>
    public string DeviceName { get; }

    private NvmlInterop(IntPtr device, string name, nvmlShutdown_t shutdown,
                        nvmlDeviceGetTemperature_t getTemp, nvmlDeviceGetFanSpeed_t getFan,
                        nvmlDeviceGetPowerUsage_t getPower, nvmlDeviceGetClockInfo_t getClock,
                        nvmlDeviceGetUtilizationRates_t getUtil)
    {
        _device = device; DeviceName = name; _shutdown = shutdown;
        _getTemp = getTemp; _getFan = getFan; _getPower = getPower;
        _getClock = getClock; _getUtil = getUtil;
    }

    /// <summary>
    /// Loads nvml.dll, initializes NVML, and selects GPU index 0. Returns null with a logged reason
    /// when NVML is absent (no NVIDIA driver), an entry point is missing, or no GPU is present.
    /// </summary>
    public static NvmlInterop? TryCreate(Action<string> log)
    {
        if (!TryLoadNvml(out IntPtr lib))
        {
            log("[gpu] nvml.dll not present (no NVIDIA driver installed) — NVIDIA GPU sensors unavailable.");
            return null;
        }

        if (!TryGet(lib, "nvmlInit_v2", out nvmlInit_t? init) ||
            !TryGet(lib, "nvmlShutdown", out nvmlShutdown_t? shutdown) ||
            !TryGet(lib, "nvmlDeviceGetCount_v2", out nvmlDeviceGetCount_t? getCount) ||
            !TryGet(lib, "nvmlDeviceGetHandleByIndex_v2", out nvmlDeviceGetHandleByIndex_t? getHandle) ||
            !TryGet(lib, "nvmlDeviceGetName", out nvmlDeviceGetName_t? getName) ||
            !TryGet(lib, "nvmlDeviceGetTemperature", out nvmlDeviceGetTemperature_t? getTemp) ||
            !TryGet(lib, "nvmlDeviceGetFanSpeed", out nvmlDeviceGetFanSpeed_t? getFan) ||
            !TryGet(lib, "nvmlDeviceGetPowerUsage", out nvmlDeviceGetPowerUsage_t? getPower) ||
            !TryGet(lib, "nvmlDeviceGetClockInfo", out nvmlDeviceGetClockInfo_t? getClock) ||
            !TryGet(lib, "nvmlDeviceGetUtilizationRates", out nvmlDeviceGetUtilizationRates_t? getUtil))
        {
            log("[gpu] nvml.dll is missing a required NVML entry point — NVIDIA GPU sensors unavailable.");
            return null;
        }

        if (init!() != NVML_SUCCESS)
        {
            log("[gpu] nvmlInit failed — NVIDIA GPU sensors unavailable.");
            return null;
        }

        try
        {
            if (getCount!(out uint count) != NVML_SUCCESS || count == 0)
            {
                log("[gpu] NVML reports no GPUs — NVIDIA GPU sensors unavailable.");
                shutdown!();
                return null;
            }

            if (getHandle!(0, out IntPtr device) != NVML_SUCCESS || device == IntPtr.Zero)
            {
                log("[gpu] nvmlDeviceGetHandleByIndex failed — NVIDIA GPU sensors unavailable.");
                shutdown!();
                return null;
            }

            var nameBuf = new byte[NameBufferSize];
            string name = "NVIDIA GPU";
            if (getName!(device, nameBuf, NameBufferSize) == NVML_SUCCESS)
            {
                int len = Array.IndexOf(nameBuf, (byte)0);
                if (len < 0) len = nameBuf.Length;
                string parsed = System.Text.Encoding.ASCII.GetString(nameBuf, 0, len).Trim();
                if (parsed.Length > 0) name = parsed;
            }

            return new NvmlInterop(device, name, shutdown!, getTemp!, getFan!, getPower!, getClock!, getUtil!);
        }
        catch (Exception ex)
        {
            log($"[gpu] NVML initialization threw ({ex.Message}) — NVIDIA GPU sensors unavailable.");
            try { shutdown!(); } catch { /* best effort */ }
            return null;
        }
    }

    public bool TryGetTemperatureC(out double value)
    {
        value = 0;
        if (_disposed) return false;
        if (_getTemp(_device, NVML_TEMPERATURE_GPU, out uint t) != NVML_SUCCESS) return false;
        value = t; return true;
    }

    public bool TryGetFanPercent(out double value)
    {
        value = 0;
        if (_disposed) return false;
        if (_getFan(_device, out uint pct) != NVML_SUCCESS) return false;
        value = pct; return true;
    }

    public bool TryGetPowerWatts(out double value)
    {
        value = 0;
        if (_disposed) return false;
        if (_getPower(_device, out uint mw) != NVML_SUCCESS) return false;
        value = mw / 1000.0; return true;
    }

    public bool TryGetClockMhz(bool memory, out double value)
    {
        value = 0;
        if (_disposed) return false;
        if (_getClock(_device, memory ? NVML_CLOCK_MEM : NVML_CLOCK_GRAPHICS, out uint mhz) != NVML_SUCCESS) return false;
        value = mhz; return true;
    }

    public bool TryGetUtilizationGfx(out double value)
    {
        value = 0;
        if (_disposed) return false;
        if (_getUtil(_device, out NvmlUtilization util) != NVML_SUCCESS) return false;
        value = util.Gpu; return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _shutdown(); } catch { /* best effort on shutdown */ }
    }

    private static bool TryLoadNvml(out IntPtr lib)
    {
        if (NativeLibrary.TryLoad("nvml.dll", out lib)) return true;
        /* Older drivers place it under Program Files\NVIDIA Corporation\NVSMI. */
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string alt = System.IO.Path.Combine(pf, "NVIDIA Corporation", "NVSMI", "nvml.dll");
        return NativeLibrary.TryLoad(alt, out lib);
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

    /*-- NVML entry-point signatures (nvml.h). Every call returns nvmlReturn_t (int; 0 == success).
         nvmlDevice_t is an opaque handle (IntPtr). x64 ABI is unified, so Cdecl is a no-op label. --*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int nvmlInit_t();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int nvmlShutdown_t();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int nvmlDeviceGetCount_t(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int nvmlDeviceGetHandleByIndex_t(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int nvmlDeviceGetName_t(IntPtr device, byte[] name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int nvmlDeviceGetTemperature_t(IntPtr device, int sensorType, out uint temp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int nvmlDeviceGetFanSpeed_t(IntPtr device, out uint speedPercent);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int nvmlDeviceGetPowerUsage_t(IntPtr device, out uint milliwatts);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int nvmlDeviceGetClockInfo_t(IntPtr device, int clockType, out uint clockMhz);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int nvmlDeviceGetUtilizationRates_t(IntPtr device, out NvmlUtilization utilization);

    /// <summary>nvmlUtilization_t — percent of time the GPU / memory was busy over the sample window.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization { public uint Gpu; public uint Memory; }
}

/// <summary>NVIDIA GPU telemetry via NVML, behind the common IGpuSensorProvider seam. HW-unvalidated.</summary>
internal sealed class NvidiaNvmlGpuProvider : IGpuSensorProvider, IDisposable
{
    private readonly NvmlInterop _nvml;
    private readonly object _lock = new();
    private bool _disposed;

    public string Name { get; }
    public bool IsAvailable => !_disposed;

    private NvidiaNvmlGpuProvider(NvmlInterop nvml) { _nvml = nvml; Name = nvml.DeviceName; }

    public static NvidiaNvmlGpuProvider? TryCreate(Action<string> log)
    {
        NvmlInterop? nvml = NvmlInterop.TryCreate(log);
        if (nvml == null) return null;
        log($"[gpu] {nvml.DeviceName}: NVML telemetry online (read-only).");
        return new NvidiaNvmlGpuProvider(nvml);
    }

    public bool TryRead(GpuMetric metric, out double value)
    {
        value = 0;
        if (_disposed) return false;
        lock (_lock)
        {
            return metric switch
            {
                GpuMetric.TempEdge    => _nvml.TryGetTemperatureC(out value),
                GpuMetric.FanPercent  => _nvml.TryGetFanPercent(out value),
                GpuMetric.PowerW      => _nvml.TryGetPowerWatts(out value),
                GpuMetric.ClockGfxMhz => _nvml.TryGetClockMhz(memory: false, out value),
                GpuMetric.ClockMemMhz => _nvml.TryGetClockMhz(memory: true, out value),
                GpuMetric.UtilGfx     => _nvml.TryGetUtilizationGfx(out value),
                /* Hot-spot / memory temp / fan-RPM are not in NVML's base getter API — report
                   not-available (per-metric gating hides them), not a bogus value. */
                _ => false
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _nvml.Dispose();
    }
}
