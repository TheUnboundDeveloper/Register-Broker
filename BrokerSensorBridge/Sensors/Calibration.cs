using System.Text.Json;
using Microsoft.Win32;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| Calibration — board identity, the data schema, and the resolved store.      |
|                                                                            |
|   Board calibration is DATA, not code: a table of labels + scales keyed by  |
|   the board's DMI identity. It can only rename / rescale / hide a channel    |
|   the chip backend already produced — there is NO address/register field,   |
|   so a wrong or hostile calibration file cannot reach hardware (see          |
|   CALIBRATION-AND-REGISTRY-PLAN.md, "the one invariant"). The trusted        |
|   register code stays compiled in the driver + SensorDecode.               |
\*---------------------------------------------------------------------------*/

/// <summary>The board's DMI identity (from the BIOS registry key — no extra dependency).</summary>
internal sealed record BoardIdentity(string Manufacturer, string Product)
{
    public static BoardIdentity Detect()
    {
        try
        {
            using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            string mfr = k?.GetValue("BaseBoardManufacturer") as string ?? "";
            string product = k?.GetValue("BaseBoardProduct") as string ?? "";
            return new BoardIdentity(mfr.Trim(), product.Trim());
        }
        catch { return new BoardIdentity("", ""); }
    }

    public override string ToString() => $"{Manufacturer} / {Product}";
}

/*-- JSON DTOs (System.Text.Json, case-insensitive). --*/
internal sealed class CalibrationData
{
    public int Schema { get; set; }
    public List<ChannelCal>? Defaults { get; set; }
    public List<BoardCal>? Boards { get; set; }
}
internal sealed class BoardCal
{
    public MatchCal? Match { get; set; }
    public List<ChannelCal>? Channels { get; set; }
}
internal sealed class MatchCal
{
    public string? Manufacturer { get; set; }
    public string? Product { get; set; }
}
internal sealed class ChannelCal
{
    public string? RawId { get; set; }
    public string? Label { get; set; }
    public string? Unit { get; set; }
    public double? Scale { get; set; }
    public double? Offset { get; set; }
    public bool? Hidden { get; set; }
}

/// <summary>A resolved per-channel override (label/scale/offset/hidden) after board matching.</summary>
internal readonly record struct ChannelOverride(string? Label, string? Unit, double Scale, double Offset, bool Hidden)
{
    public static readonly ChannelOverride None = new(null, null, 1.0, 0.0, false);
}

internal sealed class CalibrationStore
{
    private readonly Dictionary<string, ChannelOverride> _overrides;
    private readonly Dictionary<string, string> _aliases;

    private CalibrationStore(Dictionary<string, ChannelOverride> overrides, Dictionary<string, string> aliases)
    {
        _overrides = overrides;
        _aliases = aliases;
    }

    /// <summary>Aliases-only store (no board labels): used as the default before Configure / in tests.</summary>
    public static CalibrationStore Builtin =>
        new(new Dictionary<string, ChannelOverride>(StringComparer.OrdinalIgnoreCase), BuiltinAliases());

    /// <summary>Override for a raw id (label/scale/...). Returns None (passthrough) if uncalibrated.</summary>
    public ChannelOverride Resolve(string rawId) =>
        _overrides.TryGetValue(rawId, out var o) ? o : ChannelOverride.None;

    /// <summary>Map a legacy/semantic id (e.g. board.12v.volt) to its stable raw id, or null.</summary>
    public string? ResolveAlias(string id) => _aliases.TryGetValue(id, out var raw) ? raw : null;

    /// <summary>
    /// Load calibration from one or more JSON files (in order) and resolve against this board's
    /// identity. Always starts from the built-in alias map (structural id↔rawId compat is not board
    /// data). Files are LAYERED: later files override earlier ones per channel — so the baked default
    /// is loaded first and a user file last wins. A missing/invalid file is skipped (never throws).
    /// </summary>
    public static CalibrationStore Load(BoardIdentity board, Action<string> log, params string[] paths)
    {
        var overrides = new Dictionary<string, ChannelOverride>(StringComparer.OrdinalIgnoreCase);
        var aliases = BuiltinAliases();
        bool matchedAny = false;

        foreach (string path in paths)
        {
            CalibrationData? data = TryParse(path, log);
            if (data == null) continue;
            ApplyChannels(overrides, data.Defaults);                 // generic defaults first
            BoardCal? match = MatchBoard(data.Boards, board);
            if (match != null)
            {
                log($"[calib] board '{board}' matched in {Path.GetFileName(path)}.");
                ApplyChannels(overrides, match.Channels);            // board-specific overrides on top
                matchedAny = true;
            }
        }

        if (!matchedAny)
            log($"[calib] board '{board}' has no calibration entry; using generic defaults. Add one keyed by this identity to label its rails.");

        return new CalibrationStore(overrides, aliases);
    }

