using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| HidDevice — minimal Win32 HID feature-report transport                      |
|                                                                            |
|   A tiny, self-contained HID wrapper (SetupAPI + hid.dll) used by the MSI    |
|   Mystic Light controller. HidSharp was removed from this project, so this   |
|   re-implements only what the RGB path needs: enumerate HID interfaces,      |
|   match a USB vendor id, read the feature-report length, and send feature    |
|   reports. No input/output reads, no async.                                  |
|                                                                            |
|   This is a USER-MODE transport: unlike the SMBus/EC paths it does NOT go    |
|   through the kernel brick-guard. It is opt-in (AllowHidRgb) and the broker's |
|   baked report builder is the only safety boundary — see SECURITY.md.        |
\*---------------------------------------------------------------------------*/
internal sealed class HidDevice : IDisposable
{
    private readonly SafeFileHandle _handle;

    /// <summary>Feature-report length in bytes (incl. the leading report id), from HIDP_CAPS.</summary>
    public int FeatureReportByteLength { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public string Path { get; }

    private HidDevice(SafeFileHandle handle, string path, ushort vid, ushort pid, int featureLen)
    {
        _handle = handle; Path = path; VendorId = vid; ProductId = pid; FeatureReportByteLength = featureLen;
    }

    /// <summary>Sends a HID feature report (HidD_SetFeature). The buffer's first byte is the report id.</summary>
    public bool SetFeature(byte[] report) => HidD_SetFeature(_handle, report, (uint)report.Length);

    /// <summary>
    /// Reads the current HID feature report (HidD_GetFeature) into <paramref name="report"/>. The
    /// caller seeds <c>report[0]</c> with the report id to fetch. Used to capture the device's current
    /// per-zone state so a whole-device packet write can edit one zone without clobbering the others.
    /// </summary>
    public bool GetFeature(byte[] report) => HidD_GetFeature(_handle, report, (uint)report.Length);

    public void Dispose() => _handle.Dispose();

    /// <summary>
    /// Opens every present HID interface whose USB vendor id matches <paramref name="vendorId"/>.
    /// Best-effort: unreadable interfaces are skipped. Caller disposes the returned devices.
    /// </summary>
    public static IReadOnlyList<HidDevice> OpenByVendor(ushort vendorId, Action<string> log)
    {
        var found = new List<HidDevice>();
        HidD_GetHidGuid(out Guid hidGuid);

        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) { log("[hid] SetupDiGetClassDevs failed"); return found; }

        try
        {
            var ifaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref ifaceData); i++)
            {
                string? path = GetInterfacePath(set, ref ifaceData);
                if (path is null) continue;

                SafeFileHandle h = CreateFileW(path, 0xC0000000 /* R|W */, 0x3 /* share R|W */,
                    IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); continue; }

                var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (!HidD_GetAttributes(h, ref attrs) || attrs.VendorID != vendorId) { h.Dispose(); continue; }

                int featureLen = GetFeatureReportLength(h);
                found.Add(new HidDevice(h, path, attrs.VendorID, attrs.ProductID, featureLen));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        return found;
    }

    private static int GetFeatureReportLength(SafeFileHandle h)
    {
        if (!HidD_GetPreparsedData(h, out IntPtr pre) || pre == IntPtr.Zero) return 0;
        try
        {
            byte[] caps = new byte[256];                       // HIDP_CAPS; FeatureReportByteLength is at offset 8
            if (HidP_GetCaps(pre, caps) != HIDP_STATUS_SUCCESS) return 0;
            return BitConverter.ToUInt16(caps, 8);
        }
        finally { HidD_FreePreparsedData(pre); }
    }

    private static string? GetInterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifaceData)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref ifaceData, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
        if (required == 0) return null;

        IntPtr detail = Marshal.AllocHGlobal((int)required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on x64 (4-byte cbSize + WCHAR[1] + padding).
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref ifaceData, detail, required, out _, IntPtr.Zero))
                return null;
            return Marshal.PtrToStringUni(detail + 4);
        }
        finally { Marshal.FreeHGlobal(detail); }
    }

    /*-- Interop --*/
    private const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid hidGuid);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attrs);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, uint length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, uint length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsed, byte[] caps);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA ifaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifaceData, IntPtr detailData, uint detailSize, out uint requiredSize, IntPtr deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}
