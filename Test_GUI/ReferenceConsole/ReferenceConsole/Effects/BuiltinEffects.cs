using System;
using System.Collections.Generic;
using System.Linq;
using Broker.Client;

namespace ReferenceConsole.Effects;

/*===========================================================================*\
| The built-in effect set. Each is a pure client-side renderer; the engine    |
| streams its output to the broker. All knobs are EffectParams so the UI can  |
| expose them generically and adjust them live.                               |
\*===========================================================================*/

/// <summary>Solid colour across the whole device.</summary>
public sealed class StaticEffect : IEffect
{
    private readonly EffectParam _color = EffectParam.Color("color", "Colour", "00AAFF");
    public string Name => "Static";
    public bool IsAnimated => false;
    public IReadOnlyList<EffectParam> Parameters => new[] { _color };
    public void Render(in RenderContext ctx, RgbColor[] leds)
    {
        var c = _color.Color_;
        for (int i = 0; i < leds.Length; i++) leds[i] = c;
    }
}

/// <summary>
/// Map a live sensor value (e.g. CPU temp) onto a multi-stop colour gradient. Any number of
/// temperature→colour stops can be added/removed; the value is interpolated between the two
/// stops that bracket it (clamped to the coldest/hottest stop outside the range).
/// </summary>
public sealed class TemperatureEffect : IEffect, ISensorAware, IGradientEffect
{
    private readonly EffectParam _sensor = EffectParam.Choice("sensor", "Sensor", new[] { "smu.cpu.temp" });
    private readonly EffectParam _bright = EffectParam.Slider("bright", "Brightness", 0, 1, 1, 0.01);
    private readonly EffectParam _fade = EffectParam.Slider("fade", "Fade (s)", 0, 2, 0.35, 0.01);

    // Swapped atomically on add/remove so the render thread always sees a consistent array.
    private GradientStop[] _stops;

    // Eased on-screen colour so it glides between stops instead of snapping (sensor
    // updates are coarse and slow — without this the strip switches like a relay).
    private RgbColor _shown = RgbColor.Black;
    private bool _primed;

    public TemperatureEffect()
    {
        RgbColor.TryParseHex("0066FF", out var cold);
        RgbColor.TryParseHex("FF2000", out var hot);
        _stops = new[] { new GradientStop(30, cold), new GradientStop(85, hot) };
    }

    public string Name => "Temperature";
    public bool IsAnimated => true; // value drifts; re-render each tick
    public IReadOnlyList<EffectParam> Parameters => new[] { _sensor, _bright, _fade };
    public IReadOnlyList<GradientStop> Stops => _stops;

    public void SetSensorIds(IReadOnlyList<string> ids)
        => _sensor.SetChoices(ids.Count > 0 ? ids : new[] { "smu.cpu.temp" }, _sensor.SelectedChoice);

    public GradientStop AddStop()
    {
        var sorted = _stops.OrderBy(s => s.Temp).ToArray();
        // Place the new stop in the middle of the widest gap (or just above the top stop).
        double temp = 50;
        var midColor = RgbColor.FromHsv(120, 1, 1);
        if (sorted.Length == 1) temp = Math.Min(120, sorted[0].Temp + 10);
        else if (sorted.Length >= 2)
        {
            double bestGap = -1, mid = (sorted[0].Temp + sorted[^1].Temp) / 2;
            for (int i = 0; i < sorted.Length - 1; i++)
            {
                double gap = sorted[i + 1].Temp - sorted[i].Temp;
                if (gap > bestGap) { bestGap = gap; mid = (sorted[i].Temp + sorted[i + 1].Temp) / 2; }
            }
            temp = mid;
        }
        var stop = new GradientStop(Math.Round(temp), midColor);
        _stops = _stops.Append(stop).ToArray();
        return stop;
    }

    public void RemoveStop(GradientStop stop)
    {
        if (_stops.Length <= 1) return;   // always keep at least one
        _stops = _stops.Where(s => !ReferenceEquals(s, stop)).ToArray();
    }

    public void SetStops(IReadOnlyList<GradientStop> stops)
    {
        var copy = stops.Select(s => new GradientStop(s.Temp, s.Color)).ToArray();
        if (copy.Length > 0) _stops = copy;
    }