    /// <summary>Back-compat single-file overload (used by tests).</summary>
    public static CalibrationStore Load(string path, BoardIdentity board, Action<string> log) => Load(board, log, path);

    private static CalibrationData? TryParse(string path, Action<string> log)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            CalibrationData? data = JsonSerializer.Deserialize<CalibrationData>(File.ReadAllText(path), opts);
            log($"[calib] loaded {Path.GetFileName(path)}.");
            return data;
        }
        catch (Exception ex) { log($"[calib] failed to parse {path}: {ex.Message}; skipping."); return null; }
    }

    private static BoardCal? MatchBoard(List<BoardCal>? boards, BoardIdentity board)
    {
        if (boards == null) return null;
        foreach (BoardCal b in boards)
        {
            string? m = b.Match?.Manufacturer, p = b.Match?.Product;
            // A field that's null/empty in the entry is a wildcard; otherwise case-insensitive equality.
            bool mfrOk = string.IsNullOrEmpty(m) || string.Equals(m, board.Manufacturer, StringComparison.OrdinalIgnoreCase);
            bool prodOk = string.IsNullOrEmpty(p) || string.Equals(p, board.Product, StringComparison.OrdinalIgnoreCase);
            if (mfrOk && prodOk) return b;
        }
        return null;
    }

    private static void ApplyChannels(Dictionary<string, ChannelOverride> dst, List<ChannelCal>? channels)
    {
        if (channels == null) return;
        foreach (ChannelCal c in channels)
        {
            if (string.IsNullOrEmpty(c.RawId)) continue;             // a channel with no rawId is ignored (never grants access)
            ChannelOverride prev = dst.TryGetValue(c.RawId, out var o) ? o : ChannelOverride.None;
            dst[c.RawId] = new ChannelOverride(
                Label: c.Label ?? prev.Label,
                Unit: c.Unit ?? prev.Unit,
                Scale: c.Scale ?? prev.Scale,
                Offset: c.Offset ?? prev.Offset,
                Hidden: c.Hidden ?? prev.Hidden);
        }
    }

    /*-----------------------------------------------------------------------*\
    | Built-in alias map: legacy/semantic ids -> stable raw ids. Structural   |
    | (must always exist so saved configs and tests keep resolving), so it    |
    | lives in code, not the data file. Calibration only sets labels/scales.  |
    \*-----------------------------------------------------------------------*/
    private static Dictionary<string, string> BuiltinAliases()
    {
        var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cpu.temp"] = "smu.cpu.temp",
            ["board.cpu.temp"] = "nct6687d.temp.0",
            ["board.system.temp"] = "nct6687d.temp.1",
            ["board.vrm.temp"] = "nct6687d.temp.2",
            ["board.chipset.temp"] = "nct6687d.temp.3",
            ["board.socket.temp"] = "nct6687d.temp.4",
            ["board.pcie.temp"] = "nct6687d.temp.5",
            ["board.12v.volt"] = "nct6687d.volt.0",
            ["board.5v.volt"] = "nct6687d.volt.1",
            ["board.soc.volt"] = "nct6687d.volt.2",
            ["board.dram.volt"] = "nct6687d.volt.3",
            ["board.vcore.volt"] = "nct6687d.volt.4",
            ["board.volt5"] = "nct6687d.volt.5",
            ["board.volt6"] = "nct6687d.volt.6",
            ["board.volt7"] = "nct6687d.volt.7",
            ["board.3v3.volt"] = "nct6687d.volt.8",
            ["board.cpu1p8.volt"] = "nct6687d.volt.9",
            ["board.volt10"] = "nct6687d.volt.10",
            ["board.3vsb.volt"] = "nct6687d.volt.11",
            ["board.avsb.volt"] = "nct6687d.volt.12",
            ["board.vtt.volt"] = "nct6687d.volt.13",
            ["board.vbat.volt"] = "nct6687d.volt.14",
        };
        for (int i = 0; i < 8; i++) a[$"cpu.ccd{i}.temp"] = $"smu.ccd.{i}";
        for (int i = 0; i < 8; i++) a[$"fan{i}"] = $"nct6687d.fan.{i}";
        for (int i = 0; i < 8; i++) a[$"dimm{i}.temp"] = $"dimm.{i}";
        // The board.ite.* legacy aliases left with the retired Gigabyte backend (docs/GIGABYTE-SUPPORT.md).
        return a;
    }
}
