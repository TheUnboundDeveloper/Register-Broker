using System.Buffers.Binary;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| AquaSensorProvider — read-only Aquacomputer telemetry (user-mode, opt-in)    |
|                                                                            |
|   An Aquacomputer controller (the Quadro today) is an OFF-BOARD USB-HID      |
|   device, not a motherboard SMBus/Super-I/O chip: it streams its sensor      |
|   status on the HID interrupt IN endpoint. So, like the GPU provider, this   |
|   is a USER-MODE source with no kernel driver and no write path — every      |
|   value is a read of a streamed report. Opt-in (AllowAquaSensors), reduced   |
|   assurance — see SECURITY.md.                                              |
|                                                                            |
|   REMOVABLE / no-flap: unlike the GPU provider (created once, GPU assumed    |
|   fixed), this controller can be UNPLUGGED at runtime. The provider runs a   |
|   background poller that (re)opens the device and does a blocking ReadFile;   |
|   a staleness window gives hysteresis so a single missed report does NOT     |
|   drop the sensors (no flapping), while a sustained loss cleanly marks the   |
|   whole group absent and keeps retrying for re-plug. The aqua.* channels are |
|   flagged Removable so consumers render "not connected" instead of an error. |
|                                                                            |
|   Protocol ported as FACTS from the Linux hwmon driver                       |
|   drivers/hwmon/aquacomputer_d5next.c, cross-checked against liquidctl       |
|   (both agree on every offset/scale/sentinel). See QuadroProtocol below and  |
|   docs/AQUA-SENSOR-SUPPORT.md; provenance in THIRD-PARTY-NOTICES.md.         |
\*---------------------------------------------------------------------------*/

/// <summary>The read-only Aquacomputer metrics the catalog exposes (core set: temps + flow + fan RPM).
/// The integer order is the slot index into the provider's snapshot arrays — do not reorder.</summary>
internal enum AquaMetric
{
    Temp0 = 0, Temp1 = 1, Temp2 = 2, Temp3 = 3,   // 4 physical temperature inputs (°C)
    Flow  = 4,                                     // coolant flow (L/h)
    Fan0  = 5, Fan1 = 6, Fan2 = 7, Fan3 = 8        // 4 fan tach channels (RPM)
}

internal interface IAquaSensorProvider
{
    /// <summary>Human-readable controller name (e.g. "Aquacomputer Quadro"), for logs/labels.</summary>
    string Name { get; }
    /// <summary>True when the controller is currently present and a fresh status sample exists.
    /// Goes false (after the staleness window) when the controller is unplugged/stops streaming.</summary>
    bool IsAvailable { get; }
    /// <summary>Reads one metric. Returns false when stale/absent, or for a metric the device does not
    /// currently populate (e.g. a disconnected temperature probe reporting the 0x7FFF sentinel).</summary>
    bool TryRead(AquaMetric metric, out double value);
}

/// <summary>
/// Process-wide Aquacomputer sensor source. Stays null until the sensor service opts in
/// (AllowAquaSensors) and a supported controller is found at startup; the aqua.* channels read it
/// lazily at request time. Once created it survives hot-unplug/re-plug of the controller.
/// </summary>
internal static class AquaSensorProvider
{
    /// <summary>The active provider, or null when Aqua sensors are off or no controller was detected.</summary>
    public static IAquaSensorProvider? Current { get; set; }

    /// <summary>
    /// Probes for a supported Aquacomputer controller and returns a provider, or null (logged) when
    /// none is present at startup. Today only the Quadro is supported; other Aquacomputer devices
    /// (Octo, D5 Next, …) would each be another IAquaSensorProvider selected here.
    /// </summary>
    public static IAquaSensorProvider? TryCreate(Action<string> log)
        => QuadroHidProvider.TryCreate(log);
}

/*---------------------------------------------------------------------------*\
| QuadroProtocol — Aquacomputer Quadro status-report layout (ported facts)     |
\*---------------------------------------------------------------------------*/
/// <summary>
/// Byte offsets/encodings for the Quadro's HID status INPUT report. Sources (cross-checked, no
/// disagreement): Linux drivers/hwmon/aquacomputer_d5next.c and liquidctl driver/aquacomputer.py.
/// Offsets are into the raw report buffer INCLUDING the report-id byte at index 0. All multi-byte
/// values are big-endian.
/// </summary>
internal static class QuadroProtocol
{
    public const ushort UsbVendorId = 0x0C70;   // USB_VENDOR_ID_AQUACOMPUTER
    public const ushort UsbProductId = 0xF00D;  // USB_PRODUCT_ID_QUADRO
    public const byte StatusReportId = 0x01;    // STATUS_REPORT_ID
    public const int StatusReportLength = 220;  // liquidctl status_report_length (0xDC)