    public void Render(in RenderContext ctx, RgbColor[] leds)
    {
        var stops = _stops;                       // snapshot the reference
        RgbColor col = RgbColor.Black;
        if (stops.Length > 0)
        {
            var sorted = stops.OrderBy(s => s.Temp).ToArray();
            double v = ctx.Sensor(_sensor.SelectedChoice ?? "smu.cpu.temp") ?? sorted[0].Temp;
            if (v <= sorted[0].Temp) col = sorted[0].Color;
            else if (v >= sorted[^1].Temp) col = sorted[^1].Color;
            else
            {
                for (int i = 0; i < sorted.Length - 1; i++)
                {
                    if (v >= sorted[i].Temp && v <= sorted[i + 1].Temp)
                    {
                        double span = Math.Max(0.001, sorted[i + 1].Temp - sorted[i].Temp);
                        col = RgbColor.Lerp(sorted[i].Color, sorted[i + 1].Color, (v - sorted[i].Temp) / span);
                        break;
                    }
                }
            }
            col = col.Scale(_bright.Num);
        }

        // Temporal fade toward the target colour: alpha = 1 - e^(-dt/tau). A small
        // tau (default 0.35 s) makes the change soft but quick. tau = 0 snaps.
        double tau = _fade.Num;
        if (!_primed || tau <= 0.0001) { _shown = col; _primed = true; }
        else
        {
            double alpha = 1 - Math.Exp(-Math.Max(0, ctx.Dt) / tau);
            _shown = RgbColor.Lerp(_shown, col, alpha);
        }

        for (int i = 0; i < leds.Length; i++) leds[i] = _shown;
    }
}

/// <summary>Animated hue cycle, optionally spread across the strip.</summary>
public sealed class RainbowEffect : IEffect
{
    private readonly EffectParam _speed = EffectParam.Slider("speed", "Speed (cyc/s)", 0, 3, 0.3, 0.01);
    private readonly EffectParam _spread = EffectParam.Slider("spread", "Spread (°)", 0, 720, 360, 5);
    private readonly EffectParam _sat = EffectParam.Slider("sat", "Saturation", 0, 1, 1, 0.01);
    private readonly EffectParam _bright = EffectParam.Slider("bright", "Brightness", 0, 1, 1, 0.01);

    public string Name => "Rainbow";
    public bool IsAnimated => true;
    public IReadOnlyList<EffectParam> Parameters => new[] { _speed, _spread, _sat, _bright };

    public void Render(in RenderContext ctx, RgbColor[] leds)
    {
        double baseHue = ctx.Time * _speed.Num * 360.0;
        int n = Math.Max(1, leds.Length);
        for (int i = 0; i < leds.Length; i++)
        {
            double hue = baseHue + (i / (double)n) * _spread.Num;
            leds[i] = RgbColor.FromHsv(hue, _sat.Num, _bright.Num);
        }
    }
}

/// <summary>Breathing pulse of one colour.</summary>
public sealed class BreathingEffect : IEffect
{
    private readonly EffectParam _color = EffectParam.Color("color", "Colour", "FF00AA");
    private readonly EffectParam _speed = EffectParam.Slider("speed", "Speed (Hz)", 0.05, 3, 0.4, 0.01);
    private readonly EffectParam _min = EffectParam.Slider("min", "Min brightness", 0, 1, 0.05, 0.01);
    private readonly EffectParam _max = EffectParam.Slider("max", "Max brightness", 0, 1, 1, 0.01);

    public string Name => "Breathing";
    public bool IsAnimated => true;
    public IReadOnlyList<EffectParam> Parameters => new[] { _color, _speed, _min, _max };

    public void Render(in RenderContext ctx, RgbColor[] leds)
    {
        double phase = (Math.Sin(2 * Math.PI * _speed.Num * ctx.Time) + 1) / 2.0;
        double b = _min.Num + (_max.Num - _min.Num) * phase;
        var c = _color.Color_.Scale(b);
        for (int i = 0; i < leds.Length; i++) leds[i] = c;
    }
}

/// <summary>A moving lit head with a fading tail (chase).</summary>
public sealed class CometEffect : IEffect
{
    private readonly EffectParam _color = EffectParam.Color("color", "Colour", "00FFAA");
    private readonly EffectParam _bg = EffectParam.Color("bg", "Background", "000000");
    private readonly EffectParam _speed = EffectParam.Slider("speed", "Speed (LED/s)", 1, 120, 24, 1);
    private readonly EffectParam _tail = EffectParam.Slider("tail", "Tail length", 1, 60, 8, 1);

