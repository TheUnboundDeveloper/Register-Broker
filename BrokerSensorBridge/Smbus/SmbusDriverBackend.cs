using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| SmbusDriverBackend                                                         |
|                                                                            |
|   Talks to the BrokerSmbus kernel driver over DeviceIoControl using the   |
|   IOCTL contract in BrokerSmbusDriver/inc/SmbusBrokerProtocol.h.          |
|                                                                            |
|   If the driver is not installed (the common case until it is built and    |
|   test-signed on a WDK box) the device cannot be opened, Available is       |
|   false, and the broker simply never offers the smbus:read scope. The      |
|   bridge keeps working as a pure sensor broker.                            |
\*---------------------------------------------------------------------------*/
internal sealed class SmbusDriverBackend : ISmbusBackend, IDisposable
{
    private const uint PROTOCOL_VERSION = 1;
    private const int  MAX_BLOCK = 32;
    private const string Win32DeviceName = @"\\.\BrokerSmbus";

    /* JEDEC JC42.4 / TSE2004av DIMM thermal sensors live at SMBus 0x18 + slot, register 0x05.
       The TS for the DIMM whose SPD is at 0x50+i is at 0x18+i. Always bus 0 (the primary FCH
       segment the DIMMs sit on). Reads are non-destructive; the kernel allows reads ≤ 0x7F. */
    private const int DIMM_TEMP_BUS        = 0;
    private const int DIMM_TEMP_BASE_ADDR  = 0x18;
    private const int DIMM_TEMP_REG        = 0x05;
    private const int MAX_DIMM_SLOTS       = 8;     // JC42 address window 0x18–0x1F

    /* AMD per-CCD die temps. SMU sensor ids 1..8 = BrokerSmuCcd0Temp..Ccd7Temp; the kernel
       bakes in the per-model SMN address. The register's bit 11 is a valid flag — we probe it
       once per CCD to learn which exist (a 1-CCD part like the 5800X3D reports only CCD0). */
    private const uint SMU_CCD0_SENSOR     = 1;     // BrokerSmuCcd0Temp
    private const int  MAX_CCD             = 8;
    private const uint CCD_TEMP_VALID      = 0x800; // BIT(11), per k10temp

    /* AMD SVI2 voltage telemetry. SMU sensor ids 9/10 = BrokerSmuCoreVoltage/SocVoltage; the
       kernel bakes the per-model SVI plane address. A driver/CPU without the planes returns
       NotImplemented, so we probe core voltage once to learn whether the rails are served. */
    private const uint SMU_CORE_VOLTAGE    = 9;     // BrokerSmuCoreVoltage

    // CTL_CODE(0x8000, fn, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0) = (0x8000<<16)|(fn<<2)
    private const uint IOCTL_INFO    = (0x8000u << 16) | (0x800u << 2);
    private const uint IOCTL_XFER    = (0x8000u << 16) | (0x801u << 2);
    private const uint IOCTL_SMU     = (0x8000u << 16) | (0x802u << 2);
    private const uint IOCTL_SUPERIO = (0x8000u << 16) | (0x803u << 2);
    private const uint IOCTL_WRITE   = (0x8000u << 16) | (0x804u << 2);
    private const uint IOCTL_ENUM    = (0x8000u << 16) | (0x805u << 2);
    private const uint IOCTL_SIO_RGB = (0x8000u << 16) | (0x806u << 2);

    /* BROKER_ENUM_BACKENDS_RESPONSE layout (packed): Version(4) Count(4) then
       BROKER_ENUM_BACKENDS_MAX entries of Name[32] + Class(4) + Active(4) + Detail(4). */
    private const int ENUM_NAME_MAX   = 32;
    private const int ENUM_ENTRY_SIZE = ENUM_NAME_MAX + 12;
    private const int ENUM_MAX        = 16;

    private readonly SafeFileHandle? _device;
    private readonly Action<string> _log;

    /* DIMM thermal-sensor presence, detected once (lazily) by a non-destructive probe and
       cached. Only the sensor broker touches this (via the catalog); the control service never
       calls it, so write-only deployments add no DIMM bus traffic. */
    private readonly object _dimmGate = new();
    private bool _dimmScanned;
    private readonly bool[] _dimmPresent = new bool[MAX_DIMM_SLOTS];

