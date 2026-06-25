using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Broker.Client;
using ReferenceConsole.Effects;
using IEffect = ReferenceConsole.Effects.IEffect;   // disambiguate from Avalonia.Media.IEffect

namespace ReferenceConsole;

/*---------------------------------------------------------------------------*\
| MainWindow                                                                 |
|                                                                            |
|   The Register Broker reference console as a security-framework dashboard: |
|   a custom-chrome window with a live status bar, a left nav, and a set of  |
|   pages (Dashboard / Sensors / Activity / Security / Settings) driven by   |
|   the same broker pipes as before. The Dashboard surfaces overview cards   |
|   with sparklines, grouped sensors, the RGB control panel, a structured    |
|   activity feed, and the scope/permission panel that sells the whole       |
|   "scoped, brokered, non-admin" model. All the original broker/effect      |
|   plumbing is intact -- only the presentation changed.                     |
\*---------------------------------------------------------------------------*/
public partial class MainWindow : Window
{
    private static readonly string[] EffectNames =
        { "(none)", "Static", "Temperature", "Rainbow", "Breathing", "Heartbeat", "Comet", "Scanner", "Theater Chase", "Twinkle", "Aurora", "Plasma", "Fire", "Ripple", "Juggle", "Manual per-LED", "Audio Spectrum" };

    private static readonly string[] Palette =
        { "FF0000", "00FF00", "0000FF", "FFFFFF", "FF6A00", "00FFFF", "FF00FF", "000000" };

    // Fixed display order for the sensor groups (anything unmatched falls into "Other").
    private static readonly string[] GroupOrder = { "CPU", "Motherboard", "Voltages", "Fans", "Other" };

    private readonly ObservableCollection<SensorRow> _sensorRows = new();
    private readonly ObservableCollection<SensorGroup> _sensorGroups = new();
    private readonly Dictionary<string, SensorGroup> _groupByName = new();
    // Sensors that have dropped out of the live catalog (e.g. an unplugged removable Quadro),
    // counted per readall cycle so a single transient miss doesn't flicker a fixed sensor out.
    private readonly Dictionary<string, int> _sensorMiss = new();
    // Sensors that were available AND usable (read OK) in the last readall — the count shown in the UI.
    private int _usableSensorCount;
    private readonly ObservableCollection<RgbRow> _rgbRows = new();
    private readonly ObservableCollection<LedCell> _ledCells = new();
    private readonly ObservableCollection<ActivityEntry> _activity = new();

    private BrokerClient? _sensors;
    private BrokerClient? _control;
    private EffectEngine? _engine;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _uptimeTimer;
    private DateTime _connectedAt;

    private List<string> _sensorIds = new();
    private bool _showOrigins;
    private double _lastLatencyMs;

    private readonly Dictionary<string, IEffect> _effectCache = new();

    private string? _selectedDeviceId;
    private bool _suppressEffectCombo;
    private DateTime _lastPreview = DateTime.MinValue;

    private readonly ConsoleSettings _settings;
    private DispatcherTimer? _deviceSaveTimer;   // debounced flush of per-device effect/param state
    private (PixelPoint Pos, double W, double H)? _normalBounds;   // last Normal-state window placement
    private TrayIcon? _trayIcon;
    private Button[] _navButtons = Array.Empty<Button>();
    private bool _shellInitialized;   // guards one-time seed + auto-connect (Opened can re-fire on tray restore)

    // Dashboard sections: each is an independent canvas of draggable/resizable cards — the built-in
    // "Dashboard" plus any the user adds via "+ Add Section". Per-section state (boxes, layout mode,
    // lock, selection, sensor views, default order) lives in DashSection; only the in-flight
    // manipulation below is global, since the user drags/resizes one card in one visible section at a time.
    private readonly List<DashSection> _sections = new();
    private DashSection _active = null!;          // the section whose page is currently shown
    private int _nextSectionNum = 1;              // id counter for user-created sections

    // Drag-to-reorder the section nav buttons in the sidebar. A press that doesn't move past a small
    // threshold is a normal click (navigate); past it, the button reorders within SectionNavHost.
    private Button? _navDragBtn;
    private Point _navDragStart;
    private bool _navDragging;

    private Border? _dragBox;
    private Point _dragStart;                    // drag origin (canvas coords)
    private double _dragOrigLeft, _dragOrigTop;
    private Border? _resizeBox;                   // box being resized via its grip
    private Point _resizeStartPtr;
    private double _resizeW, _resizeH;

    // Marquee (rubber-band) multi-select + group move (the visual is per-section; this is in-flight state).
    private bool _marqueeActive;
    private Point _marqueeStart;
    private bool _groupDrag;
    private Dictionary<Border, Point> _groupOrigins = new();
    private static readonly Dictionary<string, string> BoxTitles = new()
    {
        ["cpu"] = "CPU Temperature", ["gpu"] = "GPU Temperature", ["vrm"] = "VRM MOS",
        ["sys"] = "System Temp", ["lat"] = "Broker Latency", ["sensors"] = "Sensors",
        ["devices"] = "RGB Devices", ["control"] = "RGB Control", ["activity"] = "Activity",
        ["security"] = "Security / Permissions",
    };

    // Metric boxes whose content comes from a hardware sensor. These are NOT offered by "Add box"
    // (their reading is added from the per-sensor tree instead); the non-sensor box (lat) and the
    // panels remain re-addable.
    private static readonly HashSet<string> SensorBackedBoxes = new() { "cpu", "gpu", "vrm", "sys" };

    // Dashboard metric cards the user has added for individual sensors live per-section now
    // (DashSection.SensorViews / .BoxSensorId); box ids are "sensor:<sectionId>:<sensorId>".
    private const string SensorBoxPrefix = "sensor:";

    // ===== Settings page: draggable / resizable cards on a canvas (parallel to, but independent of,
    //       the dashboard's system). "Lock cards" freezes move + resize. =====
    private const double SettingsCardW = 540;   // default card width; flow lays them out in columns of this
    private readonly List<Border> _settingsCards = new();
    private readonly List<Border> _settingsGrips = new();
    private readonly List<Control> _settingsDragBars = new();   // per-card header drag handle
    private bool _settingsLocked;
    private bool _settingsSeeded;
    private Border? _sDragCard;
    private Point _sDragStart;
    private double _sDragOrigLeft, _sDragOrigTop;
    private Border? _sResizeCard;
    private Point _sResizeStart;
    private double _sResizeW, _sResizeH;

    public MainWindow()
    {
        InitializeComponent();

        SensorGroupsDash.ItemsSource = _sensorGroups;
        SensorGroupsPage.ItemsSource = _sensorGroups;
        RgbDeviceList.ItemsSource = _rgbRows;
        LedPreview.ItemsSource = _ledCells;
        ActivityListDash.ItemsSource = _activity;
        ActivityListPage.ItemsSource = _activity;
        EffectCombo.ItemsSource = EffectNames;

        _navButtons = new[] { NavDashboard, NavSensors, NavRgb, NavSettings };

        // Client-identity panel: everything derived from the real session / OS, not hard-coded.
        SecUser.Text = Environment.UserName;
        SecIntegrity.Text = HostInfo.IntegrityLevel;
        SecSigned.Text = HostInfo.Signature;
        SecSession.Text = HostInfo.SessionLabel;

        _settings = ConsoleSettings.Load();
        LocalNames.Use(_settings.CustomNames);   // local-only display-name overrides (never sent to the broker)
        SetupTrayIcon();
        PropertyChanged += OnWindowPropertyChanged;
        // Re-apply every brush we set from code when the theme flips. Code-set brushes (footer status
        // colours, scope chips, the open RGB param panel) don't track theme dictionaries on their own;
        // without this they keep the previous theme's colours — or get reset to the themed default —
        // after a Dark↔Light toggle.
        ActualThemeVariantChanged += (_, _) => OnThemeChanged();
        ApplyGlobalSettings();
        SetupDashboardBoxes();
        SetupSettingsCards();
        ApplyGpuVendorColor();   // tint the GPU card by vendor (AMD red / NVIDIA green / Intel blue)
        MountRgbPanels(onRgbPage: false);   // dashboard is the default view -> RGB panels start in its cards

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => UpdateUptime();
        _uptimeTimer.Start();

        // Debounced flush of the per-device effect/parameter state. Edits (sliders, colours, drive
        // toggles…) call ScheduleDeviceSave() instead of SaveSettings(); this coalesces a burst of
        // changes into one disk write ~0.8 s after the last edit, so per-animation settings survive
        // even a non-clean exit (kill / crash / shutdown) — they no longer wait for the Closing event.
        _deviceSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _deviceSaveTimer.Tick += (_, _) => { _deviceSaveTimer!.Stop(); PersistDeviceSettings(); };

        // Remember the window's Normal placement so it can be restored next launch.
        PositionChanged += (_, _) => CaptureNormalBounds();

        Opened += async (_, _) =>
        {
            // Restoring from the tray toggles ShowInTaskbar + Show(), which re-creates the native
            // window and RE-RAISES Opened. Only seed/auto-connect once, or we'd open a second set of
            // broker pipes and a second effect engine (LED blinking + session-cap "denied" crashes).
            if (_shellInitialized) return;
            _shellInitialized = true;
            RestoreWindowGeometry();   // size/position/maximized from last session (also clears any spurious startup-maximize)
            SeedCanvasLayout(_active); // assign the dashboard's canvas positions now the window/canvas is sized
            await TryAutoConnect();
        };
        Closing += (_, _) => { SaveSettings(); Cleanup(); _trayIcon?.Dispose(); _trayIcon = null; };
    }

