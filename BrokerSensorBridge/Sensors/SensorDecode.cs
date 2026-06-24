namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| SensorDecode                                                              |
|                                                                            |
|   Pure per-chip raw->engineering-unit decoders, ported from the Linux      |
|   hwmon drivers (k10temp, nct6687d, jc42). These convert the RAW           |
|   register bytes the kernel returns into a BASE value in the chip's native  |
|   unit (°C / RPM / volts-at-pin). Board calibration (label + scale) is      |
|   applied on TOP of this in SensorCatalog; the decoders themselves are      |
|   board-independent and carry no labels.                                   |
\*---------------------------------------------------------------------------*/
internal static class SensorDecode
{
    /// <summary>
    /// AMD Family 17h/19h reported-temperature decode (k10temp). Tctl = (raw>>21)·0.125,
    /// minus 49 °C when the range-select bit is set; Tdie = Tctl − offset (0 on most desktop Ryzen).
    /// </summary>
    public static double AmdCpuTctlC(uint raw, double tctlOffset = 0.0)
    {
        double t = ((raw >> 21) & 0x7FF) * 0.125;
        if ((raw & (1u << 19)) != 0) t -= 49.0;
        return t - tctlOffset;
    }

    /// <summary>AMD per-CCD die temperature (k10temp ZEN_CCD_TEMP): °C = (raw &amp; 0x7FF)·0.125 − 49.</summary>
    public static double AmdCcdTempC(uint raw) => (raw & 0x7FF) * 0.125 - 49.0;

    /// <summary>k10temp CCD valid bit (BIT 11). The catalog checks this before exposing a CCD.</summary>
    public const uint AmdCcdValid = 0x800;

    /// <summary>
    /// AMD SVI2 telemetry-plane voltage decode (zenpower plane_to_vcc): the voltage code is
    /// byte [23:16] of the plane register; V = 1.550 − 0.00625·code. A fully-gated plane reads
    /// a high code → small/negative voltage; we clamp at 0 so an idle rail never reports negative.
    /// </summary>
    public static double AmdSviVoltageV(uint raw)
    {
        double v = 1.550 - 0.00625 * ((raw >> 16) & 0xFF);
        return v < 0.0 ? 0.0 : v;
    }

    /// <summary>NCT6687D temperature: low byte = signed °C, high byte bit 7 = +0.5 °C (raw = value | half&lt;&lt;8).</summary>
    public static double NctTempC(uint raw)
    {
        sbyte value = (sbyte)(raw & 0xFF);
        int half = (int)((raw >> 8) >> 7) & 1;
        return value + 0.5 * half;
    }

    /// <summary>NCT6687D fan tachometer: raw is the 16-bit RPM directly.</summary>
    public static int NctFanRpm(uint raw) => (int)(raw & 0xFFFF);

    /// <summary>
    /// NCT668x fan PWM duty as a percentage: the register is an 8-bit duty (0..255, nct6683
    /// NCT6683_REG_PWM = 0x160+i), so % = raw/255·100. READ-ONLY telemetry — this decodes the
    /// duty the chip is currently driving; the broker has no path to change it.
    /// </summary>
    public static double NctPwmPercent(uint raw) => (raw & 0xFF) / 255.0 * 100.0;

    /// <summary>
    /// NCT6687D voltage ADC reading in millivolts at the chip pin (nct6687d):
    /// mV = (highByte·16) + (lowByte>>4), where raw = (high&lt;&lt;8) | low. This is the PIN reading;
    /// the per-rail divider multiplier is the board calibration's scale, applied later.
    /// </summary>
    public static int NctVoltageMv(uint raw)
        => (int)((raw >> 8) & 0xFF) * 16 + ((int)(raw & 0xFF) >> 4);

    /// <summary>
    /// NCT6775-family voltage ADC reading (Linux nct6775 in_from_reg): one byte at 8 mV/LSB.
    /// This is the PIN reading; rails behind a board divider carry it as the calibration scale.
    /// </summary>
    public static int Nct6775VoltageMv(uint raw) => (int)(raw & 0xFF) * 8;

    /* The ITE IT87xx decoders left with the retired Gigabyte backend (design record:
       docs/GIGABYTE-SUPPORT.md) — restore them alongside SuperioIte.c if it returns. */

    /// <summary>
    /// JEDEC JC42.4 / TSE2004av DIMM thermal-sensor decode (Linux jc42): 0.0625 °C/LSB, 13-bit
    /// two's-complement in bits 12:0 (raw is the 16-bit reg packed MSB-first).
    /// </summary>
    public static double Jc42TempC(uint raw)
    {
        int reg = (int)(raw & 0x1FFF);
        if ((reg & 0x1000) != 0) reg -= 0x2000;
        return reg * 0.0625;
    }
}

/*---------------------------------------------------------------------------*\
| DecoderRegistry — decoder coverage by raw-id prefix                         |
|                                                                            |
|   Maps every raw-id prefix the ChannelRegistry owns to the proven source    |
|   its decode was ported from. The selftest walks every registered channel   |
|   and asserts its prefix is covered here — a chipset PR that adds channels  |
|   without declaring (and citing) a decoder fails the gate. The decode       |
|   functions themselves stay above (compile-time-bound in the channel        |
|   builders); this registry is the completeness/provenance check.            |
\*---------------------------------------------------------------------------*/
internal static class DecoderRegistry
{
    /// <summary>Raw-id prefix → the proven source the decode is ported from.</summary>
    public static readonly IReadOnlyDictionary<string, string> Sources = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["smu."]      = "Linux k10temp (AmdCpuTctlC / AmdCcdTempC) + zenpower SVI (AmdSviVoltageV)",
        ["nct6687d."] = "Linux nct6683 + Fred78290/nct6687d (NctTempC / NctFanRpm / NctVoltageMv / NctPwmPercent)",
        ["nct6775."]  = "Linux nct6775-core (NctTempC / NctFanRpm / Nct6775VoltageMv)",
        ["dimm."]     = "Linux jc42 (Jc42TempC)",
        ["gpu."]      = "AMD ADL PMLog (ADL2_New_QueryPMLogData_Get; AMD ADL SDK) — values returned in engineering units",
    };

    public static bool Covers(string rawId)
        => Sources.Keys.Any(p => rawId.StartsWith(p, StringComparison.Ordinal));

    /// <summary>First registered channel without decoder coverage, or null when complete.</summary>
    public static string? FirstUncovered(IEnumerable<RawChannel> channels)
        => channels.Select(c => c.RawId).FirstOrDefault(id => !Covers(id));
}