    private readonly object _ccdGate = new();
    private bool _ccdScanned;
    private readonly bool[] _ccdPresent = new bool[MAX_CCD];

    private readonly object _smuVoltGate = new();
    private bool _smuVoltScanned;
    private bool _smuVoltPresent;

    public bool Available { get; }
    /// <summary>True when the driver reports the AMD SMU CPU-temperature path (CAP_SMU).</summary>
    public bool SmuAvailable { get; }
    /// <summary>True when the driver reports a supported Super-I/O (CAP_SUPERIO).</summary>
    public bool SuperioAvailable { get; }
    /// <summary>Detected Super-I/O chip id from INFO (0xD59x = Nuvoton, 0x86xx/0x87xx = ITE); 0 if none.</summary>
    public int SuperioChipId { get; }
    /// <summary>True when the driver reports the brick-guarded SMBus write path (CAP_WRITE).</summary>
    public bool WriteAvailable { get; }
    /// <summary>True when the driver reports the brick-guarded NCT6687 EC RGB write path (CAP_SUPERIO_RGB).</summary>
    public bool SuperioRgbAvailable { get; }
    public string Describe { get; }

    /* The driver's backend registry, queried once at open. Diagnostic only: the CAP_*
       bits and SuperioChipId stay authoritative for gating, so a driver that predates
       ENUM_BACKENDS (empty list) changes nothing about what is served. */
    private readonly BackendInfo[] _backends = Array.Empty<BackendInfo>();
    public IReadOnlyList<BackendInfo> EnumerateBackends() => _backends;

    public SmbusDriverBackend(Action<string> log)
    {
        _log = log;
        _device = TryOpen();

        if (_device is null || _device.IsInvalid)
        {
            Available = false;
            Describe = "SMBus driver not present (smbus:read disabled)";
            return;
        }

        // Query capabilities; only advertise the scope if the driver reports a read path.
        bool haveInfo = TryInfo(out uint busCount, out uint caps, out uint vendor, out uint superioChipId);
        string vendorName = vendor switch { 1 => "Intel", 2 => "AMD", _ => "unknown" };

        SmuAvailable      = haveInfo && (caps & 0x2u) != 0;   // CAP_SMU
        SuperioAvailable  = haveInfo && (caps & 0x4u) != 0;   // CAP_SUPERIO
        WriteAvailable    = haveInfo && (caps & 0x8u) != 0;   // CAP_WRITE
        SuperioRgbAvailable = haveInfo && (caps & 0x10u) != 0; // CAP_SUPERIO_RGB
        SuperioChipId     = haveInfo ? (int)superioChipId : 0;

        if (haveInfo && (caps & 0x1u) != 0)
        {
            Available = true;
            Describe = $"SMBus driver present: vendor={vendorName}, {busCount} bus(es), read capable"
                     + (SmuAvailable ? ", SMU temp capable" : "")
                     + (SuperioAvailable ? ", Super-I/O capable" : "");
        }
        else
        {
            Available = false;
            Describe = haveInfo
                ? $"SMBus driver present: vendor={vendorName}, {busCount} bus(es), read not yet implemented (scaffold)"
                : "SMBus driver present but INFO query failed";
        }
        _log("[smbus] " + Describe);

        _backends = QueryBackends();
        if (_backends.Length > 0)
        {
            string active = string.Join(", ", _backends.Where(b => b.Active)
                .Select(b => b.Detail != 0 ? $"{b.Name} (0x{b.Detail:X})" : b.Name));
            _log("[smbus] Detected backends: " + (active.Length > 0 ? active : "none")
               + " | registered: " + string.Join(", ", _backends.Select(b => b.Name)));
        }
    }

