using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Broker.Client;

namespace ReferenceConsole;

/*---------------------------------------------------------------------------*\
| ColorPickerWindow                                                          |
|                                                                            |
|   A small, dependency-free HSV colour picker: a saturation/value chart you |
|   click or drag, plus a hue strip, with a live swatch and an editable hex  |
|   code that always tracks the chosen colour. Returns the picked colour via |
|   ShowDialog<RgbColor?> (null when cancelled). Built entirely in code to    |
|   match the console's generated-control style and avoid a new package.     |
\*---------------------------------------------------------------------------*/
public sealed class ColorPickerWindow : Window
{
    private const double ChartW = 240, ChartH = 200, HueW = 26;

    private double _hue;          // 0..360
    private double _sat = 1;      // 0..1
    private double _val = 1;      // 0..1

    private readonly Canvas _svCanvas;
    private readonly Rectangle _svHueLayer;
    private readonly Border _svCursor;
    private readonly Canvas _hueCanvas;
    private readonly Border _hueCursor;
    private readonly Border _swatch;
    private readonly TextBox _hexBox;
    private bool _suppressHex;
    private bool _dragSv, _dragHue;

    public ColorPickerWindow(RgbColor initial)
    {
        Title = "Colour picker";
        Width = 320;
        Height = 320;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E2E"));

        (_hue, _sat, _val) = ToHsv(initial);

        // ----- saturation/value chart: pure-hue base + white(left)→clear and clear→black(bottom) overlays -----
        _svHueLayer = new Rectangle { Width = ChartW, Height = ChartH };
        var satLayer = new Rectangle
        {
            Width = ChartW, Height = ChartH,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Colors.White, 0),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                },
            },
        };
        var valLayer = new Rectangle
        {
            Width = ChartW, Height = ChartH,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                    new GradientStop(Colors.Black, 1),
                },
            },
        };
        _svCursor = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
        };
        _svCanvas = new Canvas { Width = ChartW, Height = ChartH, Background = Brushes.Transparent };
        _svCanvas.Children.Add(_svHueLayer);
        _svCanvas.Children.Add(satLayer);
        _svCanvas.Children.Add(valLayer);
        _svCanvas.Children.Add(_svCursor);
        _svCanvas.PointerPressed += (_, e) => { _dragSv = true; UpdateSvFrom(e.GetPosition(_svCanvas)); };
        _svCanvas.PointerMoved += (_, e) => { if (_dragSv) UpdateSvFrom(e.GetPosition(_svCanvas)); };
        _svCanvas.PointerReleased += (_, _) => _dragSv = false;
        var svBorder = new Border
        {
            Child = _svCanvas, ClipToBounds = true,
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
        };

        // ----- hue strip (top→bottom = 0→360°) -----
        var hueRect = new Rectangle
        {
            Width = HueW, Height = ChartH,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(255, 0, 0), 0.0),
                    new GradientStop(Color.FromRgb(255, 255, 0), 1.0 / 6),
                    new GradientStop(Color.FromRgb(0, 255, 0), 2.0 / 6),
                    new GradientStop(Color.FromRgb(0, 255, 255), 3.0 / 6),
                    new GradientStop(Color.FromRgb(0, 0, 255), 4.0 / 6),
                    new GradientStop(Color.FromRgb(255, 0, 255), 5.0 / 6),
                    new GradientStop(Color.FromRgb(255, 0, 0), 1.0),
                },
            },
        };
        _hueCursor = new Border
        {
            Width = HueW + 4, Height = 5,
            BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
        };
        _hueCanvas = new Canvas { Width = HueW, Height = ChartH, Background = Brushes.Transparent };
        _hueCanvas.Children.Add(hueRect);
        _hueCanvas.Children.Add(_hueCursor);
        _hueCanvas.PointerPressed += (_, e) => { _dragHue = true; UpdateHueFrom(e.GetPosition(_hueCanvas)); };
        _hueCanvas.PointerMoved += (_, e) => { if (_dragHue) UpdateHueFrom(e.GetPosition(_hueCanvas)); };
        _hueCanvas.PointerReleased += (_, _) => _dragHue = false;
        var hueBorder = new Border
        {
            Child = _hueCanvas, ClipToBounds = true,
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
        };

        var charts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        charts.Children.Add(svBorder);
        charts.Children.Add(hueBorder);

        // ----- readout row: live swatch + editable hex -----
        _swatch = new Border
        {
            Width = 40, Height = 28, CornerRadius = new CornerRadius(3),
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
        };
        _hexBox = new TextBox
        {
            Width = 100, MaxLength = 7, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
        };
        _hexBox.PropertyChanged += (_, ev) =>
        {
            if (ev.Property != TextBox.TextProperty || _suppressHex) return;
            if (RgbColor.TryParseHex(_hexBox.Text, out var c))
            {
                (_hue, _sat, _val) = ToHsv(c);
                RefreshAll(updateHex: false);
            }
        };
        var readout = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        readout.Children.Add(_swatch);
        readout.Children.Add(new TextBlock { Text = "#", VerticalAlignment = VerticalAlignment.Center });
        readout.Children.Add(_hexBox);

        // ----- OK / Cancel -----
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 72 };
        ok.Click += (_, _) => Close((RgbColor?)RgbColor.FromHsv(_hue, _sat, _val));
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 72 };
        cancel.Click += (_, _) => Close((RgbColor?)null);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new StackPanel { Margin = new Thickness(14), Spacing = 12 };
        root.Children.Add(charts);
        root.Children.Add(readout);
        root.Children.Add(buttons);
        Content = root;

        RefreshAll(updateHex: true);
    }

    private void UpdateSvFrom(Point p)
    {
        _sat = Math.Clamp(p.X / ChartW, 0, 1);
        _val = Math.Clamp(1 - p.Y / ChartH, 0, 1);
        RefreshAll(updateHex: true);
    }

    private void UpdateHueFrom(Point p)
    {
        _hue = Math.Clamp(p.Y / ChartH, 0, 1) * 360;
        RefreshAll(updateHex: true);
    }

    /// <summary>Repaint the hue base, reposition both cursors, and sync the swatch + hex.</summary>
    private void RefreshAll(bool updateHex)
    {
        var pureHue = RgbColor.FromHsv(_hue, 1, 1);
        _svHueLayer.Fill = new SolidColorBrush(Color.FromRgb(pureHue.R, pureHue.G, pureHue.B));

        Canvas.SetLeft(_svCursor, _sat * ChartW - 7);
        Canvas.SetTop(_svCursor, (1 - _val) * ChartH - 7);
        Canvas.SetLeft(_hueCursor, -2);
        Canvas.SetTop(_hueCursor, _hue / 360 * ChartH - 2.5);

        var c = RgbColor.FromHsv(_hue, _sat, _val);
        _swatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        if (updateHex)
        {
            _suppressHex = true;
            _hexBox.Text = c.ToHex();
            _suppressHex = false;
        }
    }

    /// <summary>RGB → HSV (h in [0,360), s/v in [0,1]); inverse of <see cref="RgbColor.FromHsv"/>.</summary>
    private static (double h, double s, double v) ToHsv(RgbColor c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
        }
        if (h < 0) h += 360;
        double s = max <= 0 ? 0 : d / max;
        return (h, s, max);
    }
}
