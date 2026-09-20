using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace WSGM.Controls;

/// <summary>A full PC keyboard that edits a local text field without sending global input.</summary>
public sealed class OnScreenKeyboard : Decorator
{
    private static readonly StyledProperty<TextBox?> TargetProperty =
        AvaloniaProperty.Register<OnScreenKeyboard, TextBox?>(nameof(Target));

    private readonly List<(Button Button, KeyFace Face)> _keys = [];
    private readonly StackPanel _mainRows = new() { Spacing = 4 };
    private readonly StackPanel _numberRows = new() { Spacing = 4, IsVisible = false };
    private bool _capsLock;
    private KeyModifiers _modifiers;
    private bool _numLock = true;

    /// <summary>Creates persistent PC key rows with touch and controller focus targets.</summary>
    public OnScreenKeyboard()
    {
        var rows = _mainRows;
        var functions = new List<KeyFace> { Command(Key.Escape, "Esc") };
        for (var index = 0; index < 12; index++)
        {
            functions.Add(Command(Key.F1 + index, $"F{index + 1}", gapBefore: index % 4 == 0));
        }

        functions.AddRange([
            Command(Key.Home, "Home", gapBefore: true), Command(Key.End, "End"),
            Command(Key.PageUp, "PgUp"), Command(Key.PageDown, "PgDn")
        ]);
        AddRow(rows, functions);
        AddRow(rows,
        [
            Character(Key.Oem3, "`", "~"), Character(Key.D1, "1", "!"), Character(Key.D2, "2", "@"),
            Character(Key.D3, "3", "#"), Character(Key.D4, "4", "$"), Character(Key.D5, "5", "%"),
            Character(Key.D6, "6", "^"), Character(Key.D7, "7", "&"), Character(Key.D8, "8", "*"),
            Character(Key.D9, "9", "("), Character(Key.D0, "0", ")"), Character(Key.OemMinus, "-", "_"),
            Character(Key.OemPlus, "=", "+"), Command(Key.Back, "Backspace", 2), Command(Key.Delete, "Del")
        ]);
        var top = new List<KeyFace> { Command(Key.Tab, "Tab", 1.5) };
        AddLetters(top, "qwertyuiop");
        top.AddRange([
            Character(Key.OemOpenBrackets, "[", "{"), Character(Key.OemCloseBrackets, "]", "}"),
            Character(Key.OemPipe, "\\", "|", 1.5)
        ]);
        AddRow(rows, top);
        var home = new List<KeyFace> { new(Key.CapsLock, "Caps Lock", "Caps Lock", 1.8) };
        AddLetters(home, "asdfghjkl");
        home.AddRange([
            Character(Key.OemSemicolon, ";", ":"), Character(Key.OemQuotes, "'", "\""),
            Command(Key.Enter, "Enter", 2.2)
        ]);
        AddRow(rows, home);
        var lower = new List<KeyFace> { Modifier(Key.LeftShift, "Shift", KeyModifiers.Shift, 2.2) };
        AddLetters(lower, "zxcvbnm");
        lower.AddRange([
            Character(Key.OemComma, ",", "<"), Character(Key.OemPeriod, ".", ">"),
            Character(Key.OemQuestion, "/", "?"), Modifier(Key.RightShift, "Shift", KeyModifiers.Shift, 2.2),
            Command(Key.Up, "↑")
        ]);
        AddRow(rows, lower);
        AddRow(rows,
        [
            Modifier(Key.LeftCtrl, "Ctrl", KeyModifiers.Control, 1.3),
            Modifier(Key.LWin, "Win", KeyModifiers.Meta, 1.3),
            Modifier(Key.LeftAlt, "Alt", KeyModifiers.Alt, 1.3),
            Character(Key.Space, " ", " ", 5, "Space"),
            Modifier(Key.RightAlt, "Alt", KeyModifiers.Alt, 1.3),
            Modifier(Key.RightCtrl, "Ctrl", KeyModifiers.Control, 1.3),
            Command(Key.Left, "←"), Command(Key.Down, "↓"), Command(Key.Right, "→"),
            Command(Key.None, "Num pad", 1.8)
        ]);
        AddRow(_numberRows, [
            Command(Key.Insert, "Insert"), Command(Key.PrintScreen, "Print Screen"),
            Command(Key.Scroll, "Scroll Lock"), Command(Key.Pause, "Pause"), Command(Key.Apps, "Menu")
        ]);
        AddRow(_numberRows, [
            Command(Key.NumLock, "Num Lock"), Character(Key.Divide, "/", "/"),
            Character(Key.Multiply, "*", "*"), Character(Key.Subtract, "-", "-")
        ]);
        AddRow(_numberRows, [
            Character(Key.NumPad7, "7", "7"), Character(Key.NumPad8, "8", "8"),
            Character(Key.NumPad9, "9", "9"), Character(Key.Add, "+", "+")
        ]);
        AddRow(_numberRows, [
            Character(Key.NumPad4, "4", "4"), Character(Key.NumPad5, "5", "5"),
            Character(Key.NumPad6, "6", "6"), Command(Key.Back, "Backspace")
        ]);
        AddRow(_numberRows, [
            Character(Key.NumPad1, "1", "1"), Character(Key.NumPad2, "2", "2"),
            Character(Key.NumPad3, "3", "3"), Command(Key.Enter, "Enter")
        ]);
        AddRow(_numberRows, [
            Character(Key.NumPad0, "0", "0", 2), Character(Key.Decimal, ".", "."),
            Command(Key.None, "ABC")
        ]);
        var pages = new Grid();
        pages.Children.Add(_mainRows);
        pages.Children.Add(_numberRows);
        Child = pages;
        UpdateKeys();
    }

