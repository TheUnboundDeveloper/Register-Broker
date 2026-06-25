using System.Collections.Generic;
using Avalonia.Controls;

namespace ReferenceConsole;

/*---------------------------------------------------------------------------*\
| DashSection                                                                |
|                                                                            |
|   One dashboard "section": an independent canvas of draggable / resizable  |
|   cards with its own layout mode, lock, selection, and persisted state.    |
|   The built-in Dashboard is one of these (Id "main"); the user can add     |
|   more via "+ Add Section". Each section's lock is scoped to itself —       |
|   toggling one never affects another section (or the Settings-page lock).  |
|                                                                            |
|   This is a passive holder; all behaviour lives in MainWindow, which       |
|   operates on the section that owns the card / canvas under the pointer.   |
\*---------------------------------------------------------------------------*/
internal sealed class DashSection
{
    public required string Id { get; init; }
    public bool IsMain { get; init; }

    // ---- UI (per section) ----
    public Control Page = null!;            // page root mounted in the content host
    public Canvas Canvas = null!;            // absolute-positioned card host
    public Button? NavButton;                // sidebar entry (null for the built-in nav button is fixed)
    public Button? GridBtn;                  // "Grid" mode toggle button
    public Button? FreeBtn;                  // "Free" mode toggle button
    public Button? AddBtn;                   // "Add Card" button (flyout anchor)
    public Button? ResetBtn;                 // "Reset layout" button (null for the built-in toolbar)
    public ToggleSwitch? LockToggle;         // per-section "Lock cards" toggle
    public TextBlock? TitleText;             // header title text (updated on rename)
    public Border? Marquee;                  // rubber-band selection visual on this canvas

    // ---- live state (per section) ----
    public readonly List<Border> Boxes = new();
    public readonly List<string> DefaultOrder = new();      // built-in card order (main only)
    public readonly HashSet<Border> Selected = new();
    public readonly Dictionary<string, (TextBlock Val, Sparkline Spark)> SensorViews = new();
    public readonly Dictionary<Border, string> BoxSensorId = new();   // sensor card -> raw sensor id
    public bool Seeded;                       // canvas positions assigned once the canvas is sized

    // ---- persisted state (the JSON-backed model) ----
    public DashSectionState State = null!;

    public string LayoutMode
    {
        get => State.LayoutMode == "free" ? "free" : "grid";
        set => State.LayoutMode = value;
    }
    public bool Locked { get => State.Locked; set => State.Locked = value; }
    public string Name
    {
        get => string.IsNullOrWhiteSpace(State.Name) ? "Dashboard" : State.Name!;
        set => State.Name = value;
    }
}
