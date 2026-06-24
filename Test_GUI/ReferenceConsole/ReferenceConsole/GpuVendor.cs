using System;
using System.IO;
using Avalonia.Media;

namespace ReferenceConsole;

/*---------------------------------------------------------------------------*\
| GpuVendor                                                                  |
|                                                                            |
|   Detects which GPU vendor the broker is serving GPU sensors from, so the   |
|   dashboard can colour the GPU cards by vendor:                             |
|       AMD = red,  NVIDIA = green,  Intel = blue.                            |
|                                                                            |
|   Detection mirrors the broker's own provider order (AMD ADL -> NVIDIA NVML |
|   -> Intel Level Zero, see GpuSensorProvider.TryCreate): it checks for each |
|   vendor's runtime DLL in the SAME order and takes the first present, so    |
|   the colour matches the provider the broker actually selected. This is a   |
|   local, side-effect-free presence check (no DLL is loaded, no new          |
|   dependency, no broker/protocol change).                                   |
\*---------------------------------------------------------------------------*/
public enum GpuVendorKind { None, Amd, Nvidia, Intel }

public static class GpuVendor
{
    private static GpuVendorKind? _cached;

    /// <summary>The detected vendor (cached). None when no supported GPU runtime is present.</summary>
    public static GpuVendorKind Kind => _cached ??= Detect();

    private static GpuVendorKind Detect()
    {
        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string pf    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        if (File.Exists(Path.Combine(sys32, "atiadlxx.dll"))) return GpuVendorKind.Amd;
        if (File.Exists(Path.Combine(sys32, "nvml.dll")) ||
            File.Exists(Path.Combine(pf, "NVIDIA Corporation", "NVSMI", "nvml.dll"))) return GpuVendorKind.Nvidia;
        if (File.Exists(Path.Combine(sys32, "ze_loader.dll"))) return GpuVendorKind.Intel;
        return GpuVendorKind.None;
    }

    /// <summary>Brand accent colour for a vendor, tuned for the dark dashboard.</summary>
    public static Color AccentColor(GpuVendorKind kind) => kind switch
    {
        GpuVendorKind.Amd    => Color.FromRgb(0xE5, 0x48, 0x4D),   // AMD red
        GpuVendorKind.Nvidia => Color.FromRgb(0x76, 0xB9, 0x00),   // NVIDIA green
        GpuVendorKind.Intel  => Color.FromRgb(0x3B, 0x9E, 0xE5),   // Intel blue
        _ => Colors.Gray,
    };

    public static string DisplayName(GpuVendorKind kind) => kind switch
    {
        GpuVendorKind.Amd    => "AMD",
        GpuVendorKind.Nvidia => "NVIDIA",
        GpuVendorKind.Intel  => "Intel",
        _ => "GPU",
    };
}