    /// <summary>Gets or sets the local editor that receives keys and text.</summary>
    public TextBox? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>Raised when unmodified Enter accepts a single-line field.</summary>
    public event EventHandler? Accepted;

    /// <summary>Raised when Escape cancels local text entry.</summary>
    public event EventHandler? Cancelled;

    private void AddRow(StackPanel rows, IReadOnlyList<KeyFace> faces)
    {
        var row = new Grid { ColumnSpacing = 4 };
        foreach (var face in faces)
        {
            var button = new Button
            {
                Content = face.Label,
                Tag = face.Key,
                MinWidth = 44,
                Height = 44,
                Padding = new Thickness(3, 0),
                Margin = face.GapBefore ? new Thickness(6, 0, 0, 0) : default,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(2),
                FocusAdorner = null,
                FontSize = 13
            };
            AutomationProperties.SetName(button, face.Label);
            button.Click += (_, _) => Press(face);
            Grid.SetColumn(button, row.Children.Count);
            row.ColumnDefinitions.Add(new ColumnDefinition(face.Units, GridUnitType.Star));
            row.Children.Add(button);
            _keys.Add((button, face));
        }

        rows.Children.Add(row);
    }

    private void Press(KeyFace face)
    {
        if (face.Key == Key.None)
        {
            _mainRows.IsVisible = !_mainRows.IsVisible;
            _numberRows.IsVisible = !_mainRows.IsVisible;
            _modifiers = KeyModifiers.None;
            UpdateKeys();
            foreach (var (button, key) in _keys)
            {
                if (key.Key == Key.None && button.IsEffectivelyVisible)
                {
                    button.Focus(NavigationMethod.Directional);
                    break;
                }
            }

            return;
        }

        if (face.Key == Key.NumLock)
        {
            _numLock = !_numLock;
            UpdateKeys();
            return;
        }

        if (!_numLock && face.Key is >= Key.NumPad0 and <= Key.NumPad9 or Key.Decimal)
        {
            var navigation = face.Key switch
            {
                Key.NumPad0 => Key.Insert, Key.NumPad1 => Key.End, Key.NumPad2 => Key.Down,
                Key.NumPad3 => Key.PageDown, Key.NumPad4 => Key.Left, Key.NumPad6 => Key.Right,
                Key.NumPad7 => Key.Home, Key.NumPad8 => Key.Up, Key.NumPad9 => Key.PageUp,
                Key.Decimal => Key.Delete, _ => Key.Clear
            };
            face = Command(navigation, face.Label);
        }

        if (face.Modifier != KeyModifiers.None)
        {
            _modifiers ^= face.Modifier;
            UpdateKeys();
            return;
        }

        if (face.Key == Key.CapsLock)
        {
            _capsLock = !_capsLock;
            UpdateKeys();
            return;
        }

        if (Target is not { } target)
        {
            return;
        }

        var modifiers = _modifiers;
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var controlChord = (modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        var down = new EditorKeyEventArgs
        {
            RoutedEvent = KeyDownEvent, Key = face.Key, KeyModifiers = modifiers, Source = target
        };
        target.RaiseEvent(down);
        if (face.Printable && !controlChord && !down.Handled)
        {
            var uppercase = face.Key is >= Key.A and <= Key.Z ? shift ^ _capsLock : shift;
            target.RaiseEvent(new TextInputEventArgs
            {
                RoutedEvent = TextInputEvent, Source = target, Text = uppercase ? face.Shifted : face.Normal
            });
        }

        target.RaiseEvent(new EditorKeyEventArgs
        {
            RoutedEvent = KeyUpEvent, Key = face.Key, KeyModifiers = modifiers, Source = target
        });
        _modifiers = KeyModifiers.None;
        UpdateKeys();
        if (face.Key == Key.Enter && modifiers == KeyModifiers.None && !target.AcceptsReturn)
        {
            Accepted?.Invoke(this, EventArgs.Empty);
        }
        else if (face.Key == Key.Escape)
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateKeys()
    {
        foreach (var (button, face) in _keys)
        {
            var latched = (face.Modifier != KeyModifiers.None && _modifiers.HasFlag(face.Modifier))
                          || (face.Key == Key.CapsLock && _capsLock) || (face.Key == Key.NumLock && _numLock);
            button.Classes.Set("primary", latched);
            button.Classes.Set("latched", latched);
            var shifted = face.Key is >= Key.A and <= Key.Z
                ? _modifiers.HasFlag(KeyModifiers.Shift) ^ _capsLock
                : _modifiers.HasFlag(KeyModifiers.Shift);
            button.Content = face.Printable && face.Key != Key.Space
                ? shifted ? face.Shifted : face.Normal
                : face.Label;
        }
    }

    private static void AddLetters(List<KeyFace> row, string letters)
    {
        foreach (var letter in letters)
        {
            row.Add(Character(Key.A + (letter - 'a'), letter.ToString(), char.ToUpperInvariant(letter).ToString()));
        }
    }

    private static KeyFace Character(Key key, string normal, string shifted, double units = 1, string? label = null)
    {
        return new KeyFace(key, normal, shifted, units, true, LabelOverride: label);
    }

    private static KeyFace Command(Key key, string label, double units = 1, bool gapBefore = false)
    {
        return new KeyFace(key, label, label, units, GapBefore: gapBefore);
    }

    private static KeyFace Modifier(Key key, string label, KeyModifiers modifier, double units)
    {
        return new KeyFace(key, label, label, units, Modifier: modifier);
    }

    /// <summary>Releases all latched modifier state and Caps Lock.</summary>
    public void Reset()
    {
        _modifiers = KeyModifiers.None;
        _capsLock = false;
        _numLock = true;
        _mainRows.IsVisible = true;
        _numberRows.IsVisible = false;
        UpdateKeys();
    }

    /// <summary>Marks local editor events so overlay navigation does not consume them.</summary>
    internal sealed class EditorKeyEventArgs : KeyEventArgs;

    private sealed record KeyFace(
        Key Key,
        string Normal,
        string Shifted,
        double Units,
        bool Printable = false,
        KeyModifiers Modifier = KeyModifiers.None,
        bool GapBefore = false,
        string? LabelOverride = null)
    {
        internal string Label => LabelOverride ?? Normal;
    }
}