    // ===================================================================
    //  Custom window chrome
    // ===================================================================
    private void OnChromeDrag(object? sender, PointerPressedEventArgs e)
    {
        // Don't start a window-move when the press lands on an interactive control —
        // otherwise the move-drag swallows the click (e.g. the mode combo would only
        // open on right-click, the theme toggle/window buttons would feel dead).
        if (IsInteractive(e.Source)) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private static bool IsInteractive(object? source)
    {
        if (source is not Visual v) return false;
        return v.FindAncestorOfType<Button>(true) is not null
            || v.FindAncestorOfType<ComboBox>(true) is not null
            || v.FindAncestorOfType<ToggleSwitch>(true) is not null
            || v.FindAncestorOfType<TextBox>(true) is not null;
    }
    private void OnChromeMaximize(object? sender, TappedEventArgs e) => ToggleMaximize();
    private void OnChromeMaximizeBtn(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnChromeMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnChromeClose(object? sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = ThemeToggle.IsChecked == true ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    // The developer's contact address for the "Send feedback" action.
    private const string FeedbackEmail = "TheUnboundDeveloper@outlook.com";

    // Project identity shown in the About dialog / GitHub button. Framework = the broker+driver
    // release; GUI = this reference console (versioned separately).
    private const string FrameworkVersion = "1.6.0";
    private const string GuiVersion = "0.1.0";
    private const string RepoUrl = "https://github.com/TheUnboundDeveloper/Register-Broker";

    private async void OnAbout(object? sender, RoutedEventArgs e)
        => await new AboutDialog(FrameworkVersion, GuiVersion).ShowDialog(this);

    private void OnOpenGitHub(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
            LogEvent("INFO", "Opened the project GitHub repository", null);
        }
        catch (Exception ex)
        {
            LogEvent("WARN", $"Could not open a browser ({ex.Message}). Repo: {RepoUrl}", null);
        }
    }

    private void OnSendFeedback(object? sender, RoutedEventArgs e)
    {
        const string subject = "Register Broker Reference Console — Feedback";
        var uri = $"mailto:{FeedbackEmail}?subject={Uri.EscapeDataString(subject)}";
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            LogEvent("INFO", $"Opened mail client to {FeedbackEmail}", null);
        }
        catch (Exception ex)
        {
            LogEvent("WARN", $"Could not open a mail client ({ex.Message}). Email feedback to {FeedbackEmail}", null);
        }
    }

    // ===================================================================
    //  Navigation
    // ===================================================================
    private void OnNavDashboard(object? sender, RoutedEventArgs e) => ShowPage(PageDashboard, NavDashboard);
    private void OnNavSensors(object? sender, RoutedEventArgs e) => ShowPage(PageSensors, NavSensors);
    private void OnNavRgb(object? sender, RoutedEventArgs e) => ShowPage(PageRgb, NavRgb);
    // Activity + Security are no longer standalone pages — their panels now live on the Settings page.
    private void OnNavSettings(object? sender, RoutedEventArgs e) => ShowPage(PageSettings, NavSettings);

    private void ShowPage(Control page, Button nav)
    {
        PageDashboard.IsVisible = page == PageDashboard;
        PageSensors.IsVisible = page == PageSensors;
        PageRgb.IsVisible = page == PageRgb;
        PageSettings.IsVisible = page == PageSettings;
        foreach (var sec in _sections)
            if (!sec.IsMain) sec.Page.IsVisible = ReferenceEquals(page, sec.Page);

        // The shared RGB panels follow the active view: on the RGB page while it's shown, otherwise
        // back in the dashboard cards.
        MountRgbPanels(page == PageRgb);
        foreach (var b in _navButtons) b.Classes.Remove("active");
        foreach (var sec in _sections)
            if (!sec.IsMain) sec.NavButton?.Classes.Remove("active");
        if (!nav.Classes.Contains("active")) nav.Classes.Add("active");
        if (page == PageSettings) OnSettingsShown();

        // Track the active dashboard-like section so toolbar/canvas ops target the right one, and
        // seed its canvas the first time it becomes visible (positions need a measured canvas).
        if (_sections.FirstOrDefault(s => ReferenceEquals(s.Page, page)) is { } shown)
        {
            _active = shown;
            Dispatcher.UIThread.Post(() => SeedCanvasLayout(shown), DispatcherPriority.Loaded);
        }
    }

    // The RGB device list + options editor are a single shared control set with two homes: the
    // dashboard cards and the RGB page. Reparent them rather than duplicating all the editor logic.
    private void MountRgbPanels(bool onRgbPage)
    {
        RgbPageDevicesHost.Content = null;
        RgbCardDevicesHost.Content = null;
        RgbPageControlHost.Content = null;
        RgbCardControlHost.Content = null;
        if (onRgbPage)
        {
            RgbPageDevicesHost.Content = RgbDevicesBody;
            RgbPageControlHost.Content = RgbControlBody;
        }
        else
        {
            RgbCardDevicesHost.Content = RgbDevicesBody;
            RgbCardControlHost.Content = RgbControlBody;
        }
    }

    // ===================================================================
    //  Window resize (borderless — edge grips drive BeginResizeDrag)
    // ===================================================================
    private void OnResizeNW(object? s, PointerPressedEventArgs e) => BeginResize(WindowEdge.NorthWest, e);
    private void OnResizeN(object? s, PointerPressedEventArgs e)  => BeginResize(WindowEdge.North, e);
    private void OnResizeNE(object? s, PointerPressedEventArgs e) => BeginResize(WindowEdge.NorthEast, e);
    private void OnResizeW(object? s, PointerPressedEventArgs e)  => BeginResize(WindowEdge.West, e);
    private void OnResizeE(object? s, PointerPressedEventArgs e)  => BeginResize(WindowEdge.East, e);
    private void OnResizeSW(object? s, PointerPressedEventArgs e) => BeginResize(WindowEdge.SouthWest, e);
    private void OnResizeS(object? s, PointerPressedEventArgs e)  => BeginResize(WindowEdge.South, e);
    private void OnResizeSE(object? s, PointerPressedEventArgs e) => BeginResize(WindowEdge.SouthEast, e);
    private void BeginResize(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Normal && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(edge, e);
    }

    // ===================================================================
    //  Dashboard boxes — drag (reorder/move), ✕ remove, resize grip, Grid/Free mode
    // ===================================================================
    private static Border? BoxById(DashSection s, string id) => s.Boxes.FirstOrDefault(b => (b.Tag as string) == id);
    private static string IdOf(Border b) => b.Tag as string ?? "";

    // Which section owns this card / canvas (handlers fire on the visible section, but resolving from
    // the element keeps them correct regardless of which section is "active").
    private DashSection? SectionOfBox(Border box) => _sections.FirstOrDefault(s => s.Boxes.Contains(box));
    private DashSection? SectionOfCanvas(Canvas c) => _sections.FirstOrDefault(s => ReferenceEquals(s.Canvas, c));

    // ===================================================================
    //  Local display-name overrides (cards / sensors / RGB devices)
    //
    //  Pure UI convenience: rename what the console shows, persisted to the
    //  settings file. Never sent to the broker, never touches a driver name.
    // ===================================================================

    /// <summary>Store (or clear) a local name for <paramref name="id"/> and persist it. A blank
    /// name, or one equal to the broker fallback, removes the override.</summary>
    private void SetCustomName(string id, string? name, string fallback)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || string.Equals(name, fallback, StringComparison.Ordinal))
            _settings.CustomNames.Remove(id);
        else
            _settings.CustomNames[id] = name;
        try { _settings.Save(); } catch (Exception ex) { Log("Name save failed: " + ex.Message); }
    }

    private void ClearCustomName(string id)
    {
        if (!_settings.CustomNames.Remove(id)) return;
        try { _settings.Save(); } catch (Exception ex) { Log("Name save failed: " + ex.Message); }
    }

    // ---- dashboard cards ----------------------------------------------------
    private string DefaultBoxTitle(string id) => BoxTitles.TryGetValue(id, out var t) ? t : id;

    // The title TextBlock lives in the card's drag bar at grid column 1 (column 0 = handle,
    // column 2 = the ✕). Resolve it through the logical tree so it works before realization.
    private static TextBlock? FindBoxTitle(Border box)
    {
        foreach (var grid in box.GetLogicalDescendants().OfType<Grid>())
            if (grid.Classes.Contains("dragbar"))
                return grid.Children.OfType<TextBlock>().FirstOrDefault(t => Grid.GetColumn(t) == 1);
        return null;
    }

    private void ApplyBoxTitle(Border box)
    {
        var id = IdOf(box);
        if (FindBoxTitle(box) is { } tb) tb.Text = LocalNames.Resolve(id, DefaultBoxTitle(id));
    }

    // Right-click any card for Rename / Reset; built-in cards get this in SetupDashboardBoxes,
    // runtime sensor cards in BuildSensorBox.
    private void AttachCardMenu(Border box)
    {
        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += async (_, _) => await BeginRenameCard(box);
        var reset = new MenuItem { Header = "Reset name" };
        reset.Click += (_, _) => { ClearCustomName(IdOf(box)); ApplyBoxTitle(box); };
        var menu = new ContextMenu();
        menu.Items.Add(rename);
        menu.Items.Add(reset);
        box.ContextMenu = menu;
    }

    private async Task BeginRenameCard(Border box)
    {
        var id = IdOf(box);
        if (id.Length == 0) return;
        var current = LocalNames.Resolve(id, DefaultBoxTitle(id));
        var name = await RenameDialog.Show(this, "Rename card", current);
        if (name is null) return;   // cancelled
        SetCustomName(id, name, DefaultBoxTitle(id));
        ApplyBoxTitle(box);
    }

    // ---- sensor rows --------------------------------------------------------
    private async void OnSensorRename(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SensorRow row) return;
        var name = await RenameDialog.Show(this, "Rename sensor", row.DisplayName);
        if (name is null) return;
        SetCustomName(row.Id, name, row.BrokerName);
        row.RaiseName();
    }

    private void OnSensorResetName(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SensorRow row) return;
        ClearCustomName(row.Id);
        row.RaiseName();
    }

    // ---- RGB devices --------------------------------------------------------
    private async void OnRgbRename(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not RgbRow row) return;
        var name = await RenameDialog.Show(this, "Rename RGB device", row.Label);
        if (name is null) return;
        SetCustomName(row.Id, name, row.BrokerName);
        row.RaiseName();
    }

    private void OnRgbResetName(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not RgbRow row) return;
        ClearCustomName(row.Id);
        row.RaiseName();
    }

    private void SetupDashboardBoxes()
    {
        // Drag-reorder of the section nav buttons. Handled on the host in BOTH the tunnel and bubble
        // phases with handledEventsToo, so the press/move/release always reach us no matter how the
        // child Button consumes the event. The drag captures the pointer to the host (not the dragged
        // button), so reordering a child never drops the in-flight capture.
        const RoutingStrategies both = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;
        SectionNavHost.AddHandler(InputElement.PointerPressedEvent, OnNavPressed, both, handledEventsToo: true);
        SectionNavHost.AddHandler(InputElement.PointerMovedEvent, OnNavMoved, both, handledEventsToo: true);
        SectionNavHost.AddHandler(InputElement.PointerReleasedEvent, OnNavReleased, both, handledEventsToo: true);

        var states = _settings.EnsureSections();

        // ---- the built-in "Dashboard" section wraps the XAML canvas + toolbar ----
        var mainState = states.First(s => s.Id == "main");
        var main = new DashSection
        {
            Id = "main", IsMain = true, State = mainState,
            Page = PageDashboard, Canvas = DashCanvas,
            NavButton = NavDashboard, GridBtn = GridModeBtn, FreeBtn = FreeModeBtn,
            AddBtn = AddBoxButton, LockToggle = DashLockToggle,
        };
        _sections.Add(main);
        _active = main;

        foreach (var b in DashGrid.Children.OfType<Border>().ToList())
        {
            main.Boxes.Add(b);
            if (b.Tag is string id) main.DefaultOrder.Add(id);
            InjectResizeGrip(main, b);
            AttachCardMenu(b);     // right-click → Rename / Reset
            ApplyBoxTitle(b);      // restore any saved local card name
        }

        // Everything lives on the absolute-positioned canvas now; the WrapPanel was only the XAML
        // authoring host. Both Grid and Free modes are canvas-based — Grid just snaps to a cell grid
        // instead of reflowing, so an arrangement is preserved when toggling modes.
        WireSectionCanvas(main);
        DashGrid.IsVisible = false;
        DashCanvas.IsVisible = true;
        ApplyDashLayout(main);
        SetDashLocked(main, main.Locked);   // restore this section's lock (independent of all other locks)

        // ---- restore any user-created sections (their cards are recreated once the catalog is known) ----
        foreach (var st in states.Where(s => s.Id != "main").ToList())
        {
            var sec = CreateSection(st);
            ApplyDashLayout(sec);
            SetDashLocked(sec, sec.Locked);
        }
        UpdateNextSectionNum();
    }

    // Move a section's authored boxes onto its canvas and wire the empty-space (marquee) handlers.
    private void WireSectionCanvas(DashSection s)
    {
        foreach (var b in s.Boxes.ToList())
        {
            if (ReferenceEquals(b.Parent, DashGrid)) DashGrid.Children.Remove(b);
            if (!s.Canvas.Children.Contains(b)) s.Canvas.Children.Add(b);
        }
        // A transparent background lets the canvas receive presses on empty space (for the marquee).
        s.Canvas.Background = Brushes.Transparent;
        s.Canvas.PointerPressed += OnCanvasPressed;
        s.Canvas.PointerMoved += OnCanvasMoved;
        s.Canvas.PointerReleased += OnCanvasReleased;
        CreateMarqueeVisual(s);
    }

    private void CreateMarqueeVisual(DashSection s)
    {
        var accent = (Res("Accent") as ISolidColorBrush)?.Color ?? Colors.MediumPurple;
        s.Marquee = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(36, accent.R, accent.G, accent.B)),
            BorderBrush = Res("Accent"),
            BorderThickness = new Avalonia.Thickness(1),
            IsHitTestVisible = false,
            IsVisible = false,
            ZIndex = 1000,
        };
        s.Canvas.Children.Add(s.Marquee);
    }

    // Wrap each box's content in a Grid so a bottom-right resize grip can overlay it.
    private void InjectResizeGrip(DashSection s, Border box)
    {
        var content = box.Child;
        box.Child = null;
        var g = new Grid();
        if (content is not null) g.Children.Add(content);

        var gripIcon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 16,4 L 16,16 L 4,16 Z M 16,9 L 11,16 L 16,16 Z"),
            Opacity = 0.85,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Avalonia.Thickness(0, 0, 2, 2),
        };
        BindThemeBrush(gripIcon, Avalonia.Controls.Shapes.Shape.FillProperty, "TextMuted");
        var grip = new Border { Classes = { "resizegrip" }, Tag = box, IsVisible = !s.Locked };
        grip.Child = gripIcon;
        grip.PointerPressed += OnGripPressed;
        grip.PointerMoved += OnGripMoved;
        grip.PointerReleased += OnGripReleased;
        g.Children.Add(grip);
        box.Child = g;
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border grip || grip.Tag is not Border box) return;
        if (SectionOfBox(box) is not { } s || s.Locked) return;   // this section's lock only
        _resizeBox = box;
        _resizeStartPtr = e.GetPosition(this);
        _resizeW = box.Bounds.Width;
        _resizeH = box.Bounds.Height;
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeBox is null || SectionOfBox(_resizeBox) is not { } s) return;
        var p = e.GetPosition(this);
        // Cap the size so the card's right/bottom edge stays inside the containment area.
        var a = AreaSize(s);
        double left = NanTo0(Canvas.GetLeft(_resizeBox));
        double top = NanTo0(Canvas.GetTop(_resizeBox));
        double maxW = Math.Max(160, a.Width - left);
        double maxH = Math.Max(96, a.Height - top);
        _resizeBox.Width = Math.Clamp(_resizeW + (p.X - _resizeStartPtr.X), 160, maxW);
        _resizeBox.Height = Math.Clamp(_resizeH + (p.Y - _resizeStartPtr.Y), 96, maxH);
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizeBox is null) return;
        var s = SectionOfBox(_resizeBox);
        _resizeBox = null;
        e.Pointer.Capture(null);
        UpdateCanvasHeight();
        if (s is not null) PersistDashLayout(s);
    }

    private Border? FindBoxAncestor(Visual? v)
    {
        while (v is not null)
        {
            if (v is Border b && SectionOfBox(b) is not null) return b;
            v = v.GetVisualParent();
        }
        return null;
    }

    // ===================================================================
    //  Dashboard "Lock cards" — freezes move + resize on the dashboard only.
    //  Wholly separate from the Settings-page lock (_settingsLocked): different
    //  state, toggle, and persisted flag, so neither one drives the other.
    // ===================================================================
    private void OnDashLockToggle(object? sender, RoutedEventArgs e)
    {
        // Resolve which section's toggle fired — each section owns its own lock, so this never
        // touches another section (or the Settings-page lock).
        var s = _sections.FirstOrDefault(x => ReferenceEquals(x.LockToggle, sender)) ?? _active;
        SetDashLocked(s, s.LockToggle?.IsChecked == true);
        try { _settings.Save(); } catch (Exception ex) { Log("Settings save failed: " + ex.Message); }
        LogEvent("INFO", $"{s.Name} cards {(s.Locked ? "locked" : "unlocked")}", null);
    }

    private void SetDashLocked(DashSection s, bool locked)
    {
        s.Locked = locked;   // persisted in s.State.Locked — scoped to this section only
        if (s.LockToggle is { } t && t.IsChecked != locked) t.IsChecked = locked;
        // Hide this section's card resize grips; the press guards (OnGripPressed / OnBoxDragStart /
        // OnCanvasPressed) block the actual move/resize/marquee while locked.
        foreach (var box in s.Boxes)
            foreach (var grip in box.GetLogicalDescendants().OfType<Border>()
                                    .Where(b => b.Classes.Contains("resizegrip")))
                grip.IsVisible = !locked;
    }

    private void OnBoxDragStart(object? sender, PointerPressedEventArgs e)
    {
        // Ignore presses on a button (✕ / Clear / Details) — includeSelf so a press *on* the
        // button is excluded too, otherwise we capture the pointer and steal the click.
        if (e.Source is Visual src && src.FindAncestorOfType<Button>(true) is not null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var box = FindBoxAncestor(sender as Visual);
        if (box is null || SectionOfBox(box) is not { } s || s.Locked) return;   // this section's lock only
        _dragBox = box;
        _dragStart = e.GetPosition(s.Canvas);
        _dragOrigLeft = NanTo0(Canvas.GetLeft(box));
        _dragOrigTop = NanTo0(Canvas.GetTop(box));

        // Dragging a card that's part of a multi-selection moves the whole group together.
        if (s.Selected.Contains(box) && s.Selected.Count > 1)
        {
            _groupDrag = true;
            _groupOrigins = s.Selected.ToDictionary(b => b,
                b => new Point(NanTo0(Canvas.GetLeft(b)), NanTo0(Canvas.GetTop(b))));
            foreach (var b in s.Selected) b.Opacity = 0.6;
        }
        else
        {
            _groupDrag = false;
            if (!s.Selected.Contains(box)) ClearSelection(s);   // dragging an unselected card -> single
            box.Opacity = 0.6;
        }

        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    private void OnBoxDragMove(object? sender, PointerEventArgs e)
    {
        if (_dragBox is null || SectionOfBox(_dragBox) is not { } s) return;
        var p = e.GetPosition(s.Canvas);
        double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;

        if (_groupDrag)
        {
            if (s.LayoutMode == "grid") { dx = Snap(dx); dy = Snap(dy); }
            // Clamp the shared delta against the group's bounding box so the group stays rigid and
            // no member crosses an edge.
            var a = AreaSize(s);
            double minLeft = double.MaxValue, minTop = double.MaxValue, maxRight = double.MinValue, maxBottom = double.MinValue;
            foreach (var (b, o) in _groupOrigins)
            {
                minLeft = Math.Min(minLeft, o.X); minTop = Math.Min(minTop, o.Y);
                maxRight = Math.Max(maxRight, o.X + BoxW(b)); maxBottom = Math.Max(maxBottom, o.Y + BoxH(b));
            }
            dx = Math.Clamp(dx, -minLeft, Math.Max(0, a.Width - maxRight));
            dy = Math.Clamp(dy, -minTop, Math.Max(0, a.Height - maxBottom));
            foreach (var (b, o) in _groupOrigins) { Canvas.SetLeft(b, o.X + dx); Canvas.SetTop(b, o.Y + dy); }
            return;
        }

        double nx = Math.Max(0, _dragOrigLeft + dx);
        double ny = Math.Max(0, _dragOrigTop + dy);
        // Grid mode snaps the moving box to the nearest grid line; Free mode is pixel-exact.
        if (s.LayoutMode == "grid") { nx = Snap(nx); ny = Snap(ny); }
        // Keep the whole card inside the containment area — its edges can't be crossed.
        (nx, ny) = ClampPos(s, _dragBox, nx, ny);
        Canvas.SetLeft(_dragBox, nx);
        Canvas.SetTop(_dragBox, ny);
    }

    private void OnBoxDragEnd(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragBox is null) return;
        var s = SectionOfBox(_dragBox);
        if (_groupDrag && s is not null) foreach (var b in s.Selected) b.Opacity = 1; else _dragBox.Opacity = 1;
        _dragBox = null;
        _groupDrag = false;
        e.Pointer.Capture(null);
        UpdateCanvasHeight();
        if (s is not null) PersistDashLayout(s);
    }

    // ===================================================================
    //  Marquee (rubber-band) selection on the empty canvas
    // ===================================================================
    private void OnCanvasPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Canvas canvas || SectionOfCanvas(canvas) is not { } s) return;
        if (s.Locked) return;   // locked: no marquee selection (selection only drives group move/resize)
        if (!e.GetCurrentPoint(s.Canvas).Properties.IsLeftButtonPressed) return;
        if (FindBoxAncestor(e.Source as Visual) is not null) return;   // press landed on a card

        ClearSelection(s);
        _marqueeActive = true;
        _marqueeStart = e.GetPosition(s.Canvas);
        if (s.Marquee is not null)
        {
            Canvas.SetLeft(s.Marquee, _marqueeStart.X);
            Canvas.SetTop(s.Marquee, _marqueeStart.Y);
            s.Marquee.Width = 0;
            s.Marquee.Height = 0;
            s.Marquee.IsVisible = true;
        }
        e.Pointer.Capture(s.Canvas);
        e.Handled = true;
    }

    private void OnCanvasMoved(object? sender, PointerEventArgs e)
    {
        if (!_marqueeActive || sender is not Canvas canvas || SectionOfCanvas(canvas) is not { Marquee: { } m } s) return;
        var p = e.GetPosition(s.Canvas);
        Canvas.SetLeft(m, Math.Min(p.X, _marqueeStart.X));
        Canvas.SetTop(m, Math.Min(p.Y, _marqueeStart.Y));
        m.Width = Math.Abs(p.X - _marqueeStart.X);
        m.Height = Math.Abs(p.Y - _marqueeStart.Y);
    }

    private void OnCanvasReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_marqueeActive || sender is not Canvas canvas || SectionOfCanvas(canvas) is not { } s) return;
        _marqueeActive = false;
        e.Pointer.Capture(null);
        if (s.Marquee is not null) s.Marquee.IsVisible = false;

        var p = e.GetPosition(s.Canvas);
        var rect = new Rect(Math.Min(p.X, _marqueeStart.X), Math.Min(p.Y, _marqueeStart.Y),
            Math.Abs(p.X - _marqueeStart.X), Math.Abs(p.Y - _marqueeStart.Y));
        if (rect.Width >= 5 || rect.Height >= 5)
            foreach (var b in s.Boxes)
            {
                if (!b.IsVisible || !s.Canvas.Children.Contains(b)) continue;
                var r = new Rect(NanTo0(Canvas.GetLeft(b)), NanTo0(Canvas.GetTop(b)), BoxW(b), BoxH(b));
                if (rect.Intersects(r)) Select(s, b);
            }
    }

    private static void Select(DashSection s, Border b)
    {
        if (s.Selected.Add(b) && !b.Classes.Contains("selected")) b.Classes.Add("selected");
    }

    private static void ClearSelection(DashSection s)
    {
        foreach (var b in s.Selected) b.Classes.Remove("selected");
        s.Selected.Clear();
    }

    private static double NanTo0(double d) => double.IsNaN(d) ? 0 : d;

    private void OnBoxRemove(object? sender, RoutedEventArgs e)
    {
        var box = FindBoxAncestor(sender as Visual);
        if (box is null || SectionOfBox(box) is not { } s) return;
        // A user-added sensor card is fully removed (so "Add box" offers it again); the built-in
        // boxes are only hidden so they keep their place/size when added back.
        if (IdOf(box).StartsWith(SensorBoxPrefix, StringComparison.Ordinal)) { DiscardSensorBox(s, box); return; }
        box.IsVisible = false;
        PersistDashLayout(s);
    }

    // Pop a menu under the button: re-add removed built-in boxes, plus add a new metric card for
    // any available sensor (grouped by category). Options are derived from the live sensor catalog.
    private void OnAddBox(object? sender, RoutedEventArgs e)
    {
        var s = _sections.FirstOrDefault(x => ReferenceEquals(x.AddBtn, sender)) ?? _active;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        // 1) removed built-in boxes. The sensor-backed metric boxes (CPU/VRM/System/GPU temps) are
        //    intentionally EXCLUDED here — they're redundant with the per-sensor lists in the tree
        //    below, where the same reading can be added. Only non-sensor built-ins (Broker Latency)
        //    and the panels (Sensors/RGB/Activity/Security) appear as "+" re-add entries — and ONLY
        //    for the built-in dashboard; user sections are filled purely from the per-sensor cards.
        var hiddenBuiltins = s.IsMain
            ? s.Boxes
                .Where(b => !b.IsVisible && !IdOf(b).StartsWith(SensorBoxPrefix, StringComparison.Ordinal))
                .Where(b => !SensorBackedBoxes.Contains(IdOf(b)))
                .ToList()
            : new List<Border>();
        foreach (var b in hiddenBuiltins)
        {
            var box = b;
            var id = IdOf(box);
            var mi = new MenuItem { Header = "＋  " + LocalNames.Resolve(id, DefaultBoxTitle(id)) };
            mi.Click += (_, _) => { box.IsVisible = true; PersistDashLayout(s); };
            flyout.Items.Add(mi);
        }

        // 2) a new card for any sensor that doesn't already have one, grouped by category.
        // Each sensor (unique id) is listed exactly once. Where two different sensors share the same
        // display name, disambiguate them by appending their raw id so they're clearly not duplicates.
        var candidates = _sensorRows.Where(r => !s.SensorViews.ContainsKey(r.Id)).ToList();
        var nameCounts = candidates
            .GroupBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var groups = candidates
            .GroupBy(r => GroupOf(r.Id, r.Label, r.Unit))
            .OrderBy(g => GroupRank(g.Key))
            .ToList();

        if (hiddenBuiltins.Count > 0 && groups.Count > 0)
            flyout.Items.Add(new Separator());

        foreach (var g in groups)
        {
            var sub = new MenuItem { Header = g.Key };
            foreach (var r in g.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var row = r;
                bool ambiguous = nameCounts.TryGetValue(row.DisplayName, out var c) && c > 1;
                var mi = new MenuItem { Header = ambiguous ? $"{row.DisplayName}  ·  {row.Id}" : row.DisplayName };
                mi.Click += (_, _) => AddSensorBox(s, row);
                sub.Items.Add(mi);
            }
            flyout.Items.Add(sub);
        }

        if (flyout.Items.Count == 0)
        {
            string msg = _sensors is null ? "Connect to add sensor boxes" : "All available boxes are shown";
            flyout.Items.Add(new MenuItem { Header = msg, IsEnabled = false });
        }
        flyout.ShowAt(s.AddBtn ?? AddBoxButton);
    }

    // ===================================================================
    //  Dynamic per-sensor dashboard cards
    // ===================================================================
    // Box id for a sensor card. The built-in dashboard keeps the legacy "sensor:<id>" so existing
    // saved layouts still match; user sections namespace by section id so the same sensor can appear
    // in more than one section, each card tracked and persisted independently.
    private static string SensorBoxId(DashSection s, string sensorId)
        => s.IsMain ? SensorBoxPrefix + sensorId : $"{SensorBoxPrefix}{s.Id}:{sensorId}";

    private void AddSensorBox(DashSection s, SensorRow row)
    {
        if (s.SensorViews.ContainsKey(row.Id))
        {
            if (BoxById(s, SensorBoxId(s, row.Id)) is { } existing) existing.IsVisible = true;
            return;
        }
        var box = BuildSensorBox(s, row.Id, row.DisplayName, row.Unit, GroupOf(row.Id, row.Label, row.Unit));
        InjectResizeGrip(s, box);
        s.Boxes.Add(box);
        PlaceDynamicBox(s, box);
        if (!s.State.SensorBoxes.Contains(row.Id)) s.State.SensorBoxes.Add(row.Id);

        // Seed the value immediately from what the row already holds, then live polling keeps it current.
        if (s.SensorViews.TryGetValue(row.Id, out var view)
            && double.TryParse(row.ValueText, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            SetCard(view.Val, view.Spark, v);

        PersistDashLayout(s);
        LogEvent("INFO", $"Added sensor box: {row.DisplayName}", null);
    }

    private void DiscardSensorBox(DashSection s, Border box)
    {
        var id = IdOf(box);
        var sensorId = s.BoxSensorId.TryGetValue(box, out var sid) ? sid : "";
        s.SensorViews.Remove(sensorId);
        s.BoxSensorId.Remove(box);
        s.State.SensorBoxes.Remove(sensorId);
        BoxTitles.Remove(id);
        _settings.CustomNames.Remove(id);   // drop any local card name; full removal starts fresh
        s.Boxes.Remove(box);
        if (s.Canvas.Children.Contains(box)) s.Canvas.Children.Remove(box);
        PersistDashLayout(s);
    }

    // Recreate every section's saved sensor cards once the catalog is known (called after a connect+read).
    private void RecreateSensorBoxes()
    {
        var byId = _sensorRows.ToDictionary(r => r.Id);
        foreach (var s in _sections)
            foreach (var sid in s.State.SensorBoxes.ToList())
            {
                if (s.SensorViews.ContainsKey(sid)) continue;
                if (!byId.TryGetValue(sid, out var row)) continue;   // sensor not present on this box
                var box = BuildSensorBox(s, sid, row.DisplayName, row.Unit, GroupOf(sid, row.Label, row.Unit));
                InjectResizeGrip(s, box);
                s.Boxes.Add(box);
                PlaceDynamicBox(s, box);
            }
    }

    // Build a metric card (drag bar + ✕, value + unit + sparkline) matching the XAML metric boxes.
    private Border BuildSensorBox(DashSection s, string sensorId, string label, string unit, string group)
    {
        var id = SensorBoxId(s, sensorId);
        BoxTitles[id] = label;
        var accent = AccentForGroup(group);
        var badgeText = BadgeFor(group);
        // GPU sensor cards are tinted by vendor (AMD red / NVIDIA green / Intel blue), matching the
        // GPU overview card, so every gpu.* reading on the dashboard reads as the same vendor colour.
        if (sensorId.StartsWith("gpu", StringComparison.OrdinalIgnoreCase) && GpuVendor.Kind != GpuVendorKind.None)
        {
            accent = new SolidColorBrush(GpuVendor.AccentColor(GpuVendor.Kind));
            badgeText = "GPU";
        }

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        bar.Classes.Add("dragbar");
        DockPanel.SetDock(bar, Dock.Top);
        bar.PointerPressed += OnBoxDragStart;
        bar.PointerMoved += OnBoxDragMove;
        bar.PointerReleased += OnBoxDragEnd;

        var handle = new TextBlock { Text = "⠿", Margin = new Avalonia.Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center };
        BindThemeBrush(handle, TextBlock.ForegroundProperty, "TextMuted");
        handle.Classes.Add("handle");
        Grid.SetColumn(handle, 0);
        var title = new TextBlock { Text = LocalNames.Resolve(id, label), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis };
        title.Classes.Add("sub");
        Grid.SetColumn(title, 1);
        var close = new Button { Content = "✕" };
        close.Classes.Add("iconbtn");
        close.Click += OnBoxRemove;
        Grid.SetColumn(close, 2);
        bar.Children.Add(handle); bar.Children.Add(title); bar.Children.Add(close);

        var badgeColor = (accent as ISolidColorBrush)?.Color ?? Colors.Gray;
        var badge = new Border { Width = 26, Height = 26, CornerRadius = new Avalonia.CornerRadius(7),
            Background = new SolidColorBrush(Color.FromArgb(40, badgeColor.R, badgeColor.G, badgeColor.B)),
            Child = new TextBlock { Text = badgeText, FontSize = 9, FontWeight = FontWeight.Bold,
                Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center } };
        var val = new TextBlock { Text = "—", FontSize = 26, FontWeight = FontWeight.Bold };
        BindThemeBrush(val, TextBlock.ForegroundProperty, "TextPrimary");
        var unitTb = new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Avalonia.Thickness(0, 0, 0, 5) };
        unitTb.Classes.Add("sub");
        var valRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        valRow.Children.Add(badge); valRow.Children.Add(val); valRow.Children.Add(unitTb);

        var spark = new Sparkline { Height = 32, Stroke = accent };
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(valRow); body.Children.Add(spark);

        var dock = new DockPanel();
        dock.Children.Add(bar);
        dock.Children.Add(body);

        var box = new Border { Classes = { "card", "box" }, Tag = id, Width = 200,
            Margin = new Avalonia.Thickness(0, 0, 14, 14), Child = dock };

        s.SensorViews[sensorId] = (val, spark);
        s.BoxSensorId[box] = sensorId;
        AttachCardMenu(box);     // right-click → Rename / Reset
        return box;
    }

    // Drop a runtime box onto the canvas: at its saved spot if known, otherwise in the nearest empty
    // area (so a new card never lands on top of the first box in the corner).
    private void PlaceDynamicBox(DashSection s, Border box)
    {
        var id = IdOf(box);
        if (!s.Canvas.Children.Contains(box)) s.Canvas.Children.Add(box);

        if (s.State.Sizes is { } sz && sz.TryGetValue(id, out var wh) && wh.Length == 2)
        {
            if (wh[0] > 0) box.Width = wh[0];
            if (wh[1] > 0) box.Height = wh[1];
        }

        double x, y;
        if (s.State.Free is { } map && map.TryGetValue(id, out var xy) && xy.Length == 2)
            (x, y) = (xy[0], xy[1]);
        else
        {
            var slot = FindEmptySlot(s, BoxW(box), BoxH(box), box);
            (x, y) = (slot.X, slot.Y);
        }
        if (s.LayoutMode == "grid") { x = Snap(x); y = Snap(y); }
        (x, y) = ClampPos(s, box, x, y);   // never place a card across an edge
        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
    }

    private IBrush AccentForGroup(string group) => group switch
    {
        "CPU" => Res("Accent"),
        "Voltages" => Res("Orange"),
        "Fans" => Res("Cyan"),
        "Motherboard" => Res("Blue"),
        _ => Res("Green"),
    };

    private static string BadgeFor(string group) => group switch
    {
        "CPU" => "CPU", "Voltages" => "V", "Fans" => "FAN", "Motherboard" => "MB", _ => "SEN",
    };

    // ===== layout mode (snap-to-grid vs. free placement; both on the canvas) =====
    private const double GridSnap = 20;   // grid cell size boxes snap to in Grid mode
    private const double BoxGap = 14;      // default gap used by the auto-flow / empty-slot finder

    private void OnGridMode(object? sender, RoutedEventArgs e) => SetLayoutMode(SectionOfModeButton(sender), "grid");
    private void OnFreeMode(object? sender, RoutedEventArgs e) => SetLayoutMode(SectionOfModeButton(sender), "free");

    private DashSection SectionOfModeButton(object? sender)
        => _sections.FirstOrDefault(x => ReferenceEquals(x.GridBtn, sender) || ReferenceEquals(x.FreeBtn, sender)) ?? _active;

    private static double Snap(double v) => Math.Round(v / GridSnap) * GridSnap;

    private void SetModeFlag(DashSection s, string mode)
    {
        s.LayoutMode = mode == "free" ? "free" : "grid";   // persisted in s.State.LayoutMode
        s.GridBtn?.Classes.Set("on", s.LayoutMode == "grid");
        s.FreeBtn?.Classes.Set("on", s.LayoutMode == "free");
    }

    // Switching to Grid snaps every box's *current* position to the nearest grid line — it never
    // reflows or reorders, so the spatial arrangement the user built is preserved.
    private void SetLayoutMode(DashSection s, string mode)
    {
        SetModeFlag(s, mode);
        if (s.LayoutMode == "grid")
        {
            foreach (var b in s.Boxes)
            {
                Canvas.SetLeft(b, Snap(NanTo0(Canvas.GetLeft(b))));
                Canvas.SetTop(b, Snap(NanTo0(Canvas.GetTop(b))));
            }
            UpdateCanvasHeight();
        }
        PersistDashLayout(s);
    }

    // Reset is shared by the main toolbar, each section's toolbar, and the Settings "Reset dashboard
    // layout" button — the first two carry the section on the button; everything else means "main".
    private void OnResetLayout(object? sender, RoutedEventArgs e)
        => ResetSectionLayout(_sections.FirstOrDefault(x => ReferenceEquals(x.ResetBtn, sender))
                              ?? _sections.First(x => x.IsMain));

    private void ResetSectionLayout(DashSection s)
    {
        s.State.Free = null;
        s.State.Sizes = null;
        foreach (var b in s.Boxes)
        {
            b.IsVisible = true;
            b.ClearValue(WidthProperty);
            b.ClearValue(HeightProperty);
        }
        ApplyDefaultSizes(s);
        SetModeFlag(s, "grid");
        FlowLayout(s, ContainerWidth(s));
        PersistDashLayout(s);
    }

    // Default box footprints (match the XAML authored sizes) used by Reset layout + auto-flow.
    private static readonly Dictionary<string, double[]> DefaultSizes = new()
    {
        ["cpu"] = new[] { 200d, double.NaN }, ["gpu"] = new[] { 200d, double.NaN },
        ["vrm"] = new[] { 200d, double.NaN }, ["sys"] = new[] { 200d, double.NaN },
        ["lat"] = new[] { 200d, double.NaN }, ["sensors"] = new[] { 450d, 470d },
        ["devices"] = new[] { 210d, 470d }, ["control"] = new[] { 430d, 470d },
        ["activity"] = new[] { 690d, 280d }, ["security"] = new[] { 380d, 280d },
    };

    private void ApplyDefaultSizes(DashSection s)
    {
        foreach (var b in s.Boxes)
        {
            if (DefaultSizes.TryGetValue(IdOf(b), out var wh))
            {
                b.Width = wh[0];
                if (double.IsNaN(wh[1])) b.ClearValue(HeightProperty); else b.Height = wh[1];
            }
            else b.Width = 200;   // dynamic sensor cards
        }
    }

    private void ApplyDashLayout(DashSection s)
    {
        // sizes
        if (s.State.Sizes is { } sizes)
            foreach (var (id, wh) in sizes)
                if (BoxById(s, id) is { } b && wh.Length == 2)
                {
                    if (wh[0] > 0) b.Width = wh[0];
                    if (wh[1] > 0) b.Height = wh[1];
                }

        // hidden
        if (s.State.Hidden is { Count: > 0 })
            foreach (var id in s.State.Hidden)
                if (BoxById(s, id) is { } b) b.IsVisible = false;

        // mode flag only — actual positions are seeded once the canvas is sized (see SeedCanvasLayout).
        SetModeFlag(s, s.LayoutMode);
    }

    // Live size of the containment area (the dashboard's fill row). Adapts to the window/monitor: a
    // bigger window — up to the monitor resolution — yields a bigger usable area. The canvas may not
    // be measured yet at the first Opened, so fall back to the window size minus chrome.
    private Size AreaSize(DashSection s)
    {
        double w = s.Canvas.Bounds.Width;
        double h = s.Canvas.Bounds.Height;
        if (w < 50) w = Math.Max(800, Bounds.Width - 212 - 48);
        if (h < 50) h = Math.Max(360, Bounds.Height - 180);
        return new Size(w, h);
    }

    private double ContainerWidth(DashSection s) => AreaSize(s).Width;

    // Clamp a position so the whole card stays inside the containment area (its edges can't be crossed).
    private (double X, double Y) ClampPos(DashSection s, Border box, double x, double y)
    {
        var a = AreaSize(s);
        double maxX = Math.Max(0, a.Width - BoxW(box));
        double maxY = Math.Max(0, a.Height - BoxH(box));
        return (Math.Clamp(x, 0, maxX), Math.Clamp(y, 0, maxY));
    }

    // NOTE: cards are intentionally NOT repositioned when the window resizes — shrinking the window
    // must never move or crush the cards. A card only leaves the viewport when the window is smaller
    // than the area the card was placed in (i.e. smaller than the monitor); it is clipped, not moved.

    private static double BoxW(Border b)
    {
        if (!double.IsNaN(b.Width)) return b.Width;
        if (b.Bounds.Width > 1) return b.Bounds.Width;
        return DefaultSizes.TryGetValue(IdOf(b), out var wh) ? wh[0] : 200;
    }

    private static double BoxH(Border b)
    {
        if (!double.IsNaN(b.Height)) return b.Height;
        if (b.Bounds.Height > 1) return b.Bounds.Height;
        return DefaultBoxHeight(IdOf(b));
    }

    private static double DefaultBoxHeight(string id) => id switch
    {
        "sensors" or "devices" or "control" => 470,
        "activity" or "security" => 280,
        _ => 132,   // metric + sensor cards
    };

    // Visible boxes in their canonical order (default order first, then any user-added sensor cards).
    private IEnumerable<Border> OrderedVisibleBoxes(DashSection s)
    {
        var seen = new HashSet<Border>();
        foreach (var id in s.DefaultOrder)
            if (BoxById(s, id) is { IsVisible: true } b) { seen.Add(b); yield return b; }
        foreach (var b in s.Boxes)
            if (b.IsVisible && seen.Add(b)) yield return b;
    }

    // Auto-arrange visible boxes left-to-right, wrapping at the container width (the default layout).
    private void FlowLayout(DashSection s, double containerWidth)
    {
        double x = 0, y = 0, rowH = 0;
        foreach (var b in OrderedVisibleBoxes(s))
        {
            double w = BoxW(b), h = BoxH(b);
            if (x > 0 && x + w > containerWidth) { x = 0; y += rowH + BoxGap; rowH = 0; }
            Canvas.SetLeft(b, x);
            Canvas.SetTop(b, y);
            x += w + BoxGap;
            rowH = Math.Max(rowH, h);
        }
        UpdateCanvasHeight();
    }

    // Position every box once the canvas size is known: from saved positions if present, else auto-flow.
    private void SeedCanvasLayout(DashSection s)
    {
        if (s.Seeded) return;
        s.Seeded = true;
        if (s.State.Free is { Count: > 0 } saved)
        {
            foreach (var b in s.Boxes)
                if (saved.TryGetValue(IdOf(b), out var xy) && xy.Length == 2)
                { Canvas.SetLeft(b, xy[0]); Canvas.SetTop(b, xy[1]); }
            UpdateCanvasHeight();
        }
        else FlowLayout(s, ContainerWidth(s));
    }

    // Find the top-most / left-most empty cell that fits a w×h box, searching only *inside* the
    // containment area so a new card is never placed across an edge.
    private Point FindEmptySlot(DashSection s, double w, double h, Border? except)
    {
        var a = AreaSize(s);
        double maxX = Math.Max(0, a.Width - w);
        double maxY = Math.Max(0, a.Height - h);
        var rects = s.Boxes
            .Where(b => b.IsVisible && !ReferenceEquals(b, except) && s.Canvas.Children.Contains(b))
            .Select(b => new Rect(NanTo0(Canvas.GetLeft(b)), NanTo0(Canvas.GetTop(b)), BoxW(b), BoxH(b)))
            .ToList();

        for (double y = 0; y <= maxY + 0.5; y += GridSnap)
            for (double x = 0; x <= maxX + 0.5; x += GridSnap)
            {
                var probe = new Rect(x - 2, y - 2, w + 4, h + 4);
                bool hit = false;
                foreach (var o in rects) if (o.Intersects(probe)) { hit = true; break; }
                if (!hit) return new Point(x, y);
            }
        // No free spot inside the area — drop it at the bottom-left, still within the bounds.
        return new Point(0, maxY);
    }

    // The canvas now fills the dashboard's fill row, so no manual height growth is needed; cards are
    // kept inside the area by clamping instead. (Retained as a hook for the existing call sites.)
    private void UpdateCanvasHeight() { }

    private void PersistDashLayout(DashSection s)
    {
        s.State.Hidden = s.Boxes.Where(b => !b.IsVisible).Select(IdOf).Where(id => id.Length > 0).ToList();

        s.State.Sizes = s.Boxes.ToDictionary(IdOf, b => new[]
        {
            double.IsNaN(b.Width) ? -1 : b.Width,
            double.IsNaN(b.Height) ? -1 : b.Height,
        });

        s.State.Free = s.Boxes.Where(b => s.Canvas.Children.Contains(b))
            .ToDictionary(IdOf, b => new[] { NanTo0(Canvas.GetLeft(b)), NanTo0(Canvas.GetTop(b)) });

        try { _settings.Save(); } catch (Exception ex) { Log("Layout save failed: " + ex.Message); }
    }

    // ===================================================================
    //  User-created sections (+ Add Section)
    //
    //  A section is a full dashboard page of its own — same drag / resize / Grid-Free / marquee /
    //  Add-Card / Reset toolset as the built-in Dashboard, plus rename, delete, and its OWN lock.
    //  Each appears as a sidebar nav entry. The lock is scoped to the section: it shares no state
    //  with the Dashboard lock, any other section's lock, or the Settings-page lock.
    // ===================================================================
    private void OnAddSection(object? sender, RoutedEventArgs e)
    {
        var num = _nextSectionNum++;
        var state = new DashSectionState { Id = $"sec:{num}", Name = $"Section {num}", LayoutMode = "grid" };
        _settings.EnsureSections().Add(state);

        var sec = CreateSection(state);
        ApplyDashLayout(sec);
        SetDashLocked(sec, false);
        try { _settings.Save(); } catch (Exception ex) { Log("Section save failed: " + ex.Message); }
        LogEvent("INFO", $"Added section: {sec.Name}", null);
        ShowPage(sec.Page, sec.NavButton!);   // jump to the new (empty) section
    }

    // Build a user section's page (toolbar + card canvas) and its sidebar nav button, mirroring the
    // built-in Dashboard's layout so every card behaves identically.
    private DashSection CreateSection(DashSectionState state)
    {
        var sec = new DashSection { Id = state.Id, IsMain = false, State = state };

        var title = new TextBlock { Text = sec.Name, VerticalAlignment = VerticalAlignment.Center };
        title.Classes.Add("h1");
        sec.TitleText = title;

        sec.GridBtn = SegButton("▦ Grid", true);
        sec.FreeBtn = SegButton("✥ Free", false);
        sec.AddBtn = GhostButton("＋ Add Card");
        sec.ResetBtn = GhostButton("↺ Reset layout");
        var renameBtn = GhostButton("✎ Rename");
        var deleteBtn = GhostButton("🗑 Delete");
        renameBtn.Tag = sec; deleteBtn.Tag = sec;
        var lockLabel = new TextBlock { Text = "🔒 Lock cards", VerticalAlignment = VerticalAlignment.Center };
        lockLabel.Classes.Add("sub");
        sec.LockToggle = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center };

        sec.GridBtn.Click += OnGridMode;
        sec.FreeBtn.Click += OnFreeMode;
        sec.AddBtn.Click += OnAddBox;
        sec.ResetBtn.Click += OnResetLayout;
        renameBtn.Click += OnRenameSection;
        deleteBtn.Click += OnDeleteSection;
        sec.LockToggle.Click += OnDashLockToggle;

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in new Control[] { sec.GridBtn, sec.FreeBtn, sec.AddBtn, sec.ResetBtn,
                                          renameBtn, deleteBtn, lockLabel, sec.LockToggle })
            tools.Children.Add(c);
        Grid.SetColumn(tools, 1);

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Avalonia.Thickness(0, 0, 0, 12) };
        Grid.SetColumn(title, 0);
        toolbar.Children.Add(title);
        toolbar.Children.Add(tools);
        Grid.SetRow(toolbar, 0);

        // Containment area: cards live on the Canvas; ClipToBounds keeps them inside (mirrors XAML).
        sec.Canvas = new Canvas();
        var clip = new Panel { ClipToBounds = true };
        clip.Children.Add(sec.Canvas);
        Grid.SetRow(clip, 1);

        var pageGrid = new Grid { Margin = new Avalonia.Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*"), IsVisible = false };
        pageGrid.Children.Add(toolbar);
        pageGrid.Children.Add(clip);
        sec.Page = pageGrid;
        ContentHost.Children.Add(pageGrid);

        var nav = new Button { Content = "▦   " + sec.Name };
        nav.Classes.Add("nav");
        // Both navigation AND reorder are driven from the host pointer handlers (OnNavReleased): a
        // release with no drag opens the section, a release after a drag commits the new order. We do
        // NOT use Button.Click — after a drag steals pointer capture, Click becomes unreliable.
        ToolTip.SetTip(nav, "Click to open · drag to reorder");
        sec.NavButton = nav;
        SectionNavHost.Children.Add(nav);

        WireSectionCanvas(sec);
        _sections.Add(sec);
        return sec;
    }

    private static Button SegButton(string text, bool on)
    {
        var b = new Button { Content = text };
        b.Classes.Add("seg");
        if (on) b.Classes.Add("on");
        return b;
    }

    private static Button GhostButton(string text)
    {
        var b = new Button { Content = text };
        b.Classes.Add("ghost");
        return b;
    }

    private async void OnRenameSection(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DashSection sec) return;
        var name = await RenameDialog.Show(this, "Rename section", sec.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        sec.Name = name.Trim();
        if (sec.TitleText is { } t) t.Text = sec.Name;
        if (sec.NavButton is { } nb) nb.Content = "▦   " + sec.Name;
        try { _settings.Save(); } catch (Exception ex) { Log("Section save failed: " + ex.Message); }
        LogEvent("INFO", $"Renamed section: {sec.Name}", null);
    }

    private void OnDeleteSection(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DashSection sec || sec.IsMain) return;

        foreach (var b in sec.Boxes.ToList())
        {
            BoxTitles.Remove(IdOf(b));
            _settings.CustomNames.Remove(IdOf(b));
        }
        sec.Canvas.Children.Clear();
        ContentHost.Children.Remove(sec.Page);
        if (sec.NavButton is { } nb) SectionNavHost.Children.Remove(nb);
        _sections.Remove(sec);
        _settings.DashSections?.RemoveAll(st => st.Id == sec.Id);
        if (ReferenceEquals(_active, sec)) _active = _sections.First(s => s.IsMain);
        try { _settings.Save(); } catch (Exception ex) { Log("Section save failed: " + ex.Message); }
        LogEvent("INFO", $"Deleted section: {sec.Name}", null);
        ShowPage(PageDashboard, NavDashboard);
    }

    private void UpdateNextSectionNum()
    {
        int max = 0;
        foreach (var s in _sections)
            if (!s.IsMain && s.Id.StartsWith("sec:", StringComparison.Ordinal)
                && int.TryParse(s.Id.AsSpan(4), out var n))
                max = Math.Max(max, n);
        _nextSectionNum = max + 1;
    }

    // ---- drag-to-reorder the section nav buttons ----
    // Find the section nav button the pointer is over (e.Source is usually a child of the Button's
    // template — ContentPresenter / TextBlock — so walk up to the owning nav button).
    private Button? NavButtonFromSource(object? source)
    {
        var v = source as Visual;
        while (v is not null)
        {
            if (v is Button b && _sections.Any(s => !s.IsMain && ReferenceEquals(s.NavButton, b))) return b;
            v = v.GetVisualParent();
        }
        return null;
    }

    private void OnNavPressed(object? sender, PointerPressedEventArgs e)
    {
        if (NavButtonFromSource(e.Source) is not { } b) return;
        if (!e.GetCurrentPoint(SectionNavHost).Properties.IsLeftButtonPressed) return;
        _navDragBtn = b;
        _navDragStart = e.GetPosition(SectionNavHost);
        _navDragging = false;
        // Don't capture yet — wait for the move threshold so a plain click still navigates.
    }

    private void OnNavMoved(object? sender, PointerEventArgs e)
    {
        if (_navDragBtn is null) return;
        var p = e.GetPosition(SectionNavHost);
        if (!_navDragging)
        {
            if (Math.Abs(p.Y - _navDragStart.Y) < 6) return;   // below threshold: still a potential click
            _navDragging = true;
            _navDragBtn.Opacity = 0.6;
            // Capture to the host, not the dragged button: moving the button within the host while it
            // holds capture would cancel the capture and abort the drag.
            e.Pointer.Capture(SectionNavHost);
        }
        ReorderNavTo(p.Y);
    }

    private void OnNavReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_navDragBtn is null) return;
        var btn = _navDragBtn;
        bool dragged = _navDragging;
        btn.Opacity = 1;
        e.Pointer.Capture(null);
        _navDragBtn = null;
        _navDragging = false;
        e.Handled = true;   // we own the click/drag on these buttons; don't let Button.Click double-fire

        if (dragged)
        {
            CommitNavOrder();
        }
        else if (_sections.FirstOrDefault(s => ReferenceEquals(s.NavButton, btn)) is { } sec)
        {
            // No movement → a plain click: open the section. (Navigation lives here, not Button.Click,
            // because capture-stealing during a drag makes Click unreliable afterwards.)
            ShowPage(sec.Page, sec.NavButton!);
        }
    }

    // Live-reorder the dragged button within SectionNavHost from the pointer's Y position.
    private void ReorderNavTo(double y)
    {
        if (_navDragBtn is null) return;
        var host = SectionNavHost;
        int cur = host.Children.IndexOf(_navDragBtn);
        if (cur < 0) return;
        int target = 0;
        foreach (var child in host.Children)
        {
            if (ReferenceEquals(child, _navDragBtn)) continue;
            if (y > child.Bounds.Y + child.Bounds.Height / 2) target++;
        }
        target = Math.Clamp(target, 0, host.Children.Count - 1);
        if (target != cur)
        {
            host.Children.RemoveAt(cur);
            host.Children.Insert(target, _navDragBtn);
        }
    }

    // Persist the new order: the sidebar button order becomes the section order (built-in "main"
    // always stays first), so it survives a restart.
    private void CommitNavOrder()
    {
        var ordered = new List<DashSection>();
        foreach (var child in SectionNavHost.Children)
            if (_sections.FirstOrDefault(s => ReferenceEquals(s.NavButton, child)) is { } s)
                ordered.Add(s);

        var main = _sections.First(s => s.IsMain);
        _sections.Clear();
        _sections.Add(main);
        _sections.AddRange(ordered);

        var states = _settings.EnsureSections();
        states.Clear();
        states.Add(main.State);
        foreach (var s in ordered) states.Add(s.State);

        try { _settings.Save(); } catch (Exception ex) { Log("Section order save failed: " + ex.Message); }
        LogEvent("INFO", "Reordered sections", null);
    }

    // ===================================================================
    //  Settings page — drag-move / resize cards on a canvas (+ "Lock cards")
    //
    //  A self-contained parallel to the dashboard's canvas system: the settings cards are
    //  absolute-positioned on SettingsCanvas, draggable by any non-interactive part of the card,
    //  resizable via a bottom-right grip, and frozen by the Lock toggle. Positions/sizes/lock state
    //  persist to the settings file.
    // ===================================================================
    private static string SIdOf(Border b) => b.Tag as string ?? "";

    private void SetupSettingsCards()
    {
        _settingsCards.AddRange(SettingsCanvas.Children.OfType<Border>());
        foreach (var card in _settingsCards)
        {
            InjectSettingsGrip(card);
            // Drag from the card's header handle (the ⠿ bar) — same affordance as the dashboard.
            if (FindSettingsDragBar(card) is { } bar)
            {
                _settingsDragBars.Add(bar);
                bar.PointerPressed += OnSettingsCardPressed;
                bar.PointerMoved += OnSettingsCardMoved;
                bar.PointerReleased += OnSettingsCardReleased;
            }
        }

        // restore saved sizes
        if (_settings.SettingsSizes is { } sizes)
            foreach (var card in _settingsCards)
                if (sizes.TryGetValue(SIdOf(card), out var wh) && wh.Length == 2)
                {
                    if (wh[0] > 0) card.Width = wh[0];
                    if (wh[1] > 0) card.Height = wh[1];
                }

        // restore saved positions (auto-flow is deferred to the first show otherwise)
        if (_settings.SettingsFree is { Count: > 0 } free)
        {
            foreach (var card in _settingsCards)
                if (free.TryGetValue(SIdOf(card), out var xy) && xy.Length == 2)
                { Canvas.SetLeft(card, xy[0]); Canvas.SetTop(card, xy[1]); }
            _settingsSeeded = true;
        }

        SetSettingsLocked(_settings.SettingsLocked);
    }

    // Overlay a bottom-right resize grip on a card (mirrors the dashboard grip).
    private void InjectSettingsGrip(Border card)
    {
        var content = card.Child;
        card.Child = null;
        var g = new Grid();
        if (content is not null) g.Children.Add(content);

        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 16,4 L 16,16 L 4,16 Z M 16,9 L 11,16 L 16,16 Z"),
            Opacity = 0.85,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Avalonia.Thickness(0, 0, 2, 2),
        };
        BindThemeBrush(icon, Avalonia.Controls.Shapes.Shape.FillProperty, "TextMuted");
        var grip = new Border { Classes = { "resizegrip" }, Tag = card, Child = icon };
        grip.PointerPressed += OnSettingsGripPressed;
        grip.PointerMoved += OnSettingsGripMoved;
        grip.PointerReleased += OnSettingsGripReleased;
        g.Children.Add(grip);
        card.Child = g;
        _settingsGrips.Add(grip);
    }

    // The header drag bar (Classes="sdragbar") within a card, and the card that owns a visual.
    private static Control? FindSettingsDragBar(Border card)
        => card.GetLogicalDescendants().OfType<Control>().FirstOrDefault(c => c.Classes.Contains("sdragbar"));

    private Border? FindSettingsCardAncestor(Visual? v)
    {
        while (v is not null)
        {
            if (v is Border b && _settingsCards.Contains(b)) return b;
            v = v.GetVisualParent();
        }
        return null;
    }

    // Ignore presses on an interactive control inside the bar (e.g. the Activity card's header buttons).
    private static bool IsSettingsInteractive(object? source)
    {
        if (source is not Visual v) return false;
        return v.FindAncestorOfType<Button>(true) is not null
            || v.FindAncestorOfType<ComboBox>(true) is not null
            || v.FindAncestorOfType<ToggleSwitch>(true) is not null
            || v.FindAncestorOfType<TextBox>(true) is not null;
    }

    private void OnSettingsCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_settingsLocked) return;
        if (IsSettingsInteractive(e.Source)) return;
        var card = FindSettingsCardAncestor(sender as Visual);
        if (card is null) return;
        if (!e.GetCurrentPoint(SettingsCanvas).Properties.IsLeftButtonPressed) return;
        _sDragCard = card;
        _sDragStart = e.GetPosition(SettingsCanvas);
        _sDragOrigLeft = NanTo0(Canvas.GetLeft(card));
        _sDragOrigTop = NanTo0(Canvas.GetTop(card));
        card.Opacity = 0.7;
        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    private void OnSettingsCardMoved(object? sender, PointerEventArgs e)
    {
        if (_sDragCard is null) return;
        var p = e.GetPosition(SettingsCanvas);
        double nx = _sDragOrigLeft + (p.X - _sDragStart.X);
        double ny = _sDragOrigTop + (p.Y - _sDragStart.Y);
        (nx, ny) = ClampSettings(_sDragCard, nx, ny);
        Canvas.SetLeft(_sDragCard, nx);
        Canvas.SetTop(_sDragCard, ny);
    }

    private void OnSettingsCardReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_sDragCard is null) return;
        _sDragCard.Opacity = 1;
        _sDragCard = null;
        e.Pointer.Capture(null);
        UpdateSettingsCanvasHeight();
        PersistSettingsLayout();
    }

    private void OnSettingsGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_settingsLocked || sender is not Border grip || grip.Tag is not Border card) return;
        _sResizeCard = card;
        _sResizeStart = e.GetPosition(this);
        _sResizeW = card.Bounds.Width;
        _sResizeH = card.Bounds.Height;
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnSettingsGripMoved(object? sender, PointerEventArgs e)
    {
        if (_sResizeCard is null) return;
        var p = e.GetPosition(this);
        double left = NanTo0(Canvas.GetLeft(_sResizeCard));
        double maxW = Math.Max(260, SettingsAreaWidth() - left);
        _sResizeCard.Width = Math.Clamp(_sResizeW + (p.X - _sResizeStart.X), 260, maxW);
        _sResizeCard.Height = Math.Max(90, _sResizeH + (p.Y - _sResizeStart.Y));
    }

    private void OnSettingsGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_sResizeCard is null) return;
        _sResizeCard = null;
        e.Pointer.Capture(null);
        UpdateSettingsCanvasHeight();
        PersistSettingsLayout();
    }

    private void OnSettingsLockToggle(object? sender, RoutedEventArgs e)
    {
        SetSettingsLocked(SettingsLockToggle.IsChecked == true);
        _settings.SettingsLocked = _settingsLocked;
        try { _settings.Save(); } catch (Exception ex) { Log("Settings save failed: " + ex.Message); }
        LogEvent("INFO", _settingsLocked ? "Settings cards locked" : "Settings cards unlocked", null);
    }

    private void SetSettingsLocked(bool locked)
    {
        _settingsLocked = locked;
        if (SettingsLockToggle.IsChecked != locked) SettingsLockToggle.IsChecked = locked;
        foreach (var grip in _settingsGrips) grip.IsVisible = !locked;
        // The move cursor lives on the header handle, and only while cards are unlocked.
        var cursor = new Cursor(locked ? StandardCursorType.Arrow : StandardCursorType.SizeAll);
        foreach (var bar in _settingsDragBars) bar.Cursor = cursor;
    }

    // The card area's usable width = the scroll viewport (minus a little for the scrollbar).
    private double SettingsAreaWidth()
    {
        double w = SettingsScroll.Bounds.Width;
        if (w < 50) w = Math.Max(700, Bounds.Width - 212 - 64);
        return w - 6;
    }

    // Keep a card's left/right edges inside the area; the bottom is free (the canvas scrolls).
    private (double X, double Y) ClampSettings(Border card, double x, double y)
    {
        double cw = !double.IsNaN(card.Width) ? card.Width : (card.Bounds.Width > 1 ? card.Bounds.Width : SettingsCardW);
        double maxX = Math.Max(0, SettingsAreaWidth() - cw);
        return (Math.Clamp(x, 0, maxX), Math.Max(0, y));
    }

    private static double SettingsCardH(Border card)
    {
        if (!double.IsNaN(card.Height)) return card.Height;
        if (card.Bounds.Height > 1) return card.Bounds.Height;
        return SIdOf(card) switch
        {
            "connection" => 96, "rgbengine" => 96, "dashlayout" => 140,
            "startup" => 184, "render" => 196, "security" => 250,
            "identity" => 320, "activity" => 340, _ => 160,
        };
    }

    // Grow the canvas so the scroll range reaches the lowest card.
    private void UpdateSettingsCanvasHeight()
    {
        double maxBottom = 0;
        foreach (var card in _settingsCards)
            maxBottom = Math.Max(maxBottom, NanTo0(Canvas.GetTop(card)) + SettingsCardH(card));
        SettingsCanvas.Height = maxBottom + 14;
    }

    // Masonry auto-flow: drop each card into the currently shortest column.
    private void FlowSettings()
    {
        double areaW = SettingsAreaWidth();
        const double gap = 14;
        int cols = Math.Max(1, (int)Math.Floor((areaW + gap) / (SettingsCardW + gap)));
        var colY = new double[cols];
        foreach (var card in _settingsCards)
        {
            if (!card.IsVisible) continue;
            int c = 0;
            for (int i = 1; i < cols; i++) if (colY[i] < colY[c]) c = i;
            Canvas.SetLeft(card, c * (SettingsCardW + gap));
            Canvas.SetTop(card, colY[c]);
            colY[c] += SettingsCardH(card) + gap;
        }
        UpdateSettingsCanvasHeight();
    }

    // First time the page is shown the canvas finally has a size. Posted at Loaded priority so the
    // cards have measured before we lay them out: flow if nothing is saved, else clamp saved spots
    // into the current width.
    private void OnSettingsShown()
        => Dispatcher.UIThread.Post(() =>
        {
            if (!_settingsSeeded) { FlowSettings(); _settingsSeeded = true; PersistSettingsLayout(); }
            else
            {
                foreach (var card in _settingsCards)
                {
                    var (x, y) = ClampSettings(card, NanTo0(Canvas.GetLeft(card)), NanTo0(Canvas.GetTop(card)));
                    Canvas.SetLeft(card, x); Canvas.SetTop(card, y);
                }
                UpdateSettingsCanvasHeight();
            }
        }, DispatcherPriority.Loaded);

    private void OnResetSettingsLayout(object? sender, RoutedEventArgs e)
    {
        _settings.SettingsFree = null;
        _settings.SettingsSizes = null;
        foreach (var card in _settingsCards)
        {
            card.ClearValue(HeightProperty);
            card.Width = SettingsCardW;
            // Cards whose content fills a "*" row need an explicit height or the row collapses;
            // ClearValue wipes the XAML default, so restore it for those.
            if (SIdOf(card) == "activity") card.Height = SettingsCardH(card);
        }
        _settingsSeeded = false;
        OnSettingsShown();   // re-flow once the cleared sizes have re-measured
    }

    private void PersistSettingsLayout()
    {
        _settings.SettingsFree = _settingsCards.ToDictionary(SIdOf,
            c => new[] { NanTo0(Canvas.GetLeft(c)), NanTo0(Canvas.GetTop(c)) });
        _settings.SettingsSizes = _settingsCards.ToDictionary(SIdOf,
            c => new[] { double.IsNaN(c.Width) ? -1 : c.Width, double.IsNaN(c.Height) ? -1 : c.Height });
        try { _settings.Save(); } catch (Exception ex) { Log("Settings layout save failed: " + ex.Message); }
    }

    // ===================================================================
    //  Persisted settings
    // ===================================================================
    private void ApplyGlobalSettings()
    {
        PollInterval.Value = _settings.PollIntervalMs;
        FpsBox.Value = _settings.Fps;
        RefreshMsBox.Value = _settings.SensorRefreshMs;
        MinimizeTargetCombo.SelectedIndex = _settings.MinimizeToTray ? 1 : 0;
        AutoStartGuiToggle.IsChecked = _settings.AutoStartGui;
        AutoStartServicesToggle.IsChecked = _settings.AutoStartServices;
        SettingsLockToggle.IsChecked = _settings.SettingsLocked;
    }

    // ===================================================================
    //  Auto-start (console on login + broker services on login)
    // ===================================================================

    // HKCU Run entry for launching this console at sign-in (no admin needed).
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "RegisterBrokerConsole";

    private void OnAutoStartGuiToggle(object? sender, RoutedEventArgs e)
    {
        bool on = AutoStartGuiToggle.IsChecked == true;
        _settings.AutoStartGui = on;
        try { _settings.Save(); } catch (Exception ex) { Log("Settings save failed: " + ex.Message); }
        try
        {
            ApplyGuiAutoStart(on);
            LogEvent("INFO", on ? "Console will start on login" : "Console login start disabled", null);
        }
        catch (Exception ex) { LogEvent("WARN", "Could not update login startup: " + ex.Message, null); }
    }

    private static void ApplyGuiAutoStart(bool on)
    {
        if (!OperatingSystem.IsWindows()) return;   // the HKCU Run key is a Windows-only mechanism
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null) return;
        if (on)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)) key.SetValue(RunValueName, "\"" + exe + "\"");
        }
        else key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private void OnAutoStartServicesToggle(object? sender, RoutedEventArgs e)
    {
        bool on = AutoStartServicesToggle.IsChecked == true;
        _settings.AutoStartServices = on;
        try { _settings.Save(); } catch (Exception ex) { Log("Settings save failed: " + ex.Message); }
        LogEvent("INFO", on
            ? "Broker services set to auto-start (applied now if elevated, otherwise on next elevated start)"
            : "Broker services auto-start preference cleared", null);
        TryConfigureServiceStart(on);
    }

    // Best-effort: changing a Windows service's start type needs administrator rights this non-admin
    // console doesn't have. The preference is already persisted; if we happen to be elevated this
    // applies it, otherwise sc.exe fails quietly (output redirected) and the saved flag stands for an
    // elevated install/run to honour later.
    private static void TryConfigureServiceStart(bool auto)
    {
        string start = auto ? "auto" : "demand";
        foreach (var svc in new[] { "SensorBroker", "BrokerControl", "BrokerSmbus" })
        {
            try
            {
                var psi = new ProcessStartInfo("sc", $"config {svc} start= {start}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                Process.Start(psi);
            }
            catch { /* sc unavailable / not elevated -> preference saved for later */ }
        }
    }

    private void SaveSettings()
    {
        _settings.Fps = (int)(FpsBox.Value ?? 20);
        _settings.SensorRefreshMs = (int)(RefreshMsBox.Value ?? 750);
        _settings.PollIntervalMs = (int)(PollInterval.Value ?? 1000);
        _settings.MinimizeToTray = MinimizeTargetCombo.SelectedIndex == 1;

        CaptureNormalBounds();
        if (_normalBounds is { } nb)
            _settings.WindowBounds = new[] { (double)nb.Pos.X, (double)nb.Pos.Y, nb.W, nb.H };
        _settings.WindowMaximized = WindowState == WindowState.Maximized;

        CaptureDeviceSettings();

        try { _settings.Save(); }
        catch (Exception ex) { Log("Settings save failed: " + ex.Message); }
    }

    // Snapshot every connected device's current effect + tunables into the settings (no disk write).
    private void CaptureDeviceSettings()
    {
        if (_engine is null) return;
        foreach (var row in _rgbRows)
        {
            var eff = _engine.GetEffect(row.Id);
            if (eff is null) { _settings.Devices.Remove(row.Id); continue; }
            _settings.Devices[row.Id] = DeviceSettings.Capture(eff, _engine.IsEnabled(row.Id), row.LedCount);
        }
    }

    // Capture + write the per-device effect/parameter state immediately. Used by the debounced
    // ScheduleDeviceSave so per-animation edits persist without waiting for the window to close.
    private void PersistDeviceSettings()
    {
        if (_engine is null) return;
        CaptureDeviceSettings();
        try { _settings.Save(); }
        catch (Exception ex) { Log("Effect save failed: " + ex.Message); }
    }

    // Coalesce a burst of parameter edits into a single delayed save (see _deviceSaveTimer).
    private void ScheduleDeviceSave()
    {
        if (_engine is null || _deviceSaveTimer is null) return;
        _deviceSaveTimer.Stop();
        _deviceSaveTimer.Start();
    }

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

            _effectCache[EffectKey(row.Id, eff.Name)] = eff;
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
    //  Minimize target (taskbar vs. tray)
    // ===================================================================
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
        catch { _trayIcon = null; }
    }

    private void OnMinimizeTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        _settings.MinimizeToTray = MinimizeTargetCombo.SelectedIndex == 1;
        if (!_settings.MinimizeToTray && _trayIcon is { IsVisible: true }) RestoreFromTray();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized)
            HideToTrayIfRequested();
        if (e.Property == BoundsProperty || e.Property == WindowStateProperty)
            CaptureNormalBounds();
    }

    // ===================================================================
    //  Window geometry persistence (size / position / maximized)
    // ===================================================================

    // Track the window's placement only while Normal — that's the size/spot to return to. (When
    // maximized, Bounds is the full-screen size, which we don't want to save as the restore target.)
    private void CaptureNormalBounds()
    {
        if (WindowState != WindowState.Normal) return;
        double w = Bounds.Width, h = Bounds.Height;
        if (w > 1 && h > 1) _normalBounds = (Position, w, h);
    }

    // Reopen at the last session's size/position/maximized state. Also subsumes the old
    // "borderless windows can start spuriously maximized on Win32" guard: with nothing saved we
    // explicitly pin to Normal.
    private void RestoreWindowGeometry()
    {
        if (_settings.WindowBounds is { Length: 4 } b)
        {
            Width = Math.Max(MinWidth, b[2]);
            Height = Math.Max(MinHeight, b[3]);
            var pos = new PixelPoint((int)b[0], (int)b[1]);
            // Only honour the saved position if it still falls on a connected monitor — otherwise a
            // changed display setup (undocked, monitor removed) would reopen the window off-screen.
            if (IsOnAScreen(pos))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = pos;
            }
            _normalBounds = (Position, Width, Height);
        }
        WindowState = _settings.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
    }

    // True if the given top-left lands inside any connected monitor's bounds (with a small margin so
    // a window pushed slightly off the top/left still counts). Falls back to true if screens are
    // unavailable, so the saved position is honoured rather than discarded.
    private bool IsOnAScreen(PixelPoint p)
    {
        var all = Screens?.All;
        if (all is null || all.Count == 0) return true;
        foreach (var s in all)
        {
            var r = s.Bounds;
            if (p.X >= r.X - 64 && p.X <= r.X + r.Width - 64 &&
                p.Y >= r.Y - 8 && p.Y <= r.Y + r.Height - 8)
                return true;
        }
        return false;
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
    private async Task TryAutoConnect()
    {
        try { await Connect(); }
        catch { /* broker not running yet -> stay disconnected, user can retry */ }
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_sensors is not null || _control is not null)
        {
            SaveSettings(); Cleanup(); SetStatus(false);
            return;
        }
        try { await Connect(); }
        catch (BrokerException bx)
        {
            LogEvent("ERROR", "Connect failed: " + bx.Message, null);
            SetStatus(false);
            Cleanup();
        }
    }

    private async Task Connect()
    {
        // Never stack a second connection on top of a live one — that orphans the running engine and
        // pipes (the tray-restore double-connect bug). Disconnect first if you want to reconnect.
        if (_sensors is not null || _control is not null || _engine is not null) return;

        ConnectButton.IsEnabled = false;
        try
        {
            var sensors = BrokerClient.ForSensors();
            await sensors.ConnectAsync(new[] { "sensors:read" });
            _sensors = sensors;
            LogEvent("SUCCESS", "Connected to SensorBroker", "sensors:read");

            try
            {
                var control = BrokerClient.ForControl();
                await control.ConnectAsync(new[] { "rgb:write" });
                _control = control;
                LogEvent("SUCCESS", "Connected to BrokerControl", "rgb:write");
            }
            catch (BrokerException bx) { LogEvent("WARN", "RGB control unavailable: " + bx.Message, null); }

            SetStatus(true);
            ConnectButton.Content = "Disconnect";

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
                _engine.PushFailed += (id, msg) => LogEvent("ERROR", $"[{id}] {msg}", null);
                _engine.ConnectionLost += OnEngineConnectionLost;
                _engine.Start();
                EngineStatus.Text = "Engine running. Pick an effect, enable Live control.";
            }

            await ReadSensorsOnce();
            RecreateSensorBoxes();
            await RefreshRgbDevices();
            RestoreDeviceSettings();

            // Start a live poll so the overview sparklines and grouped readouts stay current.
            if (_pollTimer is null) { SensorPollToggle.IsChecked = true; StartPolling(); }
        }
        finally { ConnectButton.IsEnabled = true; }
    }

    private void SetStatus(bool ok)
    {
        var green = Res("Green");
        var muted = Res("TextMuted");
        var secondary = Res("TextSecondary");

        ConnDot.Fill = ok ? green : muted;
        ConnText.Text = ok ? "Connected" : "Disconnected";
        ConnText.Foreground = ok ? green : secondary;

        BrokerText.Text = ok ? "Online" : "Offline";
        BrokerText.Foreground = ok ? green : secondary;
        SessionText.Text = ok ? HostInfo.ModeLabel : "—";
        SecSession.Text = HostInfo.SessionLabel;

        FootThroughput.Text = ok ? "Throughput: live" : "Throughput: idle";

        var grantedSensors = _sensors?.GrantedScopes.Contains("sensors:read") == true;
        var grantedRgb = _control?.GrantedScopes.Contains("rgb:write") == true;
        StyleChip(ChipSensors, grantedSensors);
        StyleChip(ChipRgb, grantedRgb);

        BuildGrantedChips();
        UpdateCounts();

        if (ok)
        {
            _connectedAt = DateTime.UtcNow;
        }
        else
        {
            ConnectButton.Content = "Connect";
            ResetLiveReadouts();
        }
    }

    private void StyleChip(Border chip, bool granted)
    {
        chip.Opacity = granted ? 1.0 : 0.4;
        chip.BorderBrush = granted ? Res("Green") : Res("Border0");
        if (chip.Child is TextBlock tb) tb.Foreground = granted ? Res("Green") : Res("TextSecondary");
    }

    private void BuildGrantedChips()
    {
        GrantedChips.Children.Clear();
        GrantedChipsPage.Children.Clear();
        var scopes = (_sensors?.GrantedScopes ?? Array.Empty<string>())
            .Concat(_control?.GrantedScopes ?? Array.Empty<string>())
            .Distinct().ToList();
        foreach (var s in scopes)
        {
            GrantedChips.Children.Add(MakeScopeChip(s, 11, 2));
            GrantedChipsPage.Children.Add(MakeScopeChip(s, 12, 4));
        }
        if (scopes.Count == 0)
        {
            GrantedChips.Children.Add(new TextBlock { Text = "none — not connected", Foreground = Res("TextMuted"), FontSize = 11 });
        }
    }

    private Border MakeScopeChip(string scope, double fontSize, double padV)
    {
        return new Border
        {
            Background = Res("GreenSoft"),
            CornerRadius = new Avalonia.CornerRadius(5),
            Padding = new Avalonia.Thickness(8, padV),
            Margin = new Avalonia.Thickness(0, 0, 6, 6),
            Child = new TextBlock
            {
                Text = "✓ " + scope,
                FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
                FontSize = fontSize,
                Foreground = Res("Green"),
            },
        };
    }

    // ===================================================================
    //  Sensors
    // ===================================================================
    private async void OnSensorReadOnce(object? sender, RoutedEventArgs e) => await ReadSensorsOnce();

    private async Task ReadSensorsOnce()
    {
        if (_sensors is null) { Log("Not connected."); return; }
        try
        {
            var t0 = DateTime.UtcNow;
            var list = await _sensors.SensorReadAllAsync();
            _lastLatencyMs = (DateTime.UtcNow - t0).TotalMilliseconds;
            // sensor.readall returns only sensors that are available AND read OK this cycle, so its
            // count is exactly "available and usable" — the number surfaced everywhere in the UI.
            _usableSensorCount = list.Count;
            MergeSensors(list);
            UpdateOverview(list);
            UpdateSensorBoxes(list);
            UpdateLatency();
            UpdateCounts();
            SensorCount.Text = $"{_usableSensorCount} sensors · {_lastLatencyMs:F0} ms";
        }
        catch (Exception ex) { LogEvent("ERROR", "sensor.readall failed: " + ex.Message, null); StopPolling(); }
    }

    private void MergeSensors(IReadOnlyList<SensorInfo> list)
    {
        var byId = _sensorRows.ToDictionary(r => r.Id);
        var present = new HashSet<string>(list.Count);
        foreach (var s in list)
        {
            present.Add(s.Id);
            _sensorMiss.Remove(s.Id);
            if (byId.TryGetValue(s.Id, out var row)) { row.Update(s); continue; }

            var newRow = new SensorRow(s) { ShowOrigin = _showOrigins };
            _sensorRows.Add(newRow);
            GroupFor(GroupOf(s)).Rows.Add(newRow);
        }

        /* Prune rows whose sensor has dropped out of the live catalog — a removable controller
           (e.g. the Quadro) unplugged, or a sensor gone unavailable — so the list and the count
           stay "available and usable". Debounced by 2 cycles so one transient read miss never
           flickers a fixed sensor out. (Pinned dashboard cards keep showing "—" per the removable
           contract; only the auto-built Sensors list is pruned.) */
        foreach (var row in _sensorRows.ToList())
        {
            if (present.Contains(row.Id)) continue;
            int miss = (_sensorMiss.TryGetValue(row.Id, out int m) ? m : 0) + 1;
            _sensorMiss[row.Id] = miss;
            if (miss >= 2) RemoveSensorRow(row);
        }
    }

    private void RemoveSensorRow(SensorRow row)
    {
        _sensorRows.Remove(row);
        _sensorMiss.Remove(row.Id);
        foreach (var g in _sensorGroups.ToList())
        {
            if (!g.Rows.Remove(row)) continue;
            if (g.Rows.Count == 0) { _sensorGroups.Remove(g); _groupByName.Remove(g.Name); }
            break;
        }
    }

    private SensorGroup GroupFor(string name)
    {
        if (_groupByName.TryGetValue(name, out var g)) return g;
        g = new SensorGroup(name);
        _groupByName[name] = g;
        // Insert keeping the fixed GroupOrder.
        int targetRank = Array.IndexOf(GroupOrder, name);
        if (targetRank < 0) targetRank = GroupOrder.Length;
        int insertAt = _sensorGroups.Count;
        for (int i = 0; i < _sensorGroups.Count; i++)
        {
            int rank = Array.IndexOf(GroupOrder, _sensorGroups[i].Name);
            if (rank < 0) rank = GroupOrder.Length;
            if (rank > targetRank) { insertAt = i; break; }
        }
        _sensorGroups.Insert(insertAt, g);
        return g;
    }

    private static string GroupOf(SensorInfo s) => GroupOf(s.Id, s.Label, s.Unit);

    private static string GroupOf(string idRaw, string? labelRaw, string? unitRaw)
    {
        string id = idRaw.ToLowerInvariant();
        string label = (labelRaw ?? "").ToLowerInvariant();
        string unit = (unitRaw ?? "").ToLowerInvariant();

        bool Has(string k) => id.Contains(k) || label.Contains(k);

        if (unit.Contains("rpm") || Has("fan") || Has("pwm")) return "Fans";
        if (unit == "v" || Has("volt") || Has("vcore") || Has("vsoc")) return "Voltages";
        if (Has("cpu") || Has("ccd") || Has("soc") || Has("smu") || Has("tctl") || Has("tdie")) return "CPU";
        if (Has("vrm") || Has("mos") || Has("chipset") || Has("system") || Has("pcie") || Has("board")
            || Has("dimm") || Has("nct") || Has("mb")) return "Motherboard";
        return "Other";
    }

    private static int GroupRank(string name)
    {
        int r = Array.IndexOf(GroupOrder, name);
        return r < 0 ? GroupOrder.Length : r;
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
        PollText.Text = $"{ms} ms";
        LogEvent("INFO", $"Live poll started ({ms} ms)", null);
    }

    private void StopPolling()
    {
        if (_pollTimer is null) return;
        _pollTimer.Stop();
        _pollTimer = null;
        SensorPollToggle.IsChecked = false;
        PollText.Text = "— ms";
        Log("Live poll stopped.");
    }

    private void SetShowOrigins(bool on)
    {
        _showOrigins = on;
        OriginsTogglePage.IsChecked = on;
        foreach (var r in _sensorRows) r.ShowOrigin = on;
    }

    private void OnToggleOrigins(object? sender, RoutedEventArgs e)
    {
        bool on = sender is ToggleSwitch ts ? ts.IsChecked == true : !_showOrigins;
        SetShowOrigins(on);
    }

    // ===================================================================
    //  Overview cards + status readouts
    // ===================================================================
    private void UpdateOverview(IReadOnlyList<SensorInfo> list)
    {
        var cpu = Find(list, s => Has(s, "cpu") && IsTemp(s));
        var gpu = Find(list, s => Has(s, "gpu") && IsTemp(s));
        var vrm = Find(list, s => (Has(s, "vrm") || Has(s, "mos")) && IsTemp(s));
        var sys = Find(list, s => Has(s, "system") && IsTemp(s));
        SetCard(CpuVal, CpuSpark, cpu);
        SetCard(GpuVal, GpuSpark, gpu);
        SetCard(VrmVal, VrmSpark, vrm);
        SetCard(SysVal, SysSpark, sys);
    }

    // Colour the GPU card by which vendor the broker serves GPU sensors from:
    // AMD = red, NVIDIA = green, Intel = blue. No GPU runtime present -> keep the default styling.
    private void ApplyGpuVendorColor()
    {
        var kind = GpuVendor.Kind;
        if (kind == GpuVendorKind.None) return;
        var c = GpuVendor.AccentColor(kind);
        var accent = new SolidColorBrush(c);
        GpuBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B));
        GpuBadgeText.Foreground = accent;   // vendor colour on the badge + sparkline only;
        GpuSpark.Stroke = accent;           // the temperature value stays white (TextPrimary).
    }

    private void UpdateSensorBoxes(IReadOnlyList<SensorInfo> list)
    {
        var values = new Dictionary<string, double?>(list.Count);
        foreach (var info in list) values[info.Id] = info.Value;
        // Update every pinned card across all sections; a card whose sensor is no longer
        // available/usable (e.g. an unplugged removable Quadro) gets a null value -> SetCard shows
        // "—" (not connected) rather than a frozen stale reading. The same sensor pinned in several
        // sections updates every copy.
        foreach (var sec in _sections)
            foreach (var (id, view) in sec.SensorViews)
                SetCard(view.Val, view.Spark, values.TryGetValue(id, out var v) ? v : null);
    }

    private static bool Has(SensorInfo s, string k)
        => s.Id.ToLowerInvariant().Contains(k) || (s.Label ?? "").ToLowerInvariant().Contains(k);

    private static bool IsTemp(SensorInfo s)
        => (s.Unit ?? "").Contains('C') || (s.Label ?? "").ToLowerInvariant().Contains("temp");

    private static double? Find(IReadOnlyList<SensorInfo> list, Func<SensorInfo, bool> pred)
        => list.FirstOrDefault(s => s.Value is not null && pred(s))?.Value;

    private static void SetCard(TextBlock val, Sparkline spark, double? v)
    {
        if (v is { } d)
        {
            val.Text = d.ToString("F1", CultureInfo.InvariantCulture);
            spark.Push(d);
        }
        else val.Text = "—";
    }

    private void UpdateLatency()
    {
        LatencyText.Text = $"{_lastLatencyMs:F0} ms";
        LatVal.Text = _lastLatencyMs.ToString("F0", CultureInfo.InvariantCulture);
        LatSpark.Push(_lastLatencyMs);
    }

    private void UpdateCounts()
    {
        DevicesText.Text = _rgbRows.Count.ToString();
        // "available and usable" — the last readall's count, not the cumulative row list (which can
        // include sensors that have since dropped out, e.g. an unplugged removable controller).
        SensorsCountText.Text = _usableSensorCount.ToString();
    }

    private void UpdateUptime()
    {
        if (_sensors is null && _control is null) { FootUptime.Text = "Uptime: 00:00:00"; return; }
        var up = DateTime.UtcNow - _connectedAt;
        FootUptime.Text = $"Uptime: {up:hh\\:mm\\:ss}";
    }

    private void ResetLiveReadouts()
    {
        LatencyText.Text = "— ms";
        PollText.Text = "— ms";
        FootUptime.Text = "Uptime: 00:00:00";
        CpuVal.Text = GpuVal.Text = VrmVal.Text = SysVal.Text = LatVal.Text = "—";
        CpuSpark.Reset(); GpuSpark.Reset(); VrmSpark.Reset(); SysSpark.Reset(); LatSpark.Reset();
    }

    // ===================================================================
    //  RGB
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
            UpdateCounts();
            LogEvent("INFO", $"rgb.list → {devices.Count} device(s)", "rgb:write");
        }
        catch (Exception ex) { LogEvent("ERROR", "rgb.list failed: " + ex.Message, null); }
    }

    private RgbRow? SelectedRow => RgbDeviceList.SelectedItem as RgbRow;

    private void OnRgbDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null || _engine is null) { EditorPanel.IsVisible = false; return; }

        _selectedDeviceId = row.Id;
        RgbSelected.Text = row.Label;
        RgbSelectedSub.Text = row.CardDetail;
        EditorPanel.IsVisible = true;

        _ledCells.Clear();
        for (int i = 0; i < row.LedCount; i++) _ledCells.Add(new LedCell(i));

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
        IEffect? eff = GetOrCreateEffect(row.Id, name);

        _engine.AssignEffect(row.Id, row.LedCount, eff);
        row.SetEffect(name);
        ManualTools.IsVisible = eff is ManualEffect;
        BuildParamPanel(eff);

        if (eff is TemperatureEffect) EnsureSensorPoll();
        ScheduleDeviceSave();
    }

    private void OnDriveAll(object? sender, RoutedEventArgs e)
    {
        if (_engine is null || SelectedRow is not { } row) return;
        var src = _engine.GetEffect(row.Id);
        if (src is null) { Log("Pick an effect first."); return; }

        foreach (var r in _rgbRows)
        {
            if (r.Id != row.Id)
            {
                var clone = CloneEffect(src);
                if (clone is not null) _effectCache[EffectKey(r.Id, clone.Name)] = clone;
                _engine.AssignEffect(r.Id, r.LedCount, clone);
            }
            _engine.SetEnabled(r.Id, true);
            r.SetEffect(src.Name);
            r.SetEnabled(true);
        }
        DriveCheck.IsChecked = true;
        if (src is TemperatureEffect) EnsureSensorPoll();
        LogEvent("APPLY", $"{src.Name} applied to {_rgbRows.Count} device(s)", "RGB");
        ScheduleDeviceSave();
    }

    private void OnStopAll(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        foreach (var r in _rgbRows) { _engine.SetEnabled(r.Id, false); r.SetEnabled(false); }
        DriveCheck.IsChecked = false;
        LogEvent("INFO", "Stopped driving all devices", null);
        ScheduleDeviceSave();
    }

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
        LogEvent(on ? "APPLY" : "INFO", $"[{row.Id}] live control {(on ? "ON" : "off")}", on ? "RGB" : null);
        ScheduleDeviceSave();
    }

    private async void OnApplyOnce(object? sender, RoutedEventArgs e)
    {
        if (_engine is null || SelectedRow is not { } row) return;
        if (_engine.GetEffect(row.Id) is null) { Log("Pick an effect first."); return; }
        bool ok = await _engine.RenderOnceAsync(row.Id);
        LogEvent(ok ? "APPLY" : "ERROR", $"[{row.Id}] apply once → {(ok ? "OK" : "failed")}", ok ? "RGB" : null);
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
        if (!_engine.IsEnabled(row.Id)) await _engine.RenderOnceAsync(row.Id);
        ScheduleDeviceSave();
    }

    private async void OnManualFill(object? sender, RoutedEventArgs e)
    {
        if (_engine is null || SelectedRow is not { } row) return;
        if (_engine.GetEffect(row.Id) is not ManualEffect m) return;
        m.Fill(m.Brush);
        var b = ToBrush(m.Brush);
        foreach (var cell in _ledCells) cell.Brush = b;
        await _engine.RenderOnceAsync(row.Id);
        ScheduleDeviceSave();
    }

    private async void OnManualClear(object? sender, RoutedEventArgs e)
    {
        if (_engine is null || SelectedRow is not { } row) return;
        if (_engine.GetEffect(row.Id) is not ManualEffect m) return;
        m.Fill(RgbColor.Black);
        var b = ToBrush(RgbColor.Black);
        foreach (var cell in _ledCells) cell.Brush = b;
        await _engine.RenderOnceAsync(row.Id);
        ScheduleDeviceSave();
    }

    private void OnEngineConnectionLost(string reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_sensors is null && _control is null) return;
            LogEvent("ERROR", "Connection lost: " + reason + " — disconnected", null);
            Cleanup();
            SetStatus(false);
        });
    }

    private void OnFrameRendered(string deviceId, RgbColor[] colors)
    {
        if (deviceId != _selectedDeviceId) return;
        var now = DateTime.UtcNow;
        if ((now - _lastPreview).TotalMilliseconds < 66) return;
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
    //  Generic parameter panel
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

    private Control BuildGradientSection(IGradientEffect g)
    {
        var root = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        root.Children.Add(new TextBlock { Text = "Temperature colour stops", Foreground = Res("TextMuted"), FontSize = 11 });
        var host = new StackPanel { Spacing = 4 };
        PopulateStops(host, g);
        root.Children.Add(host);
        var add = new Button { Content = "Add colour stop", Classes = { "ghost" } };
        add.Click += (_, _) => { g.AddStop(); PopulateStops(host, g); ScheduleDeviceSave(); };
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
            temp.ValueChanged += (_, _) => { stop.Temp = (double)(temp.Value ?? 0m); ScheduleDeviceSave(); };

            var swatch = new Border { Width = 24, Height = 22, CornerRadius = new Avalonia.CornerRadius(3),
                BorderBrush = Res("Border1"), BorderThickness = new Avalonia.Thickness(1), Background = ToBrush(stop.Color) };
            var hex = new TextBox { Width = 88, Text = stop.Color.ToHex(), MaxLength = 6,
                FontFamily = new FontFamily("Cascadia Code,Consolas,monospace") };
            hex.PropertyChanged += (_, ev) =>
            {
                if (ev.Property == TextBox.TextProperty && RgbColor.TryParseHex(hex.Text, out var c))
                { stop.Color = c; swatch.Background = ToBrush(c); ScheduleDeviceSave(); }
            };
            var remove = new Button { Content = "✕", IsEnabled = g.Stops.Count > 1, Classes = { "ghost" } };
            remove.Click += (_, _) => { g.RemoveStop(stop); PopulateStops(host, g); ScheduleDeviceSave(); };

            row.Children.Add(new TextBlock { Text = "at", VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextSecondary") });
            row.Children.Add(temp);
            row.Children.Add(new TextBlock { Text = "°", VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextSecondary") });
            row.Children.Add(swatch);
            row.Children.Add(new TextBlock { Text = "#", VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextSecondary") });
            row.Children.Add(hex);
            var pick = new Button { Content = "Pick…", VerticalAlignment = VerticalAlignment.Center, Classes = { "ghost" } };
            pick.Click += async (_, _) => await PickColorInto(hex);
            row.Children.Add(pick);
            foreach (var preset in Palette)
            {
                RgbColor.TryParseHex(preset, out var pc);
                var swatchBtn = new Button { Width = 18, Height = 18, Background = ToBrush(pc),
                    BorderBrush = Res("Border1"), BorderThickness = new Avalonia.Thickness(1), Padding = new Avalonia.Thickness(0) };
                swatchBtn.Click += (_, _) => { hex.Text = preset; };
                row.Children.Add(swatchBtn);
            }
            row.Children.Add(remove);
            host.Children.Add(row);
        }
    }

    private Control BuildParamControl(EffectParam p)
    {
        var root = new StackPanel { Spacing = 4 };
        switch (p.Kind)
        {
            case ParamKind.Slider:
            {
                var head = new DockPanel();
                var value = new TextBlock { Text = FormatNum(p), Foreground = Res("TextSecondary") };
                DockPanel.SetDock(value, Dock.Right);
                head.Children.Add(value);
                head.Children.Add(new TextBlock { Text = p.Label, Foreground = Res("TextSecondary") });
                var slider = new Slider { Minimum = p.Min, Maximum = p.Max, Value = p.Num, SmallChange = p.Step };
                slider.PropertyChanged += (_, ev) =>
                {
                    if (ev.Property == Slider.ValueProperty) { p.Num = slider.Value; value.Text = FormatNum(p); ScheduleDeviceSave(); }
                };
                root.Children.Add(head);
                root.Children.Add(slider);
                break;
            }
            case ParamKind.Color:
            {
                root.Children.Add(new TextBlock { Text = p.Label, Foreground = Res("TextSecondary") });
                var rowg = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*") };
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var swatch = new Border { Width = 28, Height = 24, CornerRadius = new Avalonia.CornerRadius(3),
                    BorderBrush = Res("Border1"), BorderThickness = new Avalonia.Thickness(1), Background = ToBrush(p.Color_) };
                var hex = new TextBox { Width = 96, Text = p.Hex, MaxLength = 6,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,monospace") };
                hex.PropertyChanged += (_, ev) =>
                {
                    if (ev.Property == TextBox.TextProperty && RgbColor.TryParseHex(hex.Text, out var c))
                    { p.Color_ = c; swatch.Background = ToBrush(c); ScheduleDeviceSave(); }
                };
                line.Children.Add(swatch);
                line.Children.Add(new TextBlock { Text = "#", VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextSecondary") });
                line.Children.Add(hex);
                var pick = new Button { Content = "Pick…", VerticalAlignment = VerticalAlignment.Center, Classes = { "ghost" } };
                pick.Click += async (_, _) => await PickColorInto(hex);
                line.Children.Add(pick);
                foreach (var preset in Palette)
                {
                    RgbColor.TryParseHex(preset, out var pc);
                    var b = new Button { Width = 22, Height = 22, Background = ToBrush(pc),
                        BorderBrush = Res("Border1"), BorderThickness = new Avalonia.Thickness(1), Padding = new Avalonia.Thickness(0) };
                    b.Click += (_, _) => { hex.Text = preset; };
                    line.Children.Add(b);
                }
                root.Children.Add(line);
                break;
            }
            case ParamKind.Toggle:
            {
                var cb = new CheckBox { Content = p.Label, IsChecked = p.Flag, Foreground = Res("TextSecondary") };
                cb.IsCheckedChanged += (_, _) => { p.Flag = cb.IsChecked == true; ScheduleDeviceSave(); };
                root.Children.Add(cb);
                break;
            }
            case ParamKind.Choice:
            {
                root.Children.Add(new TextBlock { Text = p.Label, Foreground = Res("TextSecondary") });
                var combo = new ComboBox { Width = 220, ItemsSource = p.Choices, SelectedIndex = p.ChoiceIndex };
                combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) { p.ChoiceIndex = combo.SelectedIndex; ScheduleDeviceSave(); } };
                root.Children.Add(combo);
                break;
            }
        }
        return root;
    }

    private async Task PickColorInto(TextBox hex)
    {
        var current = RgbColor.TryParseHex(hex.Text, out var c) ? c : RgbColor.Black;
        var picked = await new ColorPickerWindow(current).ShowDialog<RgbColor?>(this);
        if (picked is { } p) hex.Text = p.ToHex();
    }

    private static string FormatNum(EffectParam p)
        => (p.Step < 1 ? p.Num.ToString("0.00", CultureInfo.InvariantCulture)
                       : p.Num.ToString("0", CultureInfo.InvariantCulture));

    private static ISolidColorBrush ToBrush(RgbColor c) => new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));

    private static string EffectKey(string deviceId, string name) => deviceId + "|" + name;

    private IEffect? GetOrCreateEffect(string deviceId, string name)
    {
        if (name == "(none)") return null;
        var key = EffectKey(deviceId, name);
        if (_effectCache.TryGetValue(key, out var cached)) return cached;
        var eff = CreateEffect(name);
        if (eff is null) return null;
        if (eff is ISensorAware sa) sa.SetSensorIds(_sensorIds);
        _effectCache[key] = eff;
        return eff;
    }

    private static IEffect? CreateEffect(string name) => name switch
    {
        "Static" => new StaticEffect(),
        "Temperature" => new TemperatureEffect(),
        "Rainbow" => new RainbowEffect(),
        "Breathing" => new BreathingEffect(),
        "Heartbeat" => new HeartbeatEffect(),
        "Comet" => new CometEffect(),
        "Scanner" => new ScannerEffect(),
        "Theater Chase" => new TheaterChaseEffect(),
        "Twinkle" => new TwinkleEffect(),
        "Aurora" => new AuroraEffect(),
        "Plasma" => new PlasmaEffect(),
        "Fire" => new FireEffect(),
        "Ripple" => new RippleEffect(),
        "Juggle" => new JuggleEffect(),
        "Manual per-LED" => new ManualEffect(),
        "Audio Spectrum" => new AudioSpectrumEffect(),
        _ => null,
    };

    // ===================================================================
    //  Diagnostics / activity feed
    // ===================================================================
    // No confirm/result modals: the ping result lands in the Activity feed (now visible right here on
    // the Settings page and in the dashboard Activity card), so the dialogs that pointed at it are gone.
    private async void OnPing(object? sender, RoutedEventArgs e)
    {
        if (_sensors is null) { Log("Not connected."); return; }
        try
        {
            var t0 = DateTime.UtcNow;
            bool alive = await _sensors.PingAsync();
            var ms = (DateTime.UtcNow - t0).TotalMilliseconds;
            _lastLatencyMs = ms; UpdateLatency();
            LogEvent(alive ? "SUCCESS" : "ERROR", alive ? $"ping → pong ({ms:F1} ms)" : "ping → no pong", null);
        }
        catch (Exception ex) { LogEvent("ERROR", "ping failed: " + ex.Message, null); }
    }

    private void OnClearActivity(object? sender, RoutedEventArgs e)
    {
        _activity.Clear();
        LogBox.Text = "";
        _logLines = 0;
    }

    // Swap the Activity card between the structured feed and the raw diagnostics text in place.
    private void OnRawLogToggle(object? sender, RoutedEventArgs e)
    {
        bool raw = RawLogToggle.IsChecked == true;
        RawLogScroll.IsVisible = raw;
        ActivityFeedScroll.IsVisible = !raw;
    }

    private int _logLines;

    private void Log(string msg) => LogEvent("INFO", msg, null);

    private void LogEvent(string level, string msg, string? tag)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _activity.Insert(0, new ActivityEntry(DateTime.Now, level, msg, tag));
            while (_activity.Count > 200) _activity.RemoveAt(_activity.Count - 1);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] {level}  {msg}");
        });
    }

    private void AppendLog(string line)
    {
        const int MaxLines = 500;
        LogBox.Text = string.IsNullOrEmpty(LogBox.Text) ? line : LogBox.Text + "\n" + line;
        if (++_logLines > MaxLines + 100)
        {
            var kept = LogBox.Text.Split('\n');
            LogBox.Text = string.Join("\n", kept[^MaxLines..]);
            _logLines = MaxLines;
        }
    }

    // ===================================================================
    //  Cleanup
    // ===================================================================
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
        _effectCache.Clear();
        _selectedDeviceId = null;
        EditorPanel.IsVisible = false;
        EngineStatus.Text = "Connect to drive devices.";
        BuildGrantedChips();
    }

    private static async Task DisposeClient(BrokerClient? c)
    {
        if (c is not null) await c.DisposeAsync();
    }

    // Pull a brush resource defined in App.axaml. The palette lives in theme dictionaries,
    // so the lookup must pass the active ThemeVariant (the parameterless overload misses them)
    // and fall back to the Application resources.
    private IBrush Res(string key)
    {
        var theme = ActualThemeVariant;
        if (this.TryFindResource(key, theme, out var v) && v is IBrush b) return b;
        if (Application.Current is { } app && app.TryFindResource(key, theme, out var v2) && v2 is IBrush b2) return b2;
        return Brushes.Gray;
    }

    // Bind a code-created control's brush property to a themed resource so it tracks Dark↔Light
    // switches. Unlike Res(), which snapshots the brush for the current theme only, this re-resolves
    // whenever the active theme variant changes — the fix for dynamically built cards reading as
    // white text in light mode.
    private void BindThemeBrush(Control c, AvaloniaProperty prop, string key)
        => c.Bind(prop, this.GetResourceObservable(key));

    // Re-apply everything we colour from code after a theme flip (see the ctor subscription).
    // Posted to the dispatcher so it runs AFTER Avalonia finishes re-resolving the XAML DynamicResource
    // brushes for the new theme — otherwise those bindings would re-fire and clobber our code-set
    // footer colours back to the themed default (the "footer colours vanish on toggle" bug).
    private void OnThemeChanged()
        => Dispatcher.UIThread.Post(() =>
        {
            RecolorFooter();
            BuildGrantedChips();
            if (_selectedDeviceId is { } id && _engine?.GetEffect(id) is { } eff) BuildParamPanel(eff);
        });

    // The footer's status colours are set imperatively (green when connected, themed grey otherwise),
    // so a theme change leaves them stale or resets them to the themed default. Recompute from the
    // current connection state using the active theme's brushes.
    private void RecolorFooter()
    {
        bool ok = _sensors is not null || _control is not null;
        var green = Res("Green");
        ConnDot.Fill = ok ? green : Res("TextMuted");
        ConnText.Foreground = ok ? green : Res("TextSecondary");
        BrokerText.Foreground = ok ? green : Res("TextSecondary");
        StyleChip(ChipSensors, _sensors?.GrantedScopes.Contains("sensors:read") == true);
        StyleChip(ChipRgb, _control?.GrantedScopes.Contains("rgb:write") == true);
    }
}

