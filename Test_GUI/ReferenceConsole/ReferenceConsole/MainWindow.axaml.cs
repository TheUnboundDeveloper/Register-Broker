using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Broker.Client;
using ReferenceConsole.Effects;
using IEffect = ReferenceConsole.Effects.IEffect;   // disambiguate from Avalonia.Media.IEffect

namespace ReferenceConsole;

/*---------------------------------------------------------------------------*\
| MainWindow                                                                 |
|                                                                            |
|   The whole reference console in one window. Transparent, dependency-light |
|   code-behind over the broker pipes: Sensors (live readall), RGB (a small  |
|   client-side effect engine streaming per-LED frames through rgb.set), and |
|   Diagnostics. Every effect knob is generated from the effect's parameter  |
|   list, so anything -- including the audio reactive factor -- is tunable   |
|   live.                                                                     |
\*---------------------------------------------------------------------------*/
public partial class MainWindow : Window
{
    private static readonly string[] EffectNames =
        { "(none)", "Static", "Temperature", "Rainbow", "Breathing", "Comet", "Twinkle", "Aurora", "Manual per-LED", "Audio Spectrum" };

    private static readonly string[] Palette =
        { "FF0000", "00FF00", "0000FF", "FFFFFF", "FF6A00", "00FFFF", "FF00FF", "000000" };

    private readonly ObservableCollection<SensorRow> _sensorRows = new();
    private readonly ObservableCollection<RgbRow> _rgbRows = new();
    private readonly ObservableCollection<LedCell> _ledCells = new();

    private BrokerClient? _sensors;
    private BrokerClient? _control;
    private EffectEngine? _engine;
    private DispatcherTimer? _pollTimer;

    private List<string> _sensorIds = new();
    private string? _selectedDeviceId;
    private bool _suppressEffectCombo;
    private DateTime _lastPreview = DateTime.MinValue;

    private readonly ConsoleSettings _settings;
    private TrayIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        SensorList.ItemsSource = _sensorRows;
        RgbDeviceList.ItemsSource = _rgbRows;
        LedPreview.ItemsSource = _ledCells;
        EffectCombo.ItemsSource = EffectNames;

        _settings = ConsoleSettings.Load();
        SetupTrayIcon();
        PropertyChanged += OnWindowPropertyChanged;
        ApplyGlobalSettings();