    public string Name => "Comet";
    public bool IsAnimated => true;
    public IReadOnlyList<EffectParam> Parameters => new[] { _color, _bg, _speed, _tail };

    public void Render(in RenderContext ctx, RgbColor[] leds)
    {
        int n = Math.Max(1, leds.Length);
        double head = (ctx.Time * _speed.Num) % n;
        double tail = Math.Max(1, _tail.Num);
        for (int i = 0; i < leds.Length; i++)
        {
            double dist = head - i;
            if (dist < 0) dist += n;            // wrap so the tail trails behind the head
            double b = Math.Max(0, 1 - dist / tail);
            leds[i] = RgbColor.Lerp(_bg.Color_, _color.Color_, b);
        }
    }
}

/// <summary>Manual per-LED painting. Each LED holds an explicit colour set from the UI.</summary>
public sealed class ManualEffect : IEffect
{
    private readonly EffectParam _brush = EffectParam.Color("brush", "Brush colour", "FFFFFF");
    private RgbColor[] _leds = Array.Empty<RgbColor>();

    public string Name => "Manual per-LED";
    public bool IsAnimated => false;
    public IReadOnlyList<EffectParam> Parameters => new[] { _brush };

    public RgbColor Brush => _brush.Color_;

    public void Resize(int n)
    {
        if (_leds.Length == n) return;
        var next = new RgbColor[n];
        Array.Copy(_leds, next, Math.Min(_leds.Length, n));
        _leds = next;
    }

    public void SetLed(int index, RgbColor c) { if (index >= 0 && index < _leds.Length) _leds[index] = c; }
    public void Fill(RgbColor c) { for (int i = 0; i < _leds.Length; i++) _leds[i] = c; }
    public RgbColor Get(int index) => (index >= 0 && index < _leds.Length) ? _leds[index] : RgbColor.Black;

    public void Render(in RenderContext ctx, RgbColor[] leds)
    {
        Resize(leds.Length);
        for (int i = 0; i < leds.Length; i++) leds[i] = _leds[i];
    }
}

/// <summary>Random sparkles that ignite and fade over a background.</summary>
public sealed class TwinkleEffect : IEffect
{
    private readonly EffectParam _bg = EffectParam.Color("bg", "Background", "000010");
    private readonly EffectParam _color = EffectParam.Color("color", "Twinkle colour", "FFFFFF");
    private readonly EffectParam _density = EffectParam.Slider("density", "Density", 0, 1, 0.25, 0.01);
    private readonly EffectParam _fade = EffectParam.Slider("fade", "Fade (per s)", 0.5, 12, 3, 0.1);
    private readonly EffectParam _rainbow = EffectParam.Toggle("rainbow", "Random colours", false);

    private float[] _level = Array.Empty<float>();
    private RgbColor[] _tint = Array.Empty<RgbColor>();
    private readonly Random _rng = new();

    public string Name => "Twinkle";
    public bool IsAnimated => true;
    public IReadOnlyList<EffectParam> Parameters => new[] { _bg, _color, _density, _fade, _rainbow };

    public void Render(in RenderContext ctx, RgbColor[] leds)
    {
        int n = leds.Length;
        if (_level.Length != n) { _level = new float[n]; _tint = new RgbColor[n]; }
        double dt = Math.Clamp(ctx.Dt, 0, 0.1);
        double decay = _fade.Num * dt;
        double spawn = _density.Num * dt * 6.0;     // expected ignitions per LED this frame
        for (int i = 0; i < n; i++)
        {
            if (_level[i] <= 0 && _rng.NextDouble() < spawn)
            {
                _level[i] = 1f;
                _tint[i] = _rainbow.Flag ? RgbColor.FromHsv(_rng.NextDouble() * 360, 1, 1) : _color.Color_;
            }
            _level[i] = (float)Math.Max(0, _level[i] - decay);
            leds[i] = RgbColor.Lerp(_bg.Color_, _tint[i], _level[i]);
        }
    }
}