    public const int TempStart = 0x34;          // QUADRO_SENSOR_START; 4 sensors, stride 2, s16 centi-°C
    public const int TempStride = 0x02;         // AQC_SENSOR_SIZE
    public const int TempCount = 4;             // QUADRO_NUM_SENSORS
    public const ushort TempSentinel = 0x7FFF;  // AQC_SENSOR_NA (probe not connected)

    public const int FlowOffset = 0x6E;         // QUADRO_FLOW_SENSOR_OFFSET; u16, raw is dL/h (÷10 = L/h)

    public static readonly int[] FanBase = { 0x70, 0x7D, 0x8A, 0x97 }; // quadro_sensor_fan_offsets[]
    public const int FanSpeedSub = 0x08;        // AQC_FAN_SPEED_OFFSET (raw u16 = RPM)

    /// <summary>Smallest report length that still contains every field we decode (last fan RPM ends
    /// at FanBase[3]+FanSpeedSub+2). A shorter read is treated as a bad sample.</summary>
    public const int MinUsableLength = 0x97 + FanSpeedSub + 2; // 0xA1 = 161

    /// <summary>The number of snapshot slots = number of AquaMetric values.</summary>
    public const int SlotCount = 9;

    /// <summary>Decode a status report into the snapshot slots (indexed by AquaMetric). Sets ok[i]
    /// false for a metric the device does not currently populate (disconnected temp probe). Returns
    /// false (and touches nothing) when the buffer is too short or not a status report.</summary>
    public static bool TryDecode(byte[] buf, int length, double[] vals, bool[] ok)
    {
        if (length < MinUsableLength || buf.Length < MinUsableLength || buf[0] != StatusReportId)
            return false;

        for (int t = 0; t < TempCount; t++)
        {
            ushort u = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(TempStart + t * TempStride, 2));
            if (u == TempSentinel) { ok[t] = false; vals[t] = 0; }
            else { ok[t] = true; vals[t] = (short)u / 100.0; }   // s16 centi-°C → °C (handles sub-zero)
        }

        ushort flowRaw = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(FlowOffset, 2));
        ok[(int)AquaMetric.Flow] = true; vals[(int)AquaMetric.Flow] = flowRaw / 10.0; // dL/h → L/h

        for (int f = 0; f < FanBase.Length; f++)
        {
            ushort rpm = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(FanBase[f] + FanSpeedSub, 2));
            int slot = (int)AquaMetric.Fan0 + f;
            ok[slot] = true; vals[slot] = rpm;
        }
        return true;
    }
}

/*---------------------------------------------------------------------------*\
| QuadroHidProvider — the live Quadro source (background ReadFile poller)       |
\*---------------------------------------------------------------------------*/
internal sealed class QuadroHidProvider : IAquaSensorProvider, IDisposable
{
    /* Hysteresis tuning. A status sample older than StaleMs marks the whole group absent (the
       controller was unplugged or stopped streaming) — large enough that one missed report never
       flaps the sensors. ReopenBackoffMs paces re-detection while absent (the hot-plug retry).
       FailSleepMs avoids a busy-spin if a held-open handle fails reads fast. */
    private const long StaleMs = 5000;
    private const int ReopenBackoffMs = 3000;
    private const int FailSleepMs = 250;
    private const int MaxFailuresBeforeReopen = 5;

    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly double[] _vals = new double[QuadroProtocol.SlotCount];
    private readonly bool[] _ok = new bool[QuadroProtocol.SlotCount];
    private long _lastTick = long.MinValue;
    private bool _haveSample;

    private readonly Thread _poller;
    private volatile bool _stop;
    private volatile bool _disposed;
    private HidDevice? _dev;

    public string Name => "Aquacomputer Quadro";

    public bool IsAvailable
    {
        get
        {
            lock (_lock)
                return !_disposed && _haveSample && (Environment.TickCount64 - _lastTick) <= StaleMs;
        }
    }

    private QuadroHidProvider(HidDevice dev, Action<string> log)
    {
        _dev = dev;
        _log = log;
        _poller = new Thread(PollLoop) { IsBackground = true, Name = "aqua-quadro-poll" };
    }