/// <summary>A named group of sensor rows (CPU / Motherboard / Voltages / Fans / Other).</summary>
public sealed class SensorGroup
{
    public string Name { get; }
    public ObservableCollection<SensorRow> Rows { get; } = new();
    public SensorGroup(string name) => Name = name;
}

/// <summary>One structured activity-feed entry with a coloured level badge.</summary>
public sealed class ActivityEntry
{
    public string Time { get; }
    public string Level { get; }
    public string Message { get; }
    public string? Tag { get; }
    public bool HasTag => !string.IsNullOrEmpty(Tag);
    public IBrush LevelBrush { get; }
    public IBrush LevelBg { get; }

    public ActivityEntry(DateTime when, string level, string message, string? tag)
    {
        Time = when.ToString("HH:mm:ss");
        Level = level;
        Message = message;
        Tag = tag;
        (LevelBrush, LevelBg) = level switch
        {
            "SUCCESS" => (B("#34D399"), B("#16321F")),
            "APPLY"   => (B("#B69CFF"), B("#2A2350")),
            "ERROR"   => (B("#F05252"), B("#33161A")),
            "WARN"    => (B("#F0883E"), B("#3A2A12")),
            _         => (B("#9CA4C4"), B("#171A2C")),   // INFO
        };
    }

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
}