    /// <summary>
    /// Queries the driver's backend registry (IOCTL_BROKER_ENUM_BACKENDS). Returns an
    /// empty array on a driver that predates the op (the IOCTL fails with
    /// ERROR_INVALID_FUNCTION) — callers already treat enumeration as diagnostic.
    /// </summary>
    private BackendInfo[] QueryBackends()
    {
        if (_device is null || _device.IsInvalid) return Array.Empty<BackendInfo>();

        byte[] resp = new byte[8 + ENUM_MAX * ENUM_ENTRY_SIZE];
        if (!DeviceIoControl(_device, IOCTL_ENUM, Array.Empty<byte>(), 0, resp, (uint)resp.Length, out uint got, IntPtr.Zero)
            || got < 8)
        {
            _log("[smbus] driver predates ENUM_BACKENDS (IOCTL 0x805); backend enumeration unavailable");
            return Array.Empty<BackendInfo>();
        }

        uint count = Math.Min(ReadU32(resp, 4), ENUM_MAX);
        var list = new List<BackendInfo>((int)count);
        for (int i = 0; i < count; i++)
        {
            int off = 8 + i * ENUM_ENTRY_SIZE;
            if (off + ENUM_ENTRY_SIZE > got) break;             // trust only bytes the driver returned

            int nul = Array.IndexOf(resp, (byte)0, off, ENUM_NAME_MAX);
            int nameLen = nul < 0 ? ENUM_NAME_MAX : nul - off;
            list.Add(new BackendInfo(
                System.Text.Encoding.ASCII.GetString(resp, off, nameLen),
                (BackendClass)ReadU32(resp, off + ENUM_NAME_MAX),
                ReadU32(resp, off + ENUM_NAME_MAX + 4) != 0,
                ReadU32(resp, off + ENUM_NAME_MAX + 8)));
        }
        return list.ToArray();
    }

    public SmbusResult Read(int bus, int address, int command, SmbusOp op, int length)
    {
        if (_device is null || _device.IsInvalid) return SmbusResult.Fail(SmbusStatus.Unavailable);
        if (length < 0 || length > MAX_BLOCK)     return SmbusResult.Fail(SmbusStatus.BadRequest);

        byte[] req = new byte[24];
        WriteU32(req, 0, PROTOCOL_VERSION);
        WriteU32(req, 4, (uint)op);
        WriteU32(req, 8, (uint)bus);
        WriteU32(req, 12, (uint)address);
        WriteU32(req, 16, (uint)command);
        WriteU32(req, 20, (uint)length);

        byte[] resp = new byte[8 + MAX_BLOCK];
        if (!DeviceIoControl(_device, IOCTL_XFER, req, (uint)req.Length, resp, (uint)resp.Length, out uint xferBytes, IntPtr.Zero))
        {
            _log("[smbus] DeviceIoControl failed: " + Marshal.GetLastWin32Error());
            return SmbusResult.Fail(SmbusStatus.BusError);
        }
        if (xferBytes < 8)   // status(4)+length(4) header must be present before the fields are trusted
        {
            _log($"[smbus] short XFER response ({xferBytes} bytes)");
            return SmbusResult.Fail(SmbusStatus.BusError);
        }

        var status = (SmbusStatus)ReadU32(resp, 0);
        uint len = ReadU32(resp, 4);
        if (len > MAX_BLOCK) len = MAX_BLOCK;
        byte[] data = new byte[len];
        Array.Copy(resp, 8, data, 0, (int)len);
        return new SmbusResult(status, data);
    }

    /// <summary>
    /// Reads a named SMU sensor's raw 32-bit register (the kernel bakes in the SMN
    /// address; the caller never supplies one). Decode is the caller's job.
    /// </summary>
    public bool TryReadSmuRaw(uint sensor, out uint raw, out SmbusStatus status)
    {
        raw = 0;
        status = SmbusStatus.Unavailable;
        if (_device is null || _device.IsInvalid) return false;

        byte[] req = new byte[8];
        WriteU32(req, 0, PROTOCOL_VERSION);
        WriteU32(req, 4, sensor);

        byte[] resp = new byte[8];
        if (!DeviceIoControl(_device, IOCTL_SMU, req, (uint)req.Length, resp, (uint)resp.Length, out uint smuBytes, IntPtr.Zero)
            || smuBytes < 8)
        {
            _log("[smu] DeviceIoControl failed: " + Marshal.GetLastWin32Error());
            status = SmbusStatus.BusError;
            return false;
        }

        status = (SmbusStatus)ReadU32(resp, 0);
        raw = ReadU32(resp, 4);
        return status == SmbusStatus.Ok;
    }

