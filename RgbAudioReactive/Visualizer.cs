namespace RgbAudioReactive;

/*---------------------------------------------------------------------------*\
| Visualizer                                                                 |
|                                                                            |
|   Pure mapping: AnalysisState -> per-LED RRGGBB strings for a zone of N     |
|   LEDs. No I/O, no audio — just the look. Two modes:                         |
|                                                                            |
|     Level    — a VU meter. LEDs fill from one end in proportion to overall  |
|                loudness, coloured green -> yellow -> red as the bar grows.   |
|                Reads only AnalysisState.Level. Great on any LED count.       |
|                                                                            |
|     Spectrum — a music visualiser. The frequency bands are spread across     |
|                the strip; each LED's HUE is its position on a rainbow and    |
|                its BRIGHTNESS is that band's magnitude. Bass on the left,    |
|                treble on the right. Best on multi-LED zones.                  |
\*---------------------------------------------------------------------------*/
internal enum VisualMode { Level, Spectrum }

internal sealed class Visualizer
{
    private readonly VisualMode _mode;

    public Visualizer(VisualMode mode) => _mode = mode;

    /// Render N LEDs for one zone. Reuses the caller-supplied buffer (length == ledCount)
    /// to avoid per-frame allocations on the hot path.
    public void Render(AnalysisState a, string[] outColors)
    {
        if (_mode == VisualMode.Level) RenderLevel(a, outColors);
        else RenderSpectrum(a, outColors);
    }

    private static void RenderLevel(AnalysisState a, string[] led)
    {
        int n = led.Length;
        // gamma the level a touch so small sounds still register visibly
        float lit = MathF.Pow(Math.Clamp(a.Level, 0f, 1f), 0.7f) * n;
        for (int i = 0; i < n; i++)
        {
            float frac = n == 1 ? lit : (float)i / (n - 1);   // 0 at start .. 1 at end
            bool on = i < lit;
            if (n == 1) on = true;
            // green (low) -> yellow (mid) -> red (top of the meter)
            (byte r, byte g, byte b) = HsvToRgb((1f - frac) * 0.33f, 1f, on ? (n == 1 ? a.Level : 1f) : 0f);
            led[i] = Hex(r, g, b);
        }
    }

    private static void RenderSpectrum(AnalysisState a, string[] led)
    {
        int n = led.Length;
        int nb = a.Bands.Length;
        for (int i = 0; i < n; i++)
        {
            float pos = n == 1 ? 0f : (float)i / (n - 1);     // 0..1 along the strip

            // each LED covers a CONTIGUOUS slice of the spectrum and takes the strongest band
            // in that slice. On small zones (e.g. a 5-LED DRAM stick) this means no bands are
            // skipped and the whole stick participates, instead of sampling a few far-apart
            // bands and leaving the treble LEDs dark.
            float mag;
            if (nb == 0) mag = a.Level;
            else
            {
                int lo = (int)((float)i / n * nb);
                int hi = Math.Max(lo + 1, (int)((float)(i + 1) / n * nb));
                mag = 0f;
                for (int bi = lo; bi < hi && bi < nb; bi++)
                    if (a.Bands[bi] > mag) mag = a.Bands[bi];
            }

            float value = MathF.Pow(Math.Clamp(mag, 0f, 1f), 0.6f);
            // rainbow hue across the strip; brightness = band energy
            (byte r, byte g, byte b) = HsvToRgb(pos * 0.83f, 1f, value);
            led[i] = Hex(r, g, b);
        }
    }

    private static string Hex(byte r, byte g, byte b) => $"{r:X2}{g:X2}{b:X2}";

    /// h, s, v all in 0..1. Standard HSV->RGB.
    private static (byte, byte, byte) HsvToRgb(float h, float s, float v)
    {
        h = (h % 1f + 1f) % 1f;
        float c = v * s;
        float x = c * (1 - MathF.Abs((h * 6f) % 2f - 1f));
        float m = v - c;
        float r, g, b;
        switch ((int)(h * 6f) % 6)
        {
            case 0: (r, g, b) = (c, x, 0); break;
            case 1: (r, g, b) = (x, c, 0); break;
            case 2: (r, g, b) = (0, c, x); break;
            case 3: (r, g, b) = (0, x, c); break;
            case 4: (r, g, b) = (x, 0, c); break;
            default: (r, g, b) = (c, 0, x); break;
        }
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
