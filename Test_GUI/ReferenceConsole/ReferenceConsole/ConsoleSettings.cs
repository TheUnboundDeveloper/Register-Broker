using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Broker.Client;
using ReferenceConsole.Effects;

namespace ReferenceConsole;

/*---------------------------------------------------------------------------*\
| ConsoleSettings                                                            |
|                                                                            |
|   Persisted UI state for the reference console. Written to a small JSON    |
|   file under %APPDATA% so the console comes back the way you left it: the  |
|   global render knobs (FPS / sensor cadence / poll interval) plus, per RGB |
|   device, the assigned effect, every tunable parameter value, its drive    |
|   state, gradient stops, and any hand-painted per-LED colours.             |
|                                                                            |
|   This is pure UI convenience -- it never stores addresses, scopes, or     |
|   anything the broker cares about; the broker remains the only authority   |
|   over what hardware can be touched.                                        |
\*---------------------------------------------------------------------------*/
public sealed class ConsoleSettings
{
    // --- global render knobs (mirror the RGB-tab / Sensors-tab controls) ----
    public int Fps { get; set; } = 20;
    public int SensorRefreshMs { get; set; } = 750;
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>When true, minimizing hides the window to a system-tray icon instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Launch this console automatically when the user signs in (HKCU Run key — non-admin).</summary>
    public bool AutoStartGui { get; set; }

    /// <summary>
    /// User preference: the broker Windows services should start with Windows. Persisted regardless
    /// of whether it could be applied (setting a service start type needs administrator rights); the
    /// console applies it best-effort and an elevated install/run can honour it later.
    /// </summary>
    public bool AutoStartServices { get; set; }

    /// <summary>
    /// The window's last Normal-state placement [x, y, w, h] (screen pixels for x/y, DIPs for w/h),
    /// so the console reopens at the size/spot you left it. Null until the window has been shown once.
    /// </summary>
    public double[]? WindowBounds { get; set; }

    /// <summary>True if the window was maximized when last closed — restored on next launch.</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>Dashboard box order (by box id) and the ids the user has removed from the dashboard.</summary>
    public List<string>? DashOrder { get; set; }
    public List<string>? DashHidden { get; set; }

    /// <summary>Sensor ids the user has added as standalone metric cards ("Add box" → a sensor).</summary>
    public List<string> DashSensorBoxes { get; set; } = new();

    /// <summary>Per-box size [w,h] (-1 = auto), free-placement positions [x,y], and the layout mode.</summary>
    public Dictionary<string, double[]>? DashSizes { get; set; }
    public Dictionary<string, double[]>? DashFree { get; set; }
    public string? DashLayoutMode { get; set; }

    /// <summary>Whether the dashboard cards are locked (frozen) against move/resize. Independent of
    /// <see cref="SettingsLocked"/> — the dashboard and settings locks are separate.
    /// LEGACY: pre-sections this was the built-in dashboard's lock; now migrated into
    /// <see cref="DashSections"/>[0]. Kept only so an older settings file migrates cleanly.</summary>
    public bool DashLocked { get; set; }

    /// <summary>
    /// All dashboard sections in sidebar order: the built-in "Dashboard" (Id "main") plus any the
    /// user added via "+ Add Section". Each carries its own card layout AND its own lock, so the
    /// sections are fully independent. Null on a pre-sections settings file; <see cref="EnsureSections"/>
    /// migrates the legacy single-dashboard fields into a "main" entry on first load.
    /// </summary>
    public List<DashSectionState>? DashSections { get; set; }