    /// <summary>
    /// Reads a named Super-I/O (NCT6687D) sensor by {kind, index} — never a raw EC
    /// address. Returns the raw bytes; decode (temp/fan) is the caller's job.
    /// </summary>
    public bool TryReadSuperioRaw(uint kind, uint index, out uint raw, out SmbusStatus status)
    {
        raw = 0;
        status = SmbusStatus.Unavailable;
        if (_device is null || _device.IsInvalid) return false;

        byte[] req = new byte[12];
        WriteU32(req, 0, PROTOCOL_VERSION);
        WriteU32(req, 4, kind);
        WriteU32(req, 8, index);

        byte[] resp = new byte[8];
        if (!DeviceIoControl(_device, IOCTL_SUPERIO, req, (uint)req.Length, resp, (uint)resp.Length, out uint sioBytes, IntPtr.Zero)
            || sioBytes < 8)
        {
            _log("[superio] DeviceIoControl failed: " + Marshal.GetLastWin32Error());
            status = SmbusStatus.BusError;
            return false;
        }

        status = (SmbusStatus)ReadU32(resp, 0);
        raw = ReadU32(resp, 4);
        return status == SmbusStatus.Ok;
    }

    /// <summary>
    /// Lazily probes which DIMM slots have a JC42.4 thermal sensor (a non-destructive word
    /// read of reg 0x05 at 0x18+i; an absent device NAKs → BusError). Runs once, cached.
    /// </summary>
    private void EnsureDimmScan()
    {
        if (_dimmScanned) return;
        lock (_dimmGate)
        {
            if (_dimmScanned) return;
            if (Available)
            {
                int found = 0;
                for (int i = 0; i < MAX_DIMM_SLOTS; i++)
                {
                    SmbusResult r = Read(DIMM_TEMP_BUS, DIMM_TEMP_BASE_ADDR + i, DIMM_TEMP_REG, SmbusOp.ReadWord, 2);
                    _dimmPresent[i] = r.Ok && r.Data.Length >= 2;
                    if (_dimmPresent[i]) found++;
                }
                _log($"[dimm] thermal-sensor scan: {found} present (0x{DIMM_TEMP_BASE_ADDR:X2}+)");
            }
            _dimmScanned = true;
        }
    }

    public bool DimmTempPresent(int index)
    {
        if (index < 0 || index >= MAX_DIMM_SLOTS) return false;
        EnsureDimmScan();
        return _dimmPresent[index];
    }

    /// <summary>
    /// Lazily probes which CCD die-temp sensors report valid (read each CCD's SMU register once
    /// and test the valid bit). Runs once, cached. Only the sensor broker calls this.
    /// </summary>
    private void EnsureCcdScan()
    {
        if (_ccdScanned) return;
        lock (_ccdGate)
        {
            if (_ccdScanned) return;
            if (SmuAvailable)
            {
                int found = 0;
                for (int c = 0; c < MAX_CCD; c++)
                {
                    _ccdPresent[c] = TryReadSmuRaw(SMU_CCD0_SENSOR + (uint)c, out uint raw, out _)
                                     && (raw & CCD_TEMP_VALID) != 0;
                    if (_ccdPresent[c]) found++;
                }
                _log($"[smu] CCD temp scan: {found} present");
            }
            _ccdScanned = true;
        }
    }

    public bool CcdTempPresent(int ccd)
    {
        if (ccd < 0 || ccd >= MAX_CCD) return false;
        EnsureCcdScan();
        return _ccdPresent[ccd];
    }

    /// <summary>
    /// Lazily probes whether the driver serves AMD SVI2 voltage telemetry (read core voltage once;
    /// Ok means the CPU model's plane is baked in). Runs once, cached. Only the sensor broker calls
    /// this. An old driver returns BadRequest for the new sensor id, leaving the rails absent.
    /// </summary>
    private void EnsureSmuVoltageScan()
    {
        if (_smuVoltScanned) return;
        lock (_smuVoltGate)
        {
            if (_smuVoltScanned) return;
            if (SmuAvailable)
            {
                _smuVoltPresent = TryReadSmuRaw(SMU_CORE_VOLTAGE, out _, out _);
                _log($"[smu] SVI voltage telemetry: {(_smuVoltPresent ? "present" : "not available")}");
            }
            _smuVoltScanned = true;
        }
    }