/// <summary>
/// Aurora — flowing curtains of light (my own creation). Layered drifting sine waves blend
/// three palette colours across the strip and modulate brightness, evoking the northern lights.
/// </summary>
public sealed class AuroraEffect : IEffect
{
    private readonly EffectParam _speed = EffectParam.Slider("speed", "Speed", 0, 2, 0.3, 0.01);
    private readonly EffectParam _scale = EffectParam.Slider("scale", "Wave scale", 0.5, 10, 3, 0.1);
    private readonly EffectParam _bright = EffectParam.Slider("bright", "Brightness", 0, 1, 1, 0.01);
    private readonly EffectParam _a = EffectParam.Color("a", "Colour A", "00FF66");
    private readonly EffectParam _b = EffectParam.Color("b", "Colour B", "0066FF");
    private readonly EffectParam _c = EffectParam.Color("c", "Colour C", "AA00FF");

    public string Name => "Aurora";
    public bool IsAnimated => true;
    public IReadOnlyList<EffectParam> Parameters => new[] { _speed, _scale, _bright, _a, _b, _c };

    public void Render(in RenderContext ctx, RgbColor[] leds)
    {
        int n = Math.Max(1, leds.Length);
        double t = ctx.Time * _speed.Num;
        for (int i = 0; i < leds.Length; i++)
        {
            double x = i / (double)n;
            double w1 = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (x * _scale.Num + t));
            double w2 = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (x * _scale.Num * 0.5 - t * 0.6) + 1.7);
            double w3 = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (x * _scale.Num * 0.3 + t * 0.8) + 3.1);
            var baseCol = RgbColor.Lerp(_a.Color_, _b.Color_, w1);
            var col = RgbColor.Lerp(baseCol, _c.Color_, w2 * 0.5);
            leds[i] = col.Scale((0.35 + 0.65 * w3) * _bright.Num);
        }
    }
}

/// <summary>
/// Audio-reactive spectrum. Loopback band energy → per-LED colour/brightness.
/// "Reactive factor" (gain) and "Smoothing" are live params — exactly the knobs
/// you tweaked last night, now first-class and adjustable while it runs.
/// </summary>
public sealed class AudioSpectrumEffect : IEffect
{
    private readonly EffectParam _mode = EffectParam.Choice("mode", "Mode", new[] { "Spectrum", "Level" });
    private readonly EffectParam _gain = EffectParam.Slider("gain", "Reactive factor", 0, 8, 2.0, 0.05);
    private readonly EffectParam _smooth = EffectParam.Slider("smooth", "Smoothing", 0, 0.95, 0.5, 0.01);
    private readonly EffectParam _floor = EffectParam.Slider("floor", "Noise floor", 0, 0.5, 0.02, 0.005);
    private readonly EffectParam _low = EffectParam.Color("low", "Low colour", "0000FF");
    private readonly EffectParam _high = EffectParam.Color("high", "High colour", "FF0000");

    private float[] _ema = Array.Empty<float>();

    public string Name => "Audio Spectrum";
    public bool IsAnimated => true;
    public IReadOnlyList<EffectParam> Parameters => new[] { _mode, _gain, _smooth, _floor, _low, _high };

    public void Render(in RenderContext ctx, RgbColor[] leds)
    {
        var bands = ctx.Audio.Bands;
        int nb = bands.Length;
        if (_ema.Length != nb) _ema = new float[nb];
        double a = 1 - _smooth.Num;   // smoothing: higher = slower response

        for (int i = 0; i < nb; i++)
            _ema[i] = (float)(_ema[i] + a * (bands[i] - _ema[i]));

        bool level = _mode.SelectedChoice == "Level";

        // Level mode: derive overall loudness from the (perceptually-compressed,
        // smoothed) bands rather than the raw capture level. The raw level is
        // near-saturated and, once the gain knob is applied, pegs the whole strip
        // at full almost always. RMS across the bands keeps the same dynamic range
        // as a single Spectrum band, so the gain/floor/smoothing knobs behave the
        // same way in both modes.
        float lvl = 0;
        if (level)
        {
            double s = 0;
            for (int i = 0; i < nb; i++) s += _ema[i] * _ema[i];
            lvl = (float)Math.Sqrt(s / Math.Max(1, nb));
        }

        int n = Math.Max(1, leds.Length);
        for (int i = 0; i < leds.Length; i++)
        {
            double frac = i / (double)n;             // position along strip → colour
            double energy = level ? lvl : _ema[Math.Min(nb - 1, (int)(frac * nb))];

            energy = Math.Clamp(energy * _gain.Num, 0, 1);
            if (energy < _floor.Num) { leds[i] = RgbColor.Black; continue; }
            leds[i] = RgbColor.Lerp(_low.Color_, _high.Color_, frac).Scale(energy);
        }
    }
}