    /// <summary>Return the section list, migrating the legacy single-dashboard fields into a "main"
    /// entry the first time (so existing layouts survive the upgrade).</summary>
    public List<DashSectionState> EnsureSections()
    {
        if (DashSections is { Count: > 0 } && DashSections.Any(s => s.Id == "main"))
            return DashSections;
        DashSections ??= new();
        if (DashSections.All(s => s.Id != "main"))
            DashSections.Insert(0, new DashSectionState
            {
                Id = "main",
                Name = null,                       // the built-in section's title is fixed ("Dashboard")
                Hidden = DashHidden,
                SensorBoxes = DashSensorBoxes ?? new(),
                Sizes = DashSizes,
                Free = DashFree,
                LayoutMode = DashLayoutMode,
                Locked = DashLocked,
            });
        return DashSections;
    }

    /// <summary>
    /// Settings-page card layout: free-placement positions [x,y] and sizes [w,h] (-1 = auto) keyed by
    /// card id, plus whether the cards are locked (frozen) against move/resize.
    /// </summary>
    public Dictionary<string, double[]>? SettingsFree { get; set; }
    public Dictionary<string, double[]>? SettingsSizes { get; set; }
    public bool SettingsLocked { get; set; }

    /// <summary>Per-device effect configuration, keyed by the broker's device id.</summary>
    public Dictionary<string, DeviceSettings> Devices { get; set; } = new();

    /// <summary>
    /// Local-only display-name overrides, keyed by id (a sensor id, an RGB device id, or a
    /// dashboard box id). These rename what the console SHOWS; they are never sent to the
    /// broker and never change a driver/hardware name. An absent or empty entry means the
    /// broker-supplied name stands.
    /// </summary>
    public Dictionary<string, string> CustomNames { get; set; } = new();

    // ------------------------------------------------------------------------
    //  Load / save
    // ------------------------------------------------------------------------
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>%APPDATA%\RegisterBroker\ReferenceConsole\settings.json</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RegisterBroker", "ReferenceConsole", "settings.json");

    public static ConsoleSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                if (JsonSerializer.Deserialize<ConsoleSettings>(json, JsonOptions) is { } s)
                    return s;
            }
        }
        catch { /* corrupt or unreadable -> fall back to defaults */ }
        return new ConsoleSettings();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, JsonOptions);

        // Write to a sibling temp file, then atomically swap it in. A crash or power loss mid-write
        // leaves the previous good settings.json intact instead of a half-written (corrupt) file that
        // Load() would reject -> silently discarding every saved layout/name/effect.
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
        else File.Move(tmp, FilePath);
    }
}

/// <summary>One dashboard section's persisted layout + lock. The built-in dashboard is Id "main"
/// (Name null → shown as "Dashboard"); user sections get a generated id and an editable name.</summary>
public sealed class DashSectionState
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public List<string>? Hidden { get; set; }                 // box ids hidden (built-in panels)
    public List<string> SensorBoxes { get; set; } = new();    // raw sensor ids added as metric cards
    public Dictionary<string, double[]>? Sizes { get; set; }  // box id -> [w,h] (-1 = auto)
    public Dictionary<string, double[]>? Free { get; set; }   // box id -> [x,y] canvas position
    public string? LayoutMode { get; set; }                   // "grid" | "free"
    public bool Locked { get; set; }                          // this section's own lock (independent)
}

/// <summary>One RGB device's saved effect configuration.</summary>
public sealed class DeviceSettings
{
    public string Effect { get; set; } = "(none)";
    public bool Drive { get; set; }
    public List<ParamState> Params { get; set; } = new();

    /// <summary>Gradient stops, for effects that expose them (Temperature).</summary>
    public List<StopState>? Stops { get; set; }

    /// <summary>Hand-painted per-LED colours (hex), for the Manual effect.</summary>
    public List<string>? ManualLeds { get; set; }