    public bool SmuVoltagePresent
    {
        get { EnsureSmuVoltageScan(); return _smuVoltPresent; }
    }

    public bool TryReadDimmTempRaw(int index, out uint raw, out SmbusStatus status)
    {
        raw = 0;
        status = SmbusStatus.Unavailable;
        if (index < 0 || index >= MAX_DIMM_SLOTS) { status = SmbusStatus.BadRequest; return false; }

        SmbusResult r = Read(DIMM_TEMP_BUS, DIMM_TEMP_BASE_ADDR + index, DIMM_TEMP_REG, SmbusOp.ReadWord, 2);
        status = r.Status;
        if (!r.Ok || r.Data.Length < 2) return false;

        /* The JC42 temperature register is transmitted MSB-first; the driver places the first
           wire byte (the MSB) in Data[0] and the LSB in Data[1] (see SmbusAmd.c read-word).
           Pack MSB-first so DecodeJc42TempC sees the register value (== Linux read_word_swapped). */
        raw = (uint)((r.Data[0] << 8) | r.Data[1]);
        return true;
    }

    /// <summary>
    /// Bounded SMBus write (byte or word). The kernel brick-guard permits only the RGB
    /// address range; SPD and everything else return Forbidden. The caller (broker/control
    /// service) supplies a baked address — clients never reach this directly.
    /// </summary>
    public bool TryWrite(int bus, int address, int command, int data, bool word, out SmbusStatus status)
    {
        status = SmbusStatus.Unavailable;
        if (_device is null || _device.IsInvalid) return false;

        byte[] req = new byte[24];
        WriteU32(req, 0, PROTOCOL_VERSION);
        WriteU32(req, 4, word ? 4u : 3u);       // BrokerSmbusWriteWord=4 / WriteByte=3
        WriteU32(req, 8, (uint)bus);
        WriteU32(req, 12, (uint)address);
        WriteU32(req, 16, (uint)command);
        WriteU32(req, 20, (uint)data);

        byte[] resp = new byte[4];
        if (!DeviceIoControl(_device, IOCTL_WRITE, req, (uint)req.Length, resp, (uint)resp.Length, out uint wrBytes, IntPtr.Zero)
            || wrBytes < 4)
        {
            _log("[smbus-write] DeviceIoControl failed: " + Marshal.GetLastWin32Error());
            status = SmbusStatus.BusError;
            return false;
        }

        status = (SmbusStatus)ReadU32(resp, 0);
        return status == SmbusStatus.Ok;
    }

    /// <summary>
    /// Bounded SMBus block write (1..32 bytes, BrokerSmbusWriteBlock). Sends the full
    /// extended write request (the byte/word path keeps the legacy 24-byte prefix);
    /// the kernel re-validates Length and applies the same RGB-only brick-guard.
    /// </summary>
    public bool TryWriteBlock(int bus, int address, int command, ReadOnlySpan<byte> data, out SmbusStatus status)
    {
        status = SmbusStatus.Unavailable;
        if (_device is null || _device.IsInvalid) return false;
        if (data.Length is < 1 or > MAX_BLOCK) { status = SmbusStatus.BadRequest; return false; }

        byte[] req = new byte[28 + MAX_BLOCK];   // V1 prefix (24) + Length (4) + Block (32)
        WriteU32(req, 0, PROTOCOL_VERSION);
        WriteU32(req, 4, 5u);                    // BrokerSmbusWriteBlock
        WriteU32(req, 8, (uint)bus);
        WriteU32(req, 12, (uint)address);
        WriteU32(req, 16, (uint)command);
        WriteU32(req, 20, 0u);                   // Data: unused for block
        WriteU32(req, 24, (uint)data.Length);
        data.CopyTo(req.AsSpan(28));

        byte[] resp = new byte[4];
        if (!DeviceIoControl(_device, IOCTL_WRITE, req, (uint)req.Length, resp, (uint)resp.Length, out uint wrBytes, IntPtr.Zero)
            || wrBytes < 4)
        {
            _log("[smbus-write] block DeviceIoControl failed: " + Marshal.GetLastWin32Error());
            status = SmbusStatus.BusError;
            return false;
        }

        status = (SmbusStatus)ReadU32(resp, 0);
        return status == SmbusStatus.Ok;
    }

