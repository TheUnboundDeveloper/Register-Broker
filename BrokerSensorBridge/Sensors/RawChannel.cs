namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| RawChannel / ChipChannels                                                  |
|                                                                            |
|   A RawChannel is a chip backend's view of one sensor BEFORE any board      |
|   knowledge: a stable raw id (`{chip}.{kind}.{index}`), a native unit, and  |
|   a base value produced by the chip decoder. It carries NO label and NO     |
|   per-rail scale — those are board calibration, applied later in            |
|   SensorCatalog. Raw ids are the stable persistence key (see                |
|   CALIBRATION-AND-REGISTRY-PLAN.md, decision #1); legacy semantic ids       |
|   resolve to them via the alias map.                                       |
|                                                                            |
|   IMPORTANT: the register/EC/SMN knowledge stays in the kernel driver and   |
|   in SensorDecode — a RawChannel only names a backend operation, never an   |
|   address. Adding a chip = adding a channel list here + a decoder.          |
\*---------------------------------------------------------------------------*/
internal readonly record struct RawReading(bool Ok, double Value, string Status);

internal sealed class RawChannel
{
    public string RawId { get; }
    public string DefaultUnit { get; }
    public int Round { get; }                       // decimal places for the final (scaled) value
    private readonly Func<ISmbusBackend, bool> _available;
    private readonly Func<ISmbusBackend, RawReading> _read;

    public RawChannel(string rawId, string unit, int round,
                      Func<ISmbusBackend, bool> available, Func<ISmbusBackend, RawReading> read)
    {
        RawId = rawId; DefaultUnit = unit; Round = round; _available = available; _read = read;
    }

    public bool IsAvailable(ISmbusBackend b) => _available(b);
    public RawReading ReadBase(ISmbusBackend b) => _read(b);
}

internal static class ChipChannels
{
    /* Super-I/O kind constants (mirror the driver's BROKER_SUPERIO_KIND_*). */
    private const uint Temp = 0, Fan = 1, Voltage = 2;

    /* Chip-family gates from the detected SIO chip id. The two Nuvoton families are
       disjoint under the 0xFFF0 mask, so no chip ever lights both channel sets.
       (The 0xFFF0 mask is enough HERE because 6796/0xD420 and 6798/0xD428 expose
       identical channels; the kernel's detect keeps the load-bearing 0xFFF8.)
       The ITE (0x86xx/0x87xx) gate left with the archived backend (_archive_gigabyte\). */
    private static readonly int[] NctEcIds   = { 0xC730, 0xD440, 0xD590 };                            // NCT6683 / 6686 / 6687D
    private static readonly int[] Nct6775Ids = { 0xC560, 0xC800, 0xC910, 0xD120, 0xD350, 0xD420, 0xD450 }; // 6779/6791/6792/6793/6795/6796+6798/6797
    private static bool IsNct(ISmbusBackend b)
        => b.SuperioAvailable && (Array.IndexOf(NctEcIds, b.SuperioChipId & 0xFFF0) >= 0 || b.SuperioChipId == 0);
    private static bool IsNct6775(ISmbusBackend b)
        => b.SuperioAvailable && Array.IndexOf(Nct6775Ids, b.SuperioChipId & 0xFFF0) >= 0;

    public static readonly IReadOnlyList<RawChannel> All = Build();

    private static IReadOnlyList<RawChannel> Build()
    {
        var list = new List<RawChannel>();

        /*-- AMD SMU: CPU die temp + per-CCD (only CCDs reporting valid appear). --*/
        list.Add(new RawChannel("smu.cpu.temp", "°C", 2,
            b => b.SmuAvailable,
            b => b.TryReadSmuRaw(0, out uint raw, out SmbusStatus st)
                ? new RawReading(true, SensorDecode.AmdCpuTctlC(raw), "Ok")
                : new RawReading(false, 0, st.ToString())));

        for (int ccd = 0; ccd < 8; ccd++)
        {
            int c = ccd;
            list.Add(new RawChannel($"smu.ccd.{c}", "°C", 1,
                b => b.CcdTempPresent(c),
                b => b.TryReadSmuRaw((uint)(1 + c), out uint raw, out SmbusStatus st) && (raw & SensorDecode.AmdCcdValid) != 0
                    ? new RawReading(true, SensorDecode.AmdCcdTempC(raw), "Ok")
                    : new RawReading(false, 0, st == SmbusStatus.Ok ? "Invalid" : st.ToString())));
        }

        /*-- NCT6687D (MSI). Present only on a Nuvoton chip. --*/
        for (int i = 0; i < 6; i++)
        {
            int idx = i;
            list.Add(new RawChannel($"nct6687d.temp.{idx}", "°C", 1, IsNct,
                b => b.TryReadSuperioRaw(Temp, (uint)idx, out uint raw, out SmbusStatus st)
                    ? new RawReading(true, SensorDecode.NctTempC(raw), "Ok") : new RawReading(false, 0, st.ToString())));
        }
        for (int i = 0; i < 8; i++)
        {
            int idx = i;
            list.Add(new RawChannel($"nct6687d.fan.{idx}", "RPM", 0, IsNct,
                b => b.TryReadSuperioRaw(Fan, (uint)idx, out uint raw, out SmbusStatus st)
                    ? new RawReading(true, SensorDecode.NctFanRpm(raw), "Ok") : new RawReading(false, 0, st.ToString())));
        }
        for (int i = 0; i < 15; i++)
        {
            int idx = i;
            list.Add(new RawChannel($"nct6687d.volt.{idx}", "V", 3, IsNct,
                b => b.TryReadSuperioRaw(Voltage, (uint)idx, out uint raw, out SmbusStatus st)
                    ? new RawReading(true, SensorDecode.NctVoltageMv(raw) / 1000.0, "Ok") : new RawReading(false, 0, st.ToString())));
        }

        /*-- NCT6775 "classic" family (ASUS/ASRock/Gigabyte/EVGA boards). Present only on a
             modern-group chip (docs/SUPERIO-NCT6775-FAMILY.md). Same {kind,index} IOCTL;
             the kernel's bank-select backend bakes the registers. Temps/fans reuse the NCT
             decode (identical packing); voltages are a single ADC byte at 8 mV/LSB. --*/
        for (int i = 0; i < 6; i++)
        {
            int idx = i;
            list.Add(new RawChannel($"nct6775.temp.{idx}", "°C", 1, IsNct6775,
                b => b.TryReadSuperioRaw(Temp, (uint)idx, out uint raw, out SmbusStatus st)
                    ? new RawReading(true, SensorDecode.NctTempC(raw), "Ok") : new RawReading(false, 0, st.ToString())));
        }
        for (int i = 0; i < 7; i++)
        {
            int idx = i;
            list.Add(new RawChannel($"nct6775.fan.{idx}", "RPM", 0, IsNct6775,
                b => b.TryReadSuperioRaw(Fan, (uint)idx, out uint raw, out SmbusStatus st)
                    ? new RawReading(true, SensorDecode.NctFanRpm(raw), "Ok") : new RawReading(false, 0, st.ToString())));
        }
        for (int i = 0; i < 16; i++)
        {
            int idx = i;
            list.Add(new RawChannel($"nct6775.volt.{idx}", "V", 3, IsNct6775,
                b => b.TryReadSuperioRaw(Voltage, (uint)idx, out uint raw, out SmbusStatus st)
                    ? new RawReading(true, SensorDecode.Nct6775VoltageMv(raw) / 1000.0, "Ok") : new RawReading(false, 0, st.ToString())));
        }

        /*-- DIMM thermal sensors (JC42 over SMBus). Present only at populated slots. --*/
        for (int i = 0; i < 8; i++)
        {
            int idx = i;
            list.Add(new RawChannel($"dimm.{idx}", "°C", 1,
                b => b.DimmTempPresent(idx),
                b => b.TryReadDimmTempRaw(idx, out uint raw, out SmbusStatus st)
                    ? new RawReading(true, SensorDecode.Jc42TempC(raw), "Ok") : new RawReading(false, 0, st.ToString())));
        }

        return list;
    }
}
