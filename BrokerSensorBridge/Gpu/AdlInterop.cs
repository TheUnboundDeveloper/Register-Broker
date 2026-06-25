using System.Runtime.InteropServices;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| AdlInterop — minimal AMD Display Library (ADL2) P/Invoke, READ-ONLY          |
|                                                                            |
|   A tiny, self-contained wrapper over the AMD-driver-shipped atiadlxx.dll   |
|   used by the GPU sensor provider. It resolves only the handful of ADL2     |
|   entry points the read path needs — create/destroy the ADL context,        |
|   enumerate adapters to find the primary AMD GPU, and pull the PMLog         |
|   telemetry block (ADL2_New_QueryPMLogData_Get) — and nothing else. There    |
|   is NO write, overclock, fan, or power entry point resolved here: this is   |
|   a sensor source, not a control path.                                       |
|                                                                            |
|   This is a USER-MODE source: like the USB-HID RGB transport it does NOT     |
|   go through the kernel driver or its brick-guard (there is no driver        |
|   involved at all — the AMD display driver owns the GPU). It is opt-in       |
|   (AllowGpuSensors) and reduced-assurance — see SECURITY.md.                 |
|                                                                            |
|   Provenance: ADL is a published, first-party AMD interface; the struct      |
|   layouts, entry-point names, and PMLOG sensor indices are AMD ADL SDK       |
|   facts (adl_sdk.h / adl_structures.h). atiadlxx.dll is loaded at runtime    |
|   (it ships with the Radeon driver) and is NOT redistributed.                |
\*---------------------------------------------------------------------------*/
internal sealed class AdlInterop : IDisposable
{
    private const int ADL_OK = 0;
    private const int ADL_VENDOR_ID_AMD = 1002;        // ADL reports AMD's PCI vendor id in DECIMAL (not 0x1002)
    private const int ADL_PMLOG_MAX_SENSORS = 256;     // ADLPMLogDataOutput.sensors[256]

    /* ADL needs a caller-supplied allocator. Keep the delegate field alive for the whole
       context lifetime — ADL may call it during queries, and a collected delegate is an
       instant access violation. */
    private readonly AdlMainMemoryAllocDelegate _malloc;
    private readonly ADL2_Main_Control_Destroy_t _destroy;
    private readonly ADL2_New_QueryPMLogData_Get_t _queryPmLog;
    private readonly ADL2_Adapter_VRAMUsage_Get_t? _vramUsage;   // optional — absent on older ADL
    private IntPtr _context;
    private bool _disposed;

    /// <summary>The chosen AMD adapter's ADL index (the value PMLog queries are keyed by).</summary>
    public int AdapterIndex { get; }
    /// <summary>The adapter's marketing name (e.g. "AMD Radeon RX 7900 XTX").</summary>
    public string AdapterName { get; }

    private AdlInterop(IntPtr context, int adapterIndex, string adapterName,
                       AdlMainMemoryAllocDelegate malloc,
                       ADL2_Main_Control_Destroy_t destroy,
                       ADL2_New_QueryPMLogData_Get_t queryPmLog,
                       ADL2_Adapter_VRAMUsage_Get_t? vramUsage)
    {
        _context = context; AdapterIndex = adapterIndex; AdapterName = adapterName;
        _malloc = malloc; _destroy = destroy; _queryPmLog = queryPmLog; _vramUsage = vramUsage;
    }

