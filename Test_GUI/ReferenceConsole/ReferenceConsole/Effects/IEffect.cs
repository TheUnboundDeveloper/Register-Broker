using System;
using System.Collections.Generic;
using Broker.Client;

namespace ReferenceConsole.Effects;

/// <summary>Snapshot of audio analysis for one frame. Bands are normalized 0..1, low→high frequency.</summary>
public readonly struct AudioFrame
{
    public readonly float[] Bands;
    public readonly float Level;   // overall normalized loudness 0..1
    public AudioFrame(float[] bands, float level) { Bands = bands; Level = level; }
    public static readonly AudioFrame Silent = new(new float[16], 0f);
}

/// <summary>Everything an effect can read while rendering one frame.</summary>
public readonly struct RenderContext
{
    public readonly double Time;     // seconds since engine start
    public readonly double Dt;       // seconds since previous frame
    public readonly Func<string, double?> Sensor;  // logical id -> latest value (or null)
    public readonly AudioFrame Audio;

    public RenderContext(double time, double dt, Func<string, double?> sensor, AudioFrame audio)
    {
        Time = time; Dt = dt; Sensor = sensor; Audio = audio;
    }
}

/*---------------------------------------------------------------------------*\
| IEffect                                                                    |
|                                                                            |
|   A client-side renderer. It owns its tunable parameters and fills a       |
|   per-LED buffer each frame; the engine streams that buffer to the broker  |
|   via rgb.set colors=[…]. The broker is pure transport -- all the colour   |
|   maths (temperature mapping, animation, audio reaction) lives here.       |
\*---------------------------------------------------------------------------*/
public interface IEffect
{
    string Name { get; }

    /// <summary>Tunable knobs, surfaced generically by the UI.</summary>
    IReadOnlyList<EffectParam> Parameters { get; }

    /// <summary>True if this effect produces a new frame every tick (vs. only on input change).</summary>
    bool IsAnimated { get; }

    /// <summary>Fill <paramref name="leds"/> (length = device LED count) for this frame.</summary>
    void Render(in RenderContext ctx, RgbColor[] leds);
}

/// <summary>Implemented by effects that want the live sensor-id list injected (e.g. temperature).</summary>
public interface ISensorAware
{
    void SetSensorIds(IReadOnlyList<string> ids);
}

/// <summary>One colour stop on a value→colour gradient (e.g. 60° → orange). Mutable for live editing.</summary>
public sealed class GradientStop
{
    public double Temp { get; set; }
    public RgbColor Color { get; set; }
    public GradientStop(double temp, RgbColor color) { Temp = temp; Color = color; }
}

/// <summary>An effect with an editable list of gradient stops (so the UI can add/remove definitions).</summary>
public interface IGradientEffect
{
    IReadOnlyList<GradientStop> Stops { get; }
    GradientStop AddStop();
    void RemoveStop(GradientStop stop);
    void SetStops(IReadOnlyList<GradientStop> stops);
}
