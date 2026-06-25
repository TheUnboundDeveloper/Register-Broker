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
    private IntPtr _preparsed = IntPtr.Zero;   // cached HID preparsed data (lazy; freed on Dispose)
    private bool _preparsedTried;

    /// <summary>Feature-report length in bytes (incl. the leading report id), from HIDP_CAPS.</summary>
    public int FeatureReportByteLength { get; }
    /// <summary>Input-report length in bytes (incl. the leading report id), from HIDP_CAPS. Non-zero
    /// for devices that push status on the interrupt IN endpoint (read via <see cref="ReadInput"/>).</summary>
    public int InputReportByteLength { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public string Path { get; }
    /// <summary>HID top-level collection usage page / usage (HIDP_CAPS), for diagnostics/selection.</summary>
    public ushort UsagePage { get; }
    public ushort Usage { get; }
    /// <summary>USB interface number parsed from the device path (&amp;mi_NN); -1 if non-composite.</summary>
    public int InterfaceNumber { get; }

    private HidDevice(SafeFileHandle handle, string path, ushort vid, ushort pid,
                      int featureLen, int inputLen, ushort usagePage, ushort usage)
    {
        _handle = handle; Path = path; VendorId = vid; ProductId = pid;
        FeatureReportByteLength = featureLen; InputReportByteLength = inputLen;
        UsagePage = usagePage; Usage = usage;
        InterfaceNumber = ParseInterfaceNumber(path);
    }

    /// <summary>Parse the USB interface number from a Windows HID device path (e.g. "&amp;mi_02" -> 2).
    /// Returns -1 for a non-composite device (no &amp;mi_ token). Internal for selftest.</summary>
    internal static int ParseInterfaceNumber(string path)
    {
        int at = path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
        if (at < 0 || at + 6 > path.Length) return -1;
        return int.TryParse(path.Substring(at + 4, 2),
            System.Globalization.NumberStyles.HexNumber, null, out int n) ? n : -1;
    }

    /// <summary>Sends a HID feature report (HidD_SetFeature). The buffer's first byte is the report id.</summary>
    public bool SetFeature(byte[] report) => HidD_SetFeature(_handle, report, (uint)report.Length);

    /// <summary>
    /// Sends a HID OUTPUT report (HidD_SetOutputReport). The buffer's first byte is the report id
    /// (0x00 for an unnumbered report). Like SetFeature this goes through an IOCTL, so it works on the
    /// zero-access handle the input-owned collections force — no WriteFile / elevated access needed.
    /// This is the transport for devices that use output reports (hid_write) rather than feature
    /// reports (AMD Wraith Prism, Logitech, Corsair, most SteelSeries mice).
    /// </summary>
    public bool SetOutputReport(byte[] report) => HidD_SetOutputReport(_handle, report, (uint)report.Length);

    /// <summary>
    /// Reads a HID INPUT report (HidD_GetInputReport — a synchronous GET_REPORT control request, no
    /// overlapped I/O). Seed <paramref name="report"/>[0] with the report id (0x00 for unnumbered).
    /// This is the request/response read path some vendor collections need (e.g. Corsair iCUE V2's
    /// init handshake reads the VID/PID and the lighting-control probe response). Best-effort: returns
    /// false if the device does not service GET_REPORT on this collection.
    /// </summary>
    public bool GetInputReport(byte[] report) => HidD_GetInputReport(_handle, report, (uint)report.Length);

    /// <summary>
    /// Reads the current HID feature report (HidD_GetFeature) into <paramref name="report"/>. The
    /// caller seeds <c>report[0]</c> with the report id to fetch. Used to capture the device's current
    /// per-zone state so a whole-device packet write can edit one zone without clobbering the others.
    /// </summary>
    public bool GetFeature(byte[] report) => HidD_GetFeature(_handle, report, (uint)report.Length);

    /// <summary>
    /// Reads one HID INPUT report off the interrupt IN endpoint via ReadFile (a BLOCKING read — it
    /// returns when the device next pushes a report). This is the transport for vendor sensor devices
    /// that STREAM status rather than answering GET_REPORT — e.g. the Aquacomputer Quadro, whose
    /// status report HidD_GetInputReport refuses (ERROR_GEN_FAILURE) but ReadFile delivers. The
    /// handle must have been opened with read access (OpenByVendor's R|W open succeeds for
    /// vendor-defined collections). Pass a buffer of <see cref="InputReportByteLength"/> bytes; the
    /// first byte is the report id. Returns true and the byte count on success. Call only from a
    /// dedicated thread — the broker's request threads must not block on it. Disposing the device
    /// from another thread unblocks a pending read (it returns false), which is how the poller stops.
    /// </summary>
    public bool ReadInput(byte[] buffer, out int read)
    {
        bool ok = ReadFile(_handle, buffer, (uint)buffer.Length, out uint got, IntPtr.Zero);
        read = (int)got;
        return ok && got > 0;
    }

    public void Dispose()
    {
        if (_preparsed != IntPtr.Zero) { HidD_FreePreparsedData(_preparsed); _preparsed = IntPtr.Zero; }
        _handle.Dispose();
    }

    /// <summary>
    /// Opens every present HID interface whose USB vendor id matches <paramref name="vendorId"/>.
    /// Best-effort: unreadable interfaces are skipped. Caller disposes the returned devices.
    /// </summary>
    public static IReadOnlyList<HidDevice> OpenByVendor(ushort vendorId, Action<string> log)
        => OpenMatching(log, vendorId: vendorId, usagePage: 0);

    /// <summary>
    /// Opens every present HID interface whose top-level collection UsagePage matches
    /// <paramref name="usagePage"/> (e.g. 0x84 = USB HID Power Device, for UPS/battery). Used when the
    /// device is identified by its standard HID class rather than a fixed vendor id. Best-effort.
    /// </summary>
    public static IReadOnlyList<HidDevice> OpenByUsagePage(ushort usagePage, Action<string> log)
        => OpenMatching(log, vendorId: 0, usagePage: usagePage);

    private static IReadOnlyList<HidDevice> OpenMatching(Action<string> log, ushort vendorId, ushort usagePage)
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
                if (h.IsInvalid)
                {
                    /* OS-held input collections (keyboard/mouse) refuse GENERIC_READ|WRITE. Reopen with
                       ZERO desired access — HidD_GetFeature/SetFeature work on a 0-access handle (they
                       go through IOCTLs, not ReadFile/WriteFile), so RGB feature reports still flow.
                       This mirrors hidapi and is required for Razer command collections that the HID
                       input stack owns exclusively. */
                    h.Dispose();
                    h = CreateFileW(path, 0, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                }
                if (h.IsInvalid) { h.Dispose(); continue; }

                var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (!HidD_GetAttributes(h, ref attrs)) { h.Dispose(); continue; }
                if (vendorId != 0 && attrs.VendorID != vendorId) { h.Dispose(); continue; }

                GetCaps(h, out int featureLen, out int inputLen, out ushort up, out ushort usage);
                if (usagePage != 0 && up != usagePage) { h.Dispose(); continue; }

                found.Add(new HidDevice(h, path, attrs.VendorID, attrs.ProductID, featureLen, inputLen, up, usage));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        return found;
    }

    /// <summary>
    /// One parsed HID value-cap entry: which (UsagePage, Usage) lives in which report id / link
    /// collection, plus the field's bit size and decoded base-10 unit exponent. Used to read a
    /// descriptor-driven device (e.g. a HID Power Device / UPS) by usage rather than fixed offsets.
    /// </summary>
    internal readonly struct HidValueCap
    {
        public readonly ushort UsagePage;
        public readonly ushort Usage;
        public readonly ushort LinkCollection;
        public readonly byte   ReportId;
        public readonly int    BitSize;
        public readonly int    UnitsExp;   // decoded signed base-10 exponent (HID nibble: 8..15 => -8..-1)
        public HidValueCap(ushort up, ushort u, ushort lc, byte rid, int bits, int exp)
        { UsagePage = up; Usage = u; LinkCollection = lc; ReportId = rid; BitSize = bits; UnitsExp = exp; }
    }

    /// <summary>Lazily acquire (and cache) the device's HID preparsed data, or IntPtr.Zero if unavailable.</summary>
    private IntPtr Preparsed()
    {
        if (!_preparsedTried)
        {
            _preparsedTried = true;
            _preparsed = HidD_GetPreparsedData(_handle, out IntPtr pre) ? pre : IntPtr.Zero;
        }
        return _preparsed;
    }

    /// <summary>
    /// Enumerate the device's value caps for the FEATURE (or INPUT) report type. Returns an empty
    /// list when the device has none / preparsed data is unavailable. Read-only.
    /// </summary>
    public IReadOnlyList<HidValueCap> GetValueCaps(bool feature)
    {
        var result = new List<HidValueCap>();
        IntPtr pre = Preparsed();
        if (pre == IntPtr.Zero) return result;

        byte[] caps = new byte[256];
        if (HidP_GetCaps(pre, caps) != HIDP_STATUS_SUCCESS) return result;
        int count = BitConverter.ToUInt16(caps, feature ? 60 : 48);   // HIDP_CAPS.NumberFeature/InputValueCaps
        if (count <= 0) return result;

        int reportType = feature ? 2 : 0;                              // HIDP_REPORT_TYPE Feature=2, Input=0
        byte[] buf = new byte[count * HIDP_VALUE_CAPS_SIZE];
        ushort len = (ushort)count;
        if (HidP_GetValueCaps(reportType, buf, ref len, pre) != HIDP_STATUS_SUCCESS) return result;

        for (int i = 0; i < len; i++)
        {
            int o = i * HIDP_VALUE_CAPS_SIZE;
            ushort usagePage = BitConverter.ToUInt16(buf, o + 0);
            byte   reportId  = buf[o + 2];
            ushort linkColl  = BitConverter.ToUInt16(buf, o + 6);
            ushort bitSize   = BitConverter.ToUInt16(buf, o + 18);
            int    expRaw    = (int)(BitConverter.ToUInt32(buf, o + 32) & 0xF);
            ushort usage     = BitConverter.ToUInt16(buf, o + 56);     // NotRange.Usage (== Range.UsageMin)
            int exp = expRaw > 7 ? expRaw - 16 : expRaw;
            result.Add(new HidValueCap(usagePage, usage, linkColl, reportId, bitSize, exp));
        }
        return result;
    }

    /// <summary>
    /// Extract one usage's raw logical value from a report buffer (HidP_GetUsageValue). The buffer must
    /// already hold a fetched FEATURE report (see <see cref="GetFeature"/>) for the matching report id.
    /// Returns false when the usage is absent from that report. Read-only.
    /// </summary>
    public bool TryGetUsageValue(byte[] report, ushort usagePage, ushort linkCollection, ushort usage, out uint value)
    {
        value = 0;
        IntPtr pre = Preparsed();
        if (pre == IntPtr.Zero) return false;
        return HidP_GetUsageValue(2 /* Feature */, usagePage, linkCollection, usage, out value, pre,
                                  report, (uint)report.Length) == HIDP_STATUS_SUCCESS;
    }

    private static void GetCaps(SafeFileHandle h, out int featureLen, out int inputLen, out ushort usagePage, out ushort usage)
    {
        featureLen = 0; inputLen = 0; usagePage = 0; usage = 0;
        if (!HidD_GetPreparsedData(h, out IntPtr pre) || pre == IntPtr.Zero) return;
        try
        {
            byte[] caps = new byte[256];   // HIDP_CAPS: Usage @0, UsagePage @2, InputReportByteLength @4, FeatureReportByteLength @8
            if (HidP_GetCaps(pre, caps) != HIDP_STATUS_SUCCESS) return;
            usage      = BitConverter.ToUInt16(caps, 0);
            usagePage  = BitConverter.ToUInt16(caps, 2);
            inputLen   = BitConverter.ToUInt16(caps, 4);
            featureLen = BitConverter.ToUInt16(caps, 8);
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
    private const int HIDP_VALUE_CAPS_SIZE = 72;   // sizeof(HIDP_VALUE_CAPS) on x64
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid hidGuid);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attrs);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, uint length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buffer, uint length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buffer, uint length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, uint length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsed, byte[] caps);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int reportType, byte[] valueCaps, ref ushort valueCapsLength, IntPtr preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint usageValue, IntPtr preparsed, byte[] report, uint reportLength);

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
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool ReadFile(SafeFileHandle hFile, byte[] buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);
}