    /// <summary>
    /// Bounded NCT6687 EC RGB register write (IOCTL_BROKER_SUPERIO_RGB_WRITE). Writes 1..32 bytes
    /// to consecutive EC addresses from <paramref name="ecAddress"/>. The kernel re-validates the
    /// length and applies the NCT6687 RGB-window brick-guard; while the EC RGB path is
    /// HW-unvalidated the kernel refuses every write (Forbidden) and SuperioRgbAvailable is false.
    /// </summary>
    public bool TrySuperioRgbWrite(int ecAddress, ReadOnlySpan<byte> data, out SmbusStatus status)
    {
        status = SmbusStatus.Unavailable;
        if (_device is null || _device.IsInvalid) return false;
        if (data.Length is < 1 or > MAX_BLOCK) { status = SmbusStatus.BadRequest; return false; }

        // BROKER_SUPERIO_RGB_WRITE_REQUEST: Version(4) Address(4) Length(4) Block[32]
        byte[] req = new byte[12 + MAX_BLOCK];
        WriteU32(req, 0, PROTOCOL_VERSION);
        WriteU32(req, 4, (uint)ecAddress);
        WriteU32(req, 8, (uint)data.Length);
        data.CopyTo(req.AsSpan(12));

        byte[] resp = new byte[4];
        if (!DeviceIoControl(_device, IOCTL_SIO_RGB, req, (uint)req.Length, resp, (uint)resp.Length, out uint wrBytes, IntPtr.Zero)
            || wrBytes < 4)
        {
            _log("[superio-rgb] DeviceIoControl failed: " + Marshal.GetLastWin32Error());
            status = SmbusStatus.BusError;
            return false;
        }

        status = (SmbusStatus)ReadU32(resp, 0);
        return status == SmbusStatus.Ok;
    }

    private SafeFileHandle? TryOpen()
    {
        try
        {
            var h = CreateFileW(Win32DeviceName,
                0xC0000000 /* GENERIC_READ|GENERIC_WRITE */,
                0x3 /* FILE_SHARE_READ|WRITE */,
                IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            return h;
        }
        catch { return null; }
    }

    /// <summary>Per-bus diagnostic from INFO: (PortSelect &lt;&lt; 16) | IoBase, for up to 8 buses.</summary>
    public IReadOnlyList<uint> BusInfo => _busInfo;
    private readonly uint[] _busInfo = new uint[8];

    private bool TryInfo(out uint busCount, out uint caps, out uint vendor, out uint superioChipId)
    {
        busCount = 0; caps = 0; vendor = 0; superioChipId = 0;
        byte[] outBuf = new byte[52];   // Version, BusCount, Capabilities, Vendor, BusInfo[8], SuperioChipId
        if (_device is null ||
            !DeviceIoControl(_device, IOCTL_INFO, Array.Empty<byte>(), 0, outBuf, (uint)outBuf.Length, out uint infoBytes, IntPtr.Zero) ||
            infoBytes < 16)   // must cover BusCount(4)+Capabilities(8)+Vendor(12); BusInfo is diagnostic
            return false;
        busCount = ReadU32(outBuf, 4);
        caps     = ReadU32(outBuf, 8);
        vendor   = ReadU32(outBuf, 12);
        for (int i = 0; i < 8; i++) _busInfo[i] = ReadU32(outBuf, 16 + i * 4);
        // SuperioChipId was appended after BusInfo[8] (offset 48). Older drivers return only
        // 48 bytes; leave it 0 in that case so the catalog falls back to the NCT default.
        if (infoBytes >= 52) superioChipId = ReadU32(outBuf, 48);
        return true;
    }

    private static void WriteU32(byte[] b, int off, uint v) => BitConverter.TryWriteBytes(b.AsSpan(off), v);
    private static uint ReadU32(byte[] b, int off) => BitConverter.ToUInt32(b, off);

    public void Dispose() => _device?.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}
