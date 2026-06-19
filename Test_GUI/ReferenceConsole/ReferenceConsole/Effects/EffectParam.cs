using System;
using System.Collections.Generic;
using System.ComponentModel;
using Broker.Client;

namespace ReferenceConsole.Effects;

public enum ParamKind { Slider, Color, Toggle, Choice }

/*---------------------------------------------------------------------------*\
| EffectParam                                                                |
|                                                                            |
|   One tunable knob on an effect. The UI builds a control from this purely  |
|   by Kind, so every effect is fully configurable with no per-effect UI     |
|   code -- the audio "reactive factor" is just a Slider param like any      |
|   other. Values are live: an effect reads them every frame.                |
\*---------------------------------------------------------------------------*/
public sealed class EffectParam : INotifyPropertyChanged
{
    public string Key { get; }
    public string Label { get; }
    public ParamKind Kind { get; }

    // Slider
    public double Min { get; private init; }
    public double Max { get; private init; }
    public double Step { get; private init; }

    // Choice
    public IReadOnlyList<string> Choices { get; private set; } = Array.Empty<string>();

    private double _value;
    private RgbColor _color = RgbColor.Black;
    private bool _flag;
    private int _choiceIndex;

    private EffectParam(string key, string label, ParamKind kind)
    {
        Key = key; Label = label; Kind = kind;
    }

    public static EffectParam Slider(string key, string label, double min, double max, double value, double step = 1)
        => new(key, label, ParamKind.Slider) { Min = min, Max = max, Step = step, _value = value };

    public static EffectParam Color(string key, string label, string hex)
    {
        var p = new EffectParam(key, label, ParamKind.Color);
        RgbColor.TryParseHex(hex, out var c);
        p._color = c;
        return p;
    }

    public static EffectParam Toggle(string key, string label, bool value)
        => new(key, label, ParamKind.Toggle) { _flag = value };

    public static EffectParam Choice(string key, string label, IReadOnlyList<string> choices, int index = 0)
        => new(key, label, ParamKind.Choice) { Choices = choices, _choiceIndex = index };

    // ---- live accessors (read by effects each frame) -----------------------

    public double Num { get => _value; set { if (_value != value) { _value = value; Raise(nameof(Num)); } } }
    public RgbColor Color_ { get => _color; set { _color = value; Raise(nameof(Color_)); Raise(nameof(Hex)); } }
    public bool Flag { get => _flag; set { if (_flag != value) { _flag = value; Raise(nameof(Flag)); } } }
    public int ChoiceIndex { get => _choiceIndex; set { if (_choiceIndex != value) { _choiceIndex = value; Raise(nameof(ChoiceIndex)); Raise(nameof(SelectedChoice)); } } }

    public string Hex { get => _color.ToHex(); set { if (RgbColor.TryParseHex(value, out var c)) Color_ = c; } }
    public string? SelectedChoice => (_choiceIndex >= 0 && _choiceIndex < Choices.Count) ? Choices[_choiceIndex] : null;

    /// <summary>Replace the choice list (e.g. inject live sensor ids), preserving selection if possible.</summary>
    public void SetChoices(IReadOnlyList<string> choices, string? keepSelected = null)
    {
        Choices = choices;
        int idx = 0;
        if (keepSelected != null)
            for (int i = 0; i < choices.Count; i++) if (choices[i] == keepSelected) { idx = i; break; }
        ChoiceIndex = idx;
        Raise(nameof(Choices));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