        Closing += (_, _) => { SaveSettings(); Cleanup(); _trayIcon?.Dispose(); _trayIcon = null; };
    }

    // ===================================================================
    //  Persisted settings (see ConsoleSettings)
    // ===================================================================

    /// <summary>Restore the global render knobs into their controls (called once at startup).</summary>
    private void ApplyGlobalSettings()
    {
        PollInterval.Value = _settings.PollIntervalMs;
        FpsBox.Value = _settings.Fps;
        RefreshMsBox.Value = _settings.SensorRefreshMs;
        MinimizeTargetCombo.SelectedIndex = _settings.MinimizeToTray ? 1 : 0;
    }

    /// <summary>Capture the current UI + per-device effect state and write it to disk.</summary>
    private void SaveSettings()
    {
        _settings.Fps = (int)(FpsBox.Value ?? 20);
        _settings.SensorRefreshMs = (int)(RefreshMsBox.Value ?? 750);
        _settings.PollIntervalMs = (int)(PollInterval.Value ?? 1000);
        _settings.MinimizeToTray = MinimizeTargetCombo.SelectedIndex == 1;

        // Only refresh device entries when an engine exists; otherwise keep what we loaded.
        if (_engine is not null)
        {
            foreach (var row in _rgbRows)
            {
                var eff = _engine.GetEffect(row.Id);
                if (eff is null) { _settings.Devices.Remove(row.Id); continue; }
                _settings.Devices[row.Id] = DeviceSettings.Capture(eff, _engine.IsEnabled(row.Id), row.LedCount);
            }
        }

        try { _settings.Save(); }
        catch (Exception ex) { Log("Settings save failed: " + ex.Message); }
    }

    /// <summary>Re-apply saved effects to the devices the broker just reported.</summary>
    private void RestoreDeviceSettings()
    {
        if (_engine is null) return;
        int restored = 0;
        foreach (var row in _rgbRows)
        {
            if (!_settings.Devices.TryGetValue(row.Id, out var ds)) continue;
            var eff = CreateEffect(ds.Effect);
            if (eff is null) continue;
            if (eff is ISensorAware sa) sa.SetSensorIds(_sensorIds);
            ds.ApplyTo(eff, row.LedCount);

            _engine.AssignEffect(row.Id, row.LedCount, eff);
            _engine.SetEnabled(row.Id, ds.Drive);
            row.SetEffect(eff.Name);
            row.SetEnabled(ds.Drive);
            if (eff is TemperatureEffect && ds.Drive) EnsureSensorPoll();
            restored++;
        }
        if (restored > 0) Log($"Restored {restored} saved device setting(s).");
    }

    // ===================================================================
    //  Minimize target (taskbar vs. system tray)
    // ===================================================================

    /// <summary>Build the tray icon (hidden until the window is minimized to tray).</summary>
    private void SetupTrayIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://ReferenceConsole/Assets/rc-logo.ico"));
            var menu = new NativeMenu();
            var showItem = new NativeMenuItem("Show");
            showItem.Click += (_, _) => RestoreFromTray();
            var exitItem = new NativeMenuItem("Exit");
            exitItem.Click += (_, _) => Close();
            menu.Add(showItem);
            menu.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(stream),
                ToolTipText = "Register Broker — Reference Console",
                IsVisible = false,
                Menu = menu,
            };
            _trayIcon.Clicked += (_, _) => RestoreFromTray();
        }
        catch { _trayIcon = null; }   // no tray support -> fall back to plain taskbar minimize
    }

    private void OnMinimizeTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        _settings.MinimizeToTray = MinimizeTargetCombo.SelectedIndex == 1;
        // Switching back to "Taskbar" while already hidden -> bring the window back.
        if (!_settings.MinimizeToTray && _trayIcon is { IsVisible: true }) RestoreFromTray();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized)
            HideToTrayIfRequested();
    }

    private void HideToTrayIfRequested()
    {
        if (!_settings.MinimizeToTray || _trayIcon is null) return;
        _trayIcon.IsVisible = true;
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        if (_trayIcon is not null) _trayIcon.IsVisible = false;
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // ===================================================================
    //  Connection
    // ===================================================================
    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_sensors is not null || _control is not null) { SaveSettings(); Cleanup(); SetStatus(false, "Disconnected", ""); return; }

        ConnectButton.IsEnabled = false;
        try
        {
            var sensors = BrokerClient.ForSensors();
            await sensors.ConnectAsync(new[] { "sensors:read" });
            _sensors = sensors;
            Log($"Connected to SensorBroker. Granted: [{string.Join(", ", sensors.GrantedScopes)}]");

            try
            {
                var control = BrokerClient.ForControl();
                await control.ConnectAsync(new[] { "rgb:write" });
                _control = control;
                Log($"Connected to BrokerControl. Granted: [{string.Join(", ", control.GrantedScopes)}]");
            }
            catch (BrokerException bx) { Log($"RGB control unavailable: {bx.Message}"); }

            var allScopes = _sensors.GrantedScopes.Concat(_control?.GrantedScopes ?? Array.Empty<string>());
            SetStatus(true, "Connected", "scopes: " + string.Join(", ", allScopes));
            ConnectButton.Content = "Disconnect";

            // Load the sensor-id list (for the Temperature effect's picker).
            try { _sensorIds = (await _sensors.SensorListAsync()).Select(s => s.Id).ToList(); }
            catch { _sensorIds = new(); }

            if (_control is not null)
            {
                _engine = new EffectEngine(_control, _sensors)
                {
                    Fps = (int)(FpsBox.Value ?? 20),
                    SensorRefreshMs = (int)(RefreshMsBox.Value ?? 750),
                };
                _engine.FrameRendered += OnFrameRendered;
                _engine.PushFailed += (id, msg) => Log($"[{id}] {msg}");
                _engine.Start();
                EngineStatus.Text = "Engine running. Pick an effect, tick \"Drive\".";
            }

            await ReadSensorsOnce();
            await RefreshRgbDevices();
            RestoreDeviceSettings();
        }
        catch (BrokerException bx)
        {
            Log("Connect failed: " + bx.Message);
            SetStatus(false, "Connect failed", "");
            Cleanup();
        }
        finally { ConnectButton.IsEnabled = true; }
    }

    private void SetStatus(bool ok, string text, string scopes)
    {
        StatusDot.Fill = ok ? Brushes.LimeGreen : new SolidColorBrush(Color.Parse("#888888"));
        StatusText.Text = text;
        ScopesText.Text = scopes;
        if (!ok) ConnectButton.Content = "Connect";
    }

    // ===================================================================
    //  Sensors tab
    // ===================================================================
    private async void OnSensorReadOnce(object? sender, RoutedEventArgs e) => await ReadSensorsOnce();

    private async Task ReadSensorsOnce()
    {
        if (_sensors is null) { Log("Not connected."); return; }
        try
        {
            var t0 = DateTime.UtcNow;
            var list = await _sensors.SensorReadAllAsync();
            var ms = (DateTime.UtcNow - t0).TotalMilliseconds;
            MergeSensors(list);
            SensorCount.Text = $"{list.Count} sensors · {ms:F0} ms";
        }
        catch (Exception ex) { Log("sensor.readall failed: " + ex.Message); StopPolling(); }
    }

    private void MergeSensors(IReadOnlyList<SensorInfo> list)
    {
        var byId = _sensorRows.ToDictionary(r => r.Id);
        foreach (var s in list)
        {
            if (byId.TryGetValue(s.Id, out var row)) row.Update(s);
            else _sensorRows.Add(new SensorRow(s));
        }
    }

    private void OnSensorPollToggle(object? sender, RoutedEventArgs e)
    {
        if (SensorPollToggle.IsChecked == true) StartPolling(); else StopPolling();
    }

    private void StartPolling()
    {
        if (_sensors is null) { SensorPollToggle.IsChecked = false; Log("Not connected."); return; }
        _pollTimer?.Stop();
        var ms = (int)(PollInterval.Value ?? 1000);
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _pollTimer.Tick += async (_, _) => await ReadSensorsOnce();
        _pollTimer.Start();
        Log($"Live poll started ({ms} ms).");
    }

    private void StopPolling()
    {
        if (_pollTimer is null) return;
        _pollTimer.Stop();
        _pollTimer = null;
        SensorPollToggle.IsChecked = false;
        Log("Live poll stopped.");
    }

    // ===================================================================
    //  RGB tab
    // ===================================================================
    private async void OnRgbRefresh(object? sender, RoutedEventArgs e) => await RefreshRgbDevices();

    private async Task RefreshRgbDevices()
    {
        if (_control is null) { Log("RGB control not connected."); return; }
        try
        {
            var devices = await _control.RgbListAsync();
            _rgbRows.Clear();
            foreach (var d in devices) _rgbRows.Add(new RgbRow(d));
            Log($"rgb.list -> {devices.Count} device(s).");
        }
        catch (Exception ex) { Log("rgb.list failed: " + ex.Message); }
    }

    private RgbRow? SelectedRow => RgbDeviceList.SelectedItem as RgbRow;

    private void OnRgbDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null || _engine is null) { EditorPanel.IsVisible = false; return; }

        _selectedDeviceId = row.Id;
        RgbSelected.Text = $"{row.Label}  ·  {row.Detail}";
        EditorPanel.IsVisible = true;

        // Build a fresh preview strip sized to the device.
        _ledCells.Clear();
        for (int i = 0; i < row.LedCount; i++) _ledCells.Add(new LedCell(i));

        // Restore this device's current effect + drive state.
        var eff = _engine.GetEffect(row.Id);
        _suppressEffectCombo = true;
        EffectCombo.SelectedItem = eff?.Name ?? "(none)";
        _suppressEffectCombo = false;
        DriveCheck.IsChecked = _engine.IsEnabled(row.Id);
        ManualTools.IsVisible = eff is ManualEffect;
        BuildParamPanel(eff);
    }

    private void OnEffectChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEffectCombo || _engine is null || SelectedRow is not { } row) return;
        string name = EffectCombo.SelectedItem as string ?? "(none)";
        IEffect? eff = CreateEffect(name);
        if (eff is ISensorAware sa) sa.SetSensorIds(_sensorIds);

        _engine.AssignEffect(row.Id, row.LedCount, eff);
        row.SetEffect(name);
        ManualTools.IsVisible = eff is ManualEffect;
        BuildParamPanel(eff);

        // Temperature is driven by live sensor values — make sure the poll is running
        // so the Sensors tab reflects what the effect is reacting to.
        if (eff is TemperatureEffect) EnsureSensorPoll();
    }

    /// <summary>Apply the selected device's effect (type + current params) to every device and drive them all.</summary>
    private void OnDriveAll(object? sender, RoutedEventArgs e)
    {
        if (_engine is null || SelectedRow is not { } row) return;
        var src = _engine.GetEffect(row.Id);
        if (src is null) { Log("Pick an effect first."); return; }

        foreach (var r in _rgbRows)
        {
            if (r.Id != row.Id)
                _engine.AssignEffect(r.Id, r.LedCount, CloneEffect(src));   // others get their own instance
            _engine.SetEnabled(r.Id, true);
            r.SetEffect(src.Name);
            r.SetEnabled(true);
        }
        DriveCheck.IsChecked = true;
        if (src is TemperatureEffect) EnsureSensorPoll();
        Log($"Applied {src.Name} to all {_rgbRows.Count} device(s).");
    }

    private void OnStopAll(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        foreach (var r in _rgbRows) { _engine.SetEnabled(r.Id, false); r.SetEnabled(false); }
        DriveCheck.IsChecked = false;
        Log("Stopped driving all devices.");
    }

    /// <summary>Make a fresh effect of the same type with the same parameter values (per-device state stays separate).</summary>
    private IEffect? CloneEffect(IEffect src)
    {
        var dst = CreateEffect(src.Name);
        if (dst is null) return null;
        if (dst is ISensorAware sa) sa.SetSensorIds(_sensorIds);
        var byKey = src.Parameters.ToDictionary(p => p.Key);
        foreach (var p in dst.Parameters)
        {
            if (!byKey.TryGetValue(p.Key, out var s)) continue;
            switch (p.Kind)
            {
                case ParamKind.Slider: p.Num = s.Num; break;
                case ParamKind.Color: p.Color_ = s.Color_; break;
                case ParamKind.Toggle: p.Flag = s.Flag; break;
                case ParamKind.Choice: p.ChoiceIndex = s.ChoiceIndex; break;
            }
        }
        if (src is IGradientEffect gs && dst is IGradientEffect gd) gd.SetStops(gs.Stops);
        return dst;
    }

    private void EnsureSensorPoll()
    {
        if (_pollTimer is null && _sensors is not null) { SensorPollToggle.IsChecked = true; StartPolling(); }
    }

    private void OnDriveToggled(object? sender, RoutedEventArgs e)
    {
        if (_engine is null || SelectedRow is not { } row) return;
        bool on = DriveCheck.IsChecked == true;
        if (on && _engine.GetEffect(row.Id) is null)
        {
            Log("Pick an effect before driving."); DriveCheck.IsChecked = false; return;
        }
        _engine.SetEnabled(row.Id, on);
        row.SetEnabled(on);
        Log($"[{row.Id}] drive {(on ? "ON" : "off")}");
    }

    private async void OnApplyOnce(object? sender, RoutedEventArgs e)
    {
        if (_engine is null || SelectedRow is not { } row) return;
        if (_engine.GetEffect(row.Id) is null) { Log("Pick an effect first."); return; }
        bool ok = await _engine.RenderOnceAsync(row.Id);
        Log($"[{row.Id}] apply once -> {(ok ? "OK" : "failed")}");
    }

    private void OnFpsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_engine is not null) _engine.Fps = (int)(FpsBox.Value ?? 20);
    }

    private void OnRefreshMsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_engine is not null) _engine.SensorRefreshMs = (int)(RefreshMsBox.Value ?? 750);
    }

    private async void OnLedPainted(object? sender, PointerPressedEventArgs e)
    {
        if (_engine is null || SelectedRow is not { } row) return;
        if (_engine.GetEffect(row.Id) is not ManualEffect m) return;
        if (sender is not Control c || c.Tag is not int index) return;

        m.SetLed(index, m.Brush);
        if (index >= 0 && index < _ledCells.Count) _ledCells[index].Brush = ToBrush(m.Brush);
        if (!_engine.IsEnabled(row.Id)) await _engine.RenderOnceAsync(row.Id);   // immediate feedback
    }

    private async void OnManualFill(object? sender, RoutedEventArgs e)
    {
        if (_engine is null || SelectedRow is not { } row) return;
        if (_engine.GetEffect(row.Id) is not ManualEffect m) return;
        m.Fill(m.Brush);
        var b = ToBrush(m.Brush);
        foreach (var cell in _ledCells) cell.Brush = b;
        await _engine.RenderOnceAsync(row.Id);
    }

    private async void OnManualClear(object? sender, RoutedEventArgs e)
    {
        if (_engine is null || SelectedRow is not { } row) return;
        if (_engine.GetEffect(row.Id) is not ManualEffect m) return;
        m.Fill(RgbColor.Black);
        var b = ToBrush(RgbColor.Black);
        foreach (var cell in _ledCells) cell.Brush = b;
        await _engine.RenderOnceAsync(row.Id);
    }

    // Engine raises this on its loop thread; copy + marshal a throttled preview.
    private void OnFrameRendered(string deviceId, RgbColor[] colors)
    {
        if (deviceId != _selectedDeviceId) return;
        var now = DateTime.UtcNow;
        if ((now - _lastPreview).TotalMilliseconds < 66) return;   // ~15 fps preview
        _lastPreview = now;

        int n = Math.Min(colors.Length, _ledCells.Count);
        var snapshot = new RgbColor[n];
        Array.Copy(colors, snapshot, n);
        Dispatcher.UIThread.Post(() =>
        {
            for (int i = 0; i < n && i < _ledCells.Count; i++) _ledCells[i].Brush = ToBrush(snapshot[i]);
        });
    }

    // ===================================================================
    //  Generic parameter panel — one control per EffectParam, by Kind
    // ===================================================================
    private void BuildParamPanel(IEffect? eff)
    {
        ParamPanel.Children.Clear();
        if (eff is null) return;
        foreach (var p in eff.Parameters)
            ParamPanel.Children.Add(BuildParamControl(p));
        if (eff is IGradientEffect g)
            ParamPanel.Children.Add(BuildGradientSection(g));
    }

    // Editable list of temperature→colour stops, with add/remove.
    private Control BuildGradientSection(IGradientEffect g)
    {
        var root = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        root.Children.Add(new TextBlock { Text = "Temperature colour stops", Foreground = Brushes.Gray, FontSize = 11 });
        var host = new StackPanel { Spacing = 4 };
        PopulateStops(host, g);
        root.Children.Add(host);
        var add = new Button { Content = "Add colour stop" };
        add.Click += (_, _) => { g.AddStop(); PopulateStops(host, g); };
        root.Children.Add(add);
        return root;
    }

    private void PopulateStops(StackPanel host, IGradientEffect g)
    {
        host.Children.Clear();
        foreach (var stop in g.Stops.OrderBy(s => s.Temp))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var temp = new NumericUpDown { Minimum = 0, Maximum = 120, Increment = 1,
                Value = (decimal)stop.Temp, FormatString = "0", Width = 104 };
            temp.ValueChanged += (_, _) => stop.Temp = (double)(temp.Value ?? 0m);

            var swatch = new Border { Width = 24, Height = 22, CornerRadius = new Avalonia.CornerRadius(3),
                BorderBrush = Brushes.Gray, BorderThickness = new Avalonia.Thickness(1), Background = ToBrush(stop.Color) };
            var hex = new TextBox { Width = 88, Text = stop.Color.ToHex(), MaxLength = 6,
                FontFamily = new FontFamily("Cascadia Code,Consolas,monospace") };
            hex.PropertyChanged += (_, ev) =>
            {
                if (ev.Property == TextBox.TextProperty && RgbColor.TryParseHex(hex.Text, out var c))
                { stop.Color = c; swatch.Background = ToBrush(c); }
            };
            var remove = new Button { Content = "✕", IsEnabled = g.Stops.Count > 1 };
            remove.Click += (_, _) => { g.RemoveStop(stop); PopulateStops(host, g); };

            row.Children.Add(new TextBlock { Text = "at", VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(temp);
            row.Children.Add(new TextBlock { Text = "°", VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(swatch);
            row.Children.Add(new TextBlock { Text = "#", VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(hex);
            // Basic colour quick-picks (setting hex re-runs the handler above).
            foreach (var preset in Palette)
            {
                RgbColor.TryParseHex(preset, out var pc);
                var swatchBtn = new Button { Width = 18, Height = 18, Background = ToBrush(pc),
                    BorderBrush = Brushes.Gray, BorderThickness = new Avalonia.Thickness(1), Padding = new Avalonia.Thickness(0) };
                swatchBtn.Click += (_, _) => { hex.Text = preset; };
                row.Children.Add(swatchBtn);
            }
            row.Children.Add(remove);
            host.Children.Add(row);
        }
    }

    private Control BuildParamControl(EffectParam p)
    {
        var root = new StackPanel { Spacing = 3 };
        switch (p.Kind)
        {
            case ParamKind.Slider:
            {
                var head = new DockPanel();
                var value = new TextBlock { Text = FormatNum(p), Foreground = Brushes.Gray };
                DockPanel.SetDock(value, Dock.Right);
                head.Children.Add(value);
                head.Children.Add(new TextBlock { Text = p.Label });
                var slider = new Slider { Minimum = p.Min, Maximum = p.Max, Value = p.Num, SmallChange = p.Step };
                slider.PropertyChanged += (_, ev) =>
                {
                    if (ev.Property == Slider.ValueProperty) { p.Num = slider.Value; value.Text = FormatNum(p); }
                };
                root.Children.Add(head);
                root.Children.Add(slider);
                break;
            }
            case ParamKind.Color:
            {
                root.Children.Add(new TextBlock { Text = p.Label });
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var swatch = new Border { Width = 28, Height = 24, CornerRadius = new Avalonia.CornerRadius(3),
                    BorderBrush = Brushes.Gray, BorderThickness = new Avalonia.Thickness(1), Background = ToBrush(p.Color_) };
                var hex = new TextBox { Width = 96, Text = p.Hex, MaxLength = 6,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,monospace") };
                hex.PropertyChanged += (_, ev) =>
                {
                    if (ev.Property == TextBox.TextProperty && RgbColor.TryParseHex(hex.Text, out var c))
                    { p.Color_ = c; swatch.Background = ToBrush(c); }
                };
                row.Children.Add(swatch);
                row.Children.Add(new TextBlock { Text = "#", VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(hex);
                foreach (var preset in Palette)
                {
                    RgbColor.TryParseHex(preset, out var pc);
                    var b = new Button { Width = 22, Height = 22, Background = ToBrush(pc),
                        BorderBrush = Brushes.Gray, BorderThickness = new Avalonia.Thickness(1), Padding = new Avalonia.Thickness(0) };
                    b.Click += (_, _) => { hex.Text = preset; };
                    row.Children.Add(b);
                }
                root.Children.Add(row);
                break;
            }
            case ParamKind.Toggle:
            {
                var cb = new CheckBox { Content = p.Label, IsChecked = p.Flag };
                cb.IsCheckedChanged += (_, _) => p.Flag = cb.IsChecked == true;
                root.Children.Add(cb);
                break;
            }
            case ParamKind.Choice:
            {
                root.Children.Add(new TextBlock { Text = p.Label });
                var combo = new ComboBox { Width = 220, ItemsSource = p.Choices, SelectedIndex = p.ChoiceIndex };
                combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) p.ChoiceIndex = combo.SelectedIndex; };
                root.Children.Add(combo);
                break;
            }
        }
        return root;
    }

    private static string FormatNum(EffectParam p)
        => (p.Step < 1 ? p.Num.ToString("0.00", CultureInfo.InvariantCulture)
                       : p.Num.ToString("0", CultureInfo.InvariantCulture));

    private static ISolidColorBrush ToBrush(RgbColor c) => new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));

    private static IEffect? CreateEffect(string name) => name switch
    {
        "Static" => new StaticEffect(),
        "Temperature" => new TemperatureEffect(),
        "Rainbow" => new RainbowEffect(),
        "Breathing" => new BreathingEffect(),
        "Comet" => new CometEffect(),
        "Twinkle" => new TwinkleEffect(),
        "Aurora" => new AuroraEffect(),
        "Manual per-LED" => new ManualEffect(),
        "Audio Spectrum" => new AudioSpectrumEffect(),
        _ => null,
    };

    // ===================================================================
    //  Diagnostics tab
    // ===================================================================
    private async void OnPing(object? sender, RoutedEventArgs e)
    {
        if (_sensors is null) { Log("Not connected."); return; }
        try
        {
            var t0 = DateTime.UtcNow;
            bool alive = await _sensors.PingAsync();
            var ms = (DateTime.UtcNow - t0).TotalMilliseconds;
            Log(alive ? $"ping -> pong ({ms:F1} ms)" : "ping -> no pong");
        }
        catch (Exception ex) { Log("ping failed: " + ex.Message); }
    }

    private void OnClearLog(object? sender, RoutedEventArgs e) => LogBox.Text = "";

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Dispatcher.UIThread.Post(() => LogBox.Text = string.IsNullOrEmpty(LogBox.Text) ? line : LogBox.Text + "\n" + line);
    }

    private void Cleanup()
    {
        StopPolling();
        if (_engine is not null)
        {
            var eng = _engine; _engine = null;
            _ = eng.StopAsync();
        }
        _ = DisposeClient(_sensors); _sensors = null;
        _ = DisposeClient(_control); _control = null;
        _selectedDeviceId = null;
        EditorPanel.IsVisible = false;
        EngineStatus.Text = "Connect to drive devices.";
    }

    private static async Task DisposeClient(BrokerClient? c)
    {
        if (c is not null) await c.DisposeAsync();
    }
}

/// <summary>Bindable, mutable sensor row so live polling updates values in place.</summary>
public sealed class SensorRow : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; }
    public string Label { get; private set; }
    public string Unit { get; private set; }
    /// <summary>Human-facing name: the calibrated label when present, otherwise the raw id.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Id : Label;
    private string _valueText = "—";
    public string ValueText { get => _valueText; private set { _valueText = value; Raise(nameof(ValueText)); } }

    public SensorRow(SensorInfo s) { Id = s.Id; Label = s.Label; Unit = s.Unit; Update(s); }

    public void Update(SensorInfo s)
    {
        Label = s.Label; Unit = s.Unit;
        ValueText = s.Value is { } v ? v.ToString("F2", CultureInfo.InvariantCulture) : "—";
        Raise(nameof(Label)); Raise(nameof(DisplayName)); Raise(nameof(Unit));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}

/// <summary>Bindable RGB device row (label, detail, assigned-effect summary, drive indicator).</summary>
public sealed class RgbRow : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; }
    public string Label { get; }
    public string Detail { get; }
    public int LedCount { get; }

    private string _effect = "(none)";
    private bool _enabled;

    public RgbRow(RgbDevice d)
    {
        Id = d.Id; Label = string.IsNullOrEmpty(d.Label) ? d.Id : d.Label; LedCount = d.Leds;
        var bits = new List<string> { $"{d.Leds} LED" + (d.Leds == 1 ? "" : "s") };
        if (!string.IsNullOrEmpty(d.Kind)) bits.Add(d.Kind!);
        if (!string.IsNullOrEmpty(d.Transport)) bits.Add(d.Transport!);
        Detail = $"{d.Id} · " + string.Join(" · ", bits);
    }

    public string Summary => $"{Detail} · {_effect}";
    public IBrush StatusBrush => _enabled ? Brushes.LimeGreen : new SolidColorBrush(Color.Parse("#555555"));

    public void SetEffect(string n) { _effect = n; Raise(nameof(Summary)); }
    public void SetEnabled(bool e) { _enabled = e; Raise(nameof(StatusBrush)); }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}

/// <summary>One LED swatch in the live preview strip.</summary>
public sealed class LedCell : System.ComponentModel.INotifyPropertyChanged
{
    public int Index { get; }
    private ISolidColorBrush _brush = new SolidColorBrush(Colors.Black);
    public ISolidColorBrush Brush { get => _brush; set { _brush = value; Raise(nameof(Brush)); } }
    public LedCell(int index) { Index = index; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}
