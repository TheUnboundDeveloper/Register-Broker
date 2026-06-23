using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ReferenceConsole;

/*---------------------------------------------------------------------------*\
| Sparkline                                                                  |
|                                                                            |
|   A tiny, dependency-free trend line for the dashboard overview cards.     |
|   It keeps a short ring of recent samples and draws an auto-scaled stroke  |
|   plus a soft gradient fill beneath it, so a single live value reads as a  |
|   moving graph. Push(value) on every poll; the control re-renders itself.  |
\*---------------------------------------------------------------------------*/
public sealed class Sparkline : Control
{
    private const int Capacity = 60;
    private readonly List<double> _vals = new();

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke), Brushes.Aqua);

    /// <summary>Line colour; the fill is derived from it at low opacity.</summary>
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    static Sparkline() => AffectsRender<Sparkline>(StrokeProperty);

    /// <summary>Append one sample and re-render (oldest is dropped past the ring size).</summary>
    public void Push(double value)
    {
        _vals.Add(value);
        if (_vals.Count > Capacity) _vals.RemoveAt(0);
        InvalidateVisual();
    }

    public void Reset()
    {
        _vals.Clear();
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        if (b.Width <= 1 || b.Height <= 1 || _vals.Count < 2) return;

        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in _vals) { if (v < min) min = v; if (v > max) max = v; }
        if (max - min < 1e-6) { min -= 1; max += 1; }
        double pad = (max - min) * 0.18;
        min -= pad; max += pad;

        int n = _vals.Count;
        double dx = b.Width / (Capacity - 1);
        var pts = new Point[n];
        for (int i = 0; i < n; i++)
        {
            double x = b.Width - (n - 1 - i) * dx;
            double t = (_vals[i] - min) / (max - min);
            double y = b.Height - 4 - t * (b.Height - 8);
            pts[i] = new Point(x, y);
        }

        var stroke = Stroke ?? Brushes.Aqua;
        var color = (stroke as ISolidColorBrush)?.Color ?? Colors.Aqua;

        // soft fill beneath the line
        var fillGeo = new StreamGeometry();
        using (var gc = fillGeo.Open())
        {
            gc.BeginFigure(new Point(pts[0].X, b.Height), true);
            gc.LineTo(pts[0]);
            for (int i = 1; i < n; i++) gc.LineTo(pts[i]);
            gc.LineTo(new Point(pts[n - 1].X, b.Height));
            gc.EndFigure(true);
        }
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        };
        fill.GradientStops.Add(new Avalonia.Media.GradientStop(Color.FromArgb(72, color.R, color.G, color.B), 0));
        fill.GradientStops.Add(new Avalonia.Media.GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        ctx.DrawGeometry(fill, null, fillGeo);

        // the line itself
        var lineGeo = new StreamGeometry();
        using (var gc = lineGeo.Open())
        {
            gc.BeginFigure(pts[0], false);
            for (int i = 1; i < n; i++) gc.LineTo(pts[i]);
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(stroke, 1.8) { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round }, lineGeo);
    }
}