    /// <summary>
    /// Loads atiadlxx.dll, creates an ADL2 context, and selects the first present AMD GPU adapter
    /// (deduplicated by PCI bus number — a GPU appears once per display output). Returns null with
    /// a logged reason when ADL is absent (no Radeon driver), an entry point is missing, or no AMD
    /// GPU is found. Read-only: nothing here can change hardware state.
    /// </summary>
    public static AdlInterop? TryCreate(Action<string> log)
    {
        if (!NativeLib.TryLoadSystem("atiadlxx.dll", out IntPtr lib))   // System32 only — hijack-safe
        {
            log("[gpu] atiadlxx.dll not present (no AMD Radeon driver installed) — AMD GPU sensors unavailable.");
            return null;
        }

        if (!TryGet(lib, "ADL2_Main_Control_Create", out ADL2_Main_Control_Create_t? create) ||
            !TryGet(lib, "ADL2_Main_Control_Destroy", out ADL2_Main_Control_Destroy_t? destroy) ||
            !TryGet(lib, "ADL2_Adapter_NumberOfAdapters_Get", out ADL2_Adapter_NumberOfAdapters_Get_t? numAdapters) ||
            !TryGet(lib, "ADL2_Adapter_AdapterInfo_Get", out ADL2_Adapter_AdapterInfo_Get_t? adapterInfo) ||
            !TryGet(lib, "ADL2_New_QueryPMLogData_Get", out ADL2_New_QueryPMLogData_Get_t? queryPmLog))
        {
            log("[gpu] atiadlxx.dll is missing a required ADL2 entry point — AMD GPU sensors unavailable.");
            return null;
        }

        /* VRAM usage is an optional extra (separate from PMLog); resolve best-effort so an older
           atiadlxx.dll that lacks it simply leaves gpu.mem.used not-available. */
        TryGet(lib, "ADL2_Adapter_VRAMUsage_Get", out ADL2_Adapter_VRAMUsage_Get_t? vramUsage);

        /* The malloc callback ADL calls to allocate buffers it hands back. Must outlive the context. */
        AdlMainMemoryAllocDelegate malloc = size => Marshal.AllocHGlobal(size);

        IntPtr context = IntPtr.Zero;
        if (create!(malloc, 1 /* enumerate connected adapters only */, out context) != ADL_OK || context == IntPtr.Zero)
        {
            log("[gpu] ADL2_Main_Control_Create failed — AMD GPU sensors unavailable.");
            return null;
        }

        try
        {
            if (numAdapters!(context, out int count) != ADL_OK || count <= 0)
            {
                log("[gpu] ADL reports no adapters — AMD GPU sensors unavailable.");
                destroy!(context);
                return null;
            }

            int stride = Marshal.SizeOf<AdapterInfo>();
            IntPtr buf = Marshal.AllocHGlobal(stride * count);
            try
            {
                /* Zero the buffer so unfilled trailing entries read as not-present. */
                for (int off = 0; off < stride * count; off += IntPtr.Size)
                    Marshal.WriteIntPtr(buf, off, IntPtr.Zero);

                if (adapterInfo!(context, buf, stride * count) != ADL_OK)
                {
                    log("[gpu] ADL2_Adapter_AdapterInfo_Get failed — AMD GPU sensors unavailable.");
                    destroy!(context);
                    return null;
                }

                var seenBus = new HashSet<int>();
                int chosenIndex = -1;
                string chosenName = "AMD GPU";
                for (int i = 0; i < count; i++)
                {
                    var ai = Marshal.PtrToStructure<AdapterInfo>(buf + i * stride);
                    if (ai.iVendorID != ADL_VENDOR_ID_AMD) continue;   // AMD GPUs only
                    if (ai.iExist == 0 && ai.iPresent == 0) continue;  // physically present adapters only
                    if (!seenBus.Add(ai.iBusNumber)) continue;         // one logical GPU per PCI bus

                    chosenIndex = ai.iAdapterIndex;
                    if (!string.IsNullOrWhiteSpace(ai.strAdapterName)) chosenName = ai.strAdapterName.Trim();
                    break;
                }

                if (chosenIndex < 0)
                {
                    log("[gpu] no present AMD GPU adapter found via ADL — AMD GPU sensors unavailable.");
                    destroy!(context);
                    return null;
                }

                var adl = new AdlInterop(context, chosenIndex, chosenName, malloc, destroy!, queryPmLog!, vramUsage);
                context = IntPtr.Zero;   // ownership transferred to the instance; don't destroy in finally
                return adl;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex)
        {
            log($"[gpu] ADL initialization threw ({ex.Message}) — AMD GPU sensors unavailable.");
            if (context != IntPtr.Zero) { try { destroy!(context); } catch { /* best effort */ } }
            return null;
        }
    }

    /// <summary>
    /// Reads the GPU's current PMLog telemetry block. Fills <paramref name="supported"/> and
    /// <paramref name="value"/> (both length ADL_PMLOG_MAX_SENSORS) indexed by the PMLOG sensor
    /// type; a sensor with supported[i]==0 is not present on this ASIC/driver. Returns false on a
    /// query error. Thread-safety is the caller's responsibility (the provider serializes).
    /// </summary>
    public bool TryQueryPmLog(int[] supported, int[] value)
    {
        if (_disposed || _context == IntPtr.Zero) return false;
        if (supported.Length < ADL_PMLOG_MAX_SENSORS || value.Length < ADL_PMLOG_MAX_SENSORS)
            return false;

        /* ADLPMLogDataOutput { int size; ADLSingleSensorData sensors[256] { int supported; int value; } }.
           Marshal it as a flat buffer rather than a 2 KB fixed struct: leading int + 256 * (int,int). */
        int bufSize = sizeof(int) + ADL_PMLOG_MAX_SENSORS * 2 * sizeof(int);
        IntPtr buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            for (int off = 0; off < bufSize; off += sizeof(int)) Marshal.WriteInt32(buf, off, 0);
            if (_queryPmLog(_context, AdapterIndex, buf) != ADL_OK) return false;

            for (int i = 0; i < ADL_PMLOG_MAX_SENSORS; i++)
            {
                int baseOff = sizeof(int) + i * 2 * sizeof(int);
                supported[i] = Marshal.ReadInt32(buf, baseOff);
                value[i]     = Marshal.ReadInt32(buf, baseOff + sizeof(int));
            }
            return true;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// Reads dedicated VRAM in use (MB) via ADL2_Adapter_VRAMUsage_Get. Separate from the PMLog block.
    /// Returns false when the entry point is absent (older driver) or the query errors. Read-only.
    /// </summary>
    public bool TryGetVramUsageMb(out int megabytes)
    {
        megabytes = 0;
        if (_disposed || _context == IntPtr.Zero || _vramUsage is null) return false;
        try
        {
            if (_vramUsage(_context, AdapterIndex, out int raw) != ADL_OK || raw < 0) return false;
            /* Despite the entry point's "InMB" name, some drivers return KB. Anchored on an
               RX 7900 XTX: raw 2,478,836 -> 2.36 GB in use (matches a labeled reference). Auto-detect:
               anything above 64 GB-as-MB can't be megabytes for any real GPU, so treat it as KB. */
            megabytes = raw > 65536 ? raw / 1024 : raw;
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_context != IntPtr.Zero)
        {
            try { _destroy(_context); } catch { /* best effort on shutdown */ }
            _context = IntPtr.Zero;
        }
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

    /*-- ADL2 entry-point signatures. On x64 the ABI is unified, so Cdecl here is a no-op label.
         ADL_CONTEXT_HANDLE is an opaque void* (IntPtr). Every call returns int (ADL_OK == 0). --*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AdlMainMemoryAllocDelegate(int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ADL2_Main_Control_Create_t(AdlMainMemoryAllocDelegate callback, int enumConnectedAdapters, out IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ADL2_Main_Control_Destroy_t(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ADL2_Adapter_NumberOfAdapters_Get_t(IntPtr context, out int numAdapters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ADL2_Adapter_AdapterInfo_Get_t(IntPtr context, IntPtr info, int inputSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ADL2_New_QueryPMLogData_Get_t(IntPtr context, int adapterIndex, IntPtr pmLogDataOutput);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ADL2_Adapter_VRAMUsage_Get_t(IntPtr context, int adapterIndex, out int vramUsageInMB);

    /// <summary>ADL AdapterInfo (adl_structures.h). All-int + inline ANSI char[256] fields, no pointers.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct AdapterInfo
    {
        public int iSize;
        public int iAdapterIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strUDID;
        public int iBusNumber;
        public int iDeviceNumber;
        public int iFunctionNumber;
        public int iVendorID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strDisplayName;
        public int iPresent;
        public int iExist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strDriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strDriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strPNPString;
        public int iOSDisplayIndex;
    }
}