/// <summary>Bindable, mutable sensor row so live polling updates values in place.</summary>
public sealed class SensorRow : System.ComponentModel.INotifyPropertyChanged
{
    // The value text Foreground defaults to the themed TextPrimary in XAML; IsHot/IsActive
    // toggle CSS-style classes that recolour it (orange when hot, green for spinning fans),
    // which keeps it legible in both light and dark themes.
    public string Id { get; }
    public string Label { get; private set; }
    public string Unit { get; private set; }

    /// <summary>The broker-supplied name (label, or the id when unlabelled) — the rename fallback.</summary>
    public string BrokerName => string.IsNullOrWhiteSpace(Label) ? Id : Label;

    /// <summary>What the UI shows: the local override if one is set, else the broker name.</summary>
    public string DisplayName => LocalNames.Resolve(Id, BrokerName);

    private string _valueText = "—";
    public string ValueText { get => _valueText; private set { _valueText = value; Raise(nameof(ValueText)); } }

    private bool _isHot;
    public bool IsHot { get => _isHot; private set { _isHot = value; Raise(nameof(IsHot)); } }
    private bool _isActive;
    public bool IsActive { get => _isActive; private set { _isActive = value; Raise(nameof(IsActive)); } }

    private bool _showOrigin;
    public bool ShowOrigin { get => _showOrigin; set { _showOrigin = value; Raise(nameof(ShowOrigin)); } }