    public static QuadroHidProvider? TryCreate(Action<string> log)
    {
        HidDevice? dev = TryOpen(log);
        if (dev == null) { log("[aqua] no Aquacomputer Quadro present — aqua.* stay absent."); return null; }

        var p = new QuadroHidProvider(dev, log);
        p._poller.Start();
        if (p.WaitForFirstSample(2000))
            log($"[aqua] Aquacomputer Quadro online (input report {dev.InputReportByteLength} B) — read-only, removable, reduced assurance.");
        else
            log("[aqua] Aquacomputer Quadro opened but no status report yet (will keep polling in the background).");
        return p;
    }

    /// <summary>Open the Quadro's streaming HID interface (largest input report among PID matches),
    /// disposing the other VID-matched interfaces. Returns null when no Quadro is present.</summary>
    private static HidDevice? TryOpen(Action<string> log)
    {
        IReadOnlyList<HidDevice> devs = HidDevice.OpenByVendor(QuadroProtocol.UsbVendorId, log);
        HidDevice? pick = null;
        foreach (HidDevice d in devs)
        {
            bool candidate = d.ProductId == QuadroProtocol.UsbProductId
                             && d.InputReportByteLength >= QuadroProtocol.MinUsableLength;
            if (candidate && (pick == null || d.InputReportByteLength > pick.InputReportByteLength))
            {
                pick?.Dispose();
                pick = d;
            }
            else d.Dispose();
        }
        return pick;
    }

    private bool WaitForFirstSample(int ms)
    {
        long deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            if (IsAvailable) return true;
            Thread.Sleep(25);
        }
        return IsAvailable;
    }

    private void PollLoop()
    {
        int failures = 0;
        while (!_stop)
        {
            HidDevice? dev = _dev;
            if (dev == null)
            {
                dev = TryOpen(_log);
                if (dev == null) { Thread.Sleep(ReopenBackoffMs); continue; }  // controller absent — retry (hot-plug)
                _dev = dev;
                failures = 0;
                _log("[aqua] Aquacomputer Quadro (re)connected.");
            }

            try
            {
                byte[] buf = new byte[Math.Max(dev.InputReportByteLength, QuadroProtocol.StatusReportLength)];
                /* Decode into LOCAL arrays, then publish the whole snapshot under the lock so a
                   concurrent TryRead never sees a half-written set of slots. */
                double[] lv = new double[QuadroProtocol.SlotCount];
                bool[] lo = new bool[QuadroProtocol.SlotCount];
                if (dev.ReadInput(buf, out int n) && QuadroProtocol.TryDecode(buf, n, lv, lo))
                {
                    lock (_lock)
                    {
                        Array.Copy(lv, _vals, QuadroProtocol.SlotCount);
                        Array.Copy(lo, _ok, QuadroProtocol.SlotCount);
                        _haveSample = true;
                        _lastTick = Environment.TickCount64;
                    }
                    failures = 0;
                }
                else if (++failures >= MaxFailuresBeforeReopen)
                {
                    _log("[aqua] Aquacomputer Quadro stopped responding — closing; will retry for re-plug.");
                    dev.Dispose(); _dev = null; failures = 0;
                }
                else
                {
                    Thread.Sleep(FailSleepMs);
                }
            }
            catch (Exception ex)
            {
                _log($"[aqua] poll error: {ex.Message}");
                try { dev.Dispose(); } catch { /* already gone */ }
                _dev = null; failures = 0;
                Thread.Sleep(FailSleepMs);
            }
        }
    }

    public bool TryRead(AquaMetric metric, out double value)
    {
        value = 0;
        int i = (int)metric;
        lock (_lock)
        {
            if (_disposed || !_haveSample) return false;
            if (Environment.TickCount64 - _lastTick > StaleMs) return false;  // controller gone / sample stale
            if (!_ok[i]) return false;                                        // e.g. disconnected temp probe
            value = _vals[i];
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop = true;
        try { _dev?.Dispose(); } catch { /* unblocks a pending ReadFile */ }
        if (_poller.IsAlive) _poller.Join(1500);
        _dev = null;
    }
}

/// <summary>
/// A fixed in-memory Aqua provider for --selftest: reports a supplied value for the metrics in the
/// map and "not available" for everything else, so the aqua.* gates/decoders can be verified
/// without real hardware. Never used in production.
/// </summary>
internal sealed class FixedAquaProvider : IAquaSensorProvider
{
    private readonly IReadOnlyDictionary<AquaMetric, double> _values;
    public string Name { get; }
    public bool IsAvailable => true;

    public FixedAquaProvider(string name, IReadOnlyDictionary<AquaMetric, double> values)
    {
        Name = name; _values = values;
    }

    public bool TryRead(AquaMetric metric, out double value) => _values.TryGetValue(metric, out value);
}
