using System.Runtime.InteropServices;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| NativeLib — hijack-safe native library loading for the user-mode backends    |
|                                                                            |
|   The GPU vendor backends load runtime DLLs (atiadlxx.dll / nvml.dll /       |
|   ze_loader.dll) into the LocalSystem broker process. Loading them by BARE   |
|   NAME uses the OS search order, which includes the application directory,   |
|   the current directory, and PATH — so a DLL of the same name planted in any |
|   of those could be loaded with the service's privileges (DLL hijacking).    |
|                                                                            |
|   These vendor DLLs are installed to System32 by their drivers, so we load   |
|   them by ABSOLUTE System32 path (plus any explicit absolute fallbacks).     |
|   An absolute-path load is not subject to the search order, closing the      |
|   hijack vector. If the vendor DLL is genuinely absent the load just fails    |
|   (the backend reports the GPU as not present, as before).                   |
\*---------------------------------------------------------------------------*/
internal static class NativeLib
{
    /// <summary>
    /// Loads a vendor runtime DLL from System32 by absolute path (hijack-safe), then tries any
    /// supplied absolute fallback paths in order. Never loads by bare name (no search-order
    /// exposure). Returns false (lib = IntPtr.Zero) when none of the candidates load.
    /// </summary>
    public static bool TryLoadSystem(string fileName, out IntPtr lib, params string[] fallbackAbsolutePaths)
    {
        string system32 = System.IO.Path.Combine(Environment.SystemDirectory, fileName);
        if (NativeLibrary.TryLoad(system32, out lib)) return true;

        foreach (string p in fallbackAbsolutePaths)
            if (!string.IsNullOrEmpty(p) && System.IO.Path.IsPathRooted(p) && NativeLibrary.TryLoad(p, out lib))
                return true;

        lib = IntPtr.Zero;
        return false;
    }
}