    public SensorRow(SensorInfo s) { Id = s.Id; Label = s.Label; Unit = s.Unit; Update(s); }

    public void Update(SensorInfo s)
    {
        Label = s.Label; Unit = s.Unit;
        if (s.Value is { } v)
        {
            ValueText = v.ToString("F2", CultureInfo.InvariantCulture);
            bool isTemp = (s.Unit ?? "").Contains('C');
            bool isRpm = (s.Unit ?? "").ToLowerInvariant().Contains("rpm");
            IsHot = isTemp && v >= 75;
            IsActive = isRpm && v > 0;
        }
        else { ValueText = "—"; IsHot = false; IsActive = false; }
        Raise(nameof(Label)); Raise(nameof(DisplayName)); Raise(nameof(Unit));
    }

    /// <summary>Re-evaluate the display name after a local rename/reset.</summary>
    public void RaiseName() => Raise(nameof(DisplayName));

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}

/// <summary>Bindable RGB device row (label, detail, assigned-effect summary, drive indicator).</summary>
public sealed class RgbRow : System.ComponentModel.INotifyPropertyChanged
{
    private static readonly IBrush Off = new SolidColorBrush(Color.Parse("#555B7A"));

    public string Id { get; }

    /// <summary>The broker-supplied name (label, or the id when unlabelled) — the rename fallback.</summary>
    public string BrokerName { get; }