    /// <summary>Snapshot an effect's full tunable state for persistence.</summary>
    public static DeviceSettings Capture(IEffect eff, bool drive, int ledCount)
    {
        var ds = new DeviceSettings { Effect = eff.Name, Drive = drive };
        foreach (var p in eff.Parameters)
        {
            var ps = new ParamState { Key = p.Key };
            switch (p.Kind)
            {
                case ParamKind.Slider: ps.Num = p.Num; break;
                case ParamKind.Color: ps.Color = p.Hex; break;
                case ParamKind.Toggle: ps.Flag = p.Flag; break;
                case ParamKind.Choice: ps.Choice = p.SelectedChoice; break;
            }
            ds.Params.Add(ps);
        }
        if (eff is IGradientEffect g)
            ds.Stops = g.Stops.Select(s => new StopState { Temp = s.Temp, Color = s.Color.ToHex() }).ToList();
        if (eff is ManualEffect m && ledCount > 0)
        {
            ds.ManualLeds = new List<string>(ledCount);
            for (int i = 0; i < ledCount; i++) ds.ManualLeds.Add(m.Get(i).ToHex());
        }
        return ds;
    }

    /// <summary>Push the saved state back onto a freshly created effect of the same type.</summary>
    public void ApplyTo(IEffect eff, int ledCount)
    {
        var byKey = eff.Parameters.ToDictionary(p => p.Key);
        foreach (var ps in Params)
        {
            if (!byKey.TryGetValue(ps.Key, out var p)) continue;
            switch (p.Kind)
            {
                case ParamKind.Slider: if (ps.Num is { } n) p.Num = n; break;
                case ParamKind.Color: if (ps.Color is { } c) p.Hex = c; break;
                case ParamKind.Toggle: if (ps.Flag is { } f) p.Flag = f; break;
                case ParamKind.Choice:
                    // Match by the saved choice string so it survives a changed sensor list.
                    if (ps.Choice is { } sel)
                        for (int i = 0; i < p.Choices.Count; i++)
                            if (p.Choices[i] == sel) { p.ChoiceIndex = i; break; }
                    break;
            }
        }
        if (eff is IGradientEffect g && Stops is { Count: > 0 })
            g.SetStops(Stops.Select(s =>
            {
                RgbColor.TryParseHex(s.Color, out var col);
                return new GradientStop(s.Temp, col);
            }).ToList());
        if (eff is ManualEffect m && ManualLeds is { Count: > 0 } && ledCount > 0)
        {
            m.Resize(ledCount);
            for (int i = 0; i < ManualLeds.Count; i++)
                if (RgbColor.TryParseHex(ManualLeds[i], out var col)) m.SetLed(i, col);
        }
    }
}

/// <summary>One persisted parameter value. Only the field matching the param's kind is set.</summary>
public sealed class ParamState
{
    public string Key { get; set; } = "";
    public double? Num { get; set; }
    public string? Color { get; set; }
    public bool? Flag { get; set; }
    public string? Choice { get; set; }
}

/// <summary>One persisted gradient stop (temperature → colour).</summary>
public sealed class StopState
{
    public double Temp { get; set; }
    public string Color { get; set; } = "000000";
}

/*---------------------------------------------------------------------------*\
| LocalNames                                                                 |
|                                                                            |
|   The console's local-only naming layer. Bindable row models (SensorRow,  |
|   RgbRow) and the dashboard cards all resolve their display name through   |
|   here, so a single override dictionary -- loaded from / saved to the      |
|   settings file -- renames a sensor, RGB device, or card everywhere it     |
|   shows. This is presentation only: nothing here ever reaches the broker   |
|   or a hardware/driver name.                                                |
\*---------------------------------------------------------------------------*/
public static class LocalNames
{
    private static Dictionary<string, string> _map = new();

    /// <summary>Point the resolver at the loaded settings' dictionary (called once at startup).</summary>
    public static void Use(Dictionary<string, string> map) => _map = map;

    /// <summary>The local override for <paramref name="id"/>, or null if the broker name should stand.</summary>
    public static string? Get(string id)
        => _map.TryGetValue(id, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>Resolve a display name: the local override if set, else the broker-supplied fallback.</summary>
    public static string Resolve(string id, string fallback) => Get(id) ?? fallback;
}
