using System.Globalization;

namespace Broker.Client;

/*---------------------------------------------------------------------------*\
| RgbColor                                                                   |
|                                                                            |
|   The one color type used across the wire client and the console's effect  |
|   engine. Kept here (not in the UI) so the client library owns the exact   |
|   "RRGGBB" hex form the broker's rgb.set op expects.                       |
\*---------------------------------------------------------------------------*/
public readonly struct RgbColor
{
    public readonly byte R, G, B;
    public RgbColor(byte r, byte g, byte b) { R = r; G = g; B = b; }

    public static readonly RgbColor Black = new(0, 0, 0);

    /// <summary>Wire form expected by the broker: six upper-case hex digits, no '#'.</summary>
    public string ToHex() => $"{R:X2}{G:X2}{B:X2}";

    public static bool TryParseHex(string? hex, out RgbColor color)
    {
        color = Black;
        hex = hex?.TrimStart('#');
        if (hex is null || hex.Length != 6) return false;
        if (byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            color = new RgbColor(r, g, b);
            return true;
        }
        return false;
    }

    /// <summary>HSV → RGB. h in [0,360), s and v in [0,1].</summary>
    public static RgbColor FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0);
        else if (h < 120) (r, g, b) = (x, c, 0);
        else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c);
        else if (h < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);
        return new RgbColor(To255(r + m), To255(g + m), To255(b + m));
    }

    /// <summary>Linear blend between two colors. t in [0,1].</summary>
    public static RgbColor Lerp(RgbColor a, RgbColor b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new RgbColor(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    /// <summary>Scale intensity by a factor in [0,1] (simple multiplicative brightness).</summary>
    public RgbColor Scale(double f)
    {
        f = Math.Clamp(f, 0, 1);
        return new RgbColor(To255(R / 255.0 * f), To255(G / 255.0 * f), To255(B / 255.0 * f));
    }

    private static byte To255(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
}