    /// <summary>What the UI shows: the local override if one is set, else the broker name.</summary>
    public string Label => LocalNames.Resolve(Id, BrokerName);

    public string Detail { get; }
    public string CardDetail { get; }
    public int LedCount { get; }

    private string _effect = "(none)";
    private bool _enabled;

    public RgbRow(RgbDevice d)
    {
        Id = d.Id; BrokerName = string.IsNullOrEmpty(d.Label) ? d.Id : d.Label; LedCount = d.Leds;
        var bits = new List<string> { $"{d.Leds} LED" + (d.Leds == 1 ? "" : "s") };
        if (!string.IsNullOrEmpty(d.Kind)) bits.Add(d.Kind!);
        if (!string.IsNullOrEmpty(d.Transport)) bits.Add(d.Transport!);
        Detail = $"{d.Id} · " + string.Join(" · ", bits);
        CardDetail = string.Join(" · ", bits);
    }

    public string Summary => $"{Detail} · {_effect}";
    public IBrush StatusBrush => _enabled ? Brushes.LimeGreen : Off;

    public void SetEffect(string n) { _effect = n; Raise(nameof(Summary)); }
    public void SetEnabled(bool e) { _enabled = e; Raise(nameof(StatusBrush)); }

    /// <summary>Re-evaluate the display name after a local rename/reset.</summary>
    public void RaiseName() => Raise(nameof(Label));

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
