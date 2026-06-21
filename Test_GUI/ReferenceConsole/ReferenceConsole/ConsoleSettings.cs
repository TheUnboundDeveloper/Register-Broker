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

    /// <summary>Per-device effect configuration, keyed by the broker's device id.</summary>
    public Dictionary<string, DeviceSettings> Devices { get; set; } = new();

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
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
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
