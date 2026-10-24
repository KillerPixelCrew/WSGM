using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace WSGM.Controls;

/// <summary>A compact touch keyboard with symbols and PC keys on secondary layouts.</summary>
public sealed class OnScreenKeyboard : Decorator
{
    /// <summary>Defines the Avalonia property holding the text field the keys edit.</summary>
    public static readonly StyledProperty<TextBox?> TargetProperty =
        AvaloniaProperty.Register<OnScreenKeyboard, TextBox?>(nameof(Target));

    private readonly List<(Button Button, KeyFace Face)> _keys = [];
    private readonly StackPanel _mainRows = new() { Spacing = 1 };
    private readonly StackPanel _numberRows = new() { Spacing = 1, IsVisible = false };
    private readonly StackPanel _toolRows = new() { Spacing = 1, IsVisible = false };
    private readonly StackPanel _padRows = new() { Spacing = 1, IsVisible = false };
    private bool _capsLock;
    private KeyModifiers _modifiers;
    private bool _numLock = true;

    /// <summary>Creates four tightly packed rows per layout, retaining touch and controller targets.</summary>
    public OnScreenKeyboard()
    {
        var top = new List<KeyFace>();
        AddLetters(top, "qwertyuiop");
        top.Add(Command(Key.Back, "⌫", 1.45));
        AddRow(_mainRows, top);
        var home = new List<KeyFace>();
        AddLetters(home, "asdfghjkl");
        home.Add(Command(Key.Enter, "↵", 1.7));
        AddRow(_mainRows, home);
        var lower = new List<KeyFace> { Modifier(Key.LeftShift, "⇧", KeyModifiers.Shift, 1.5) };
        AddLetters(lower, "zxcvbnm");
        lower.AddRange([
            Character(Key.OemComma, ",", "<"), Character(Key.OemPeriod, ".", ">"),
            Character(Key.OemQuestion, "?", "/")
        ]);
        AddRow(_mainRows, lower);
        AddRow(_mainRows, [
            Command(Key.None, "&123", 1.5), Modifier(Key.LeftCtrl, "Ctrl", KeyModifiers.Control, 1),
            Character(Key.Space, " ", " ", 5, "Space"), Command(Key.Left, "←"), Command(Key.Right, "→"),
            Command(Key.None, "More", 1.5)
        ]);
        AddRow(_numberRows, [
            Character(Key.D1, "1", "!"), Character(Key.D2, "2", "@"), Character(Key.D3, "3", "#"),
            Character(Key.D4, "4", "$"), Character(Key.D5, "5", "%"), Character(Key.D6, "6", "^"),
            Character(Key.D7, "7", "&"), Character(Key.D8, "8", "*"), Character(Key.D9, "9", "("),
            Character(Key.D0, "0", ")"), Command(Key.Back, "⌫", 1.45)
        ]);
        AddRow(_numberRows, [
            Character(Key.D2, "@", "@"), Character(Key.D3, "#", "#"), Character(Key.D4, "$", "$"),
            Character(Key.D5, "%", "%"), Character(Key.D7, "&", "&"), Character(Key.D8, "*", "*"),
            Character(Key.D9, "(", "("), Character(Key.D0, ")", ")"), Character(Key.D1, "!", "!"),
            Character(Key.OemQuestion, "?", "?"), Command(Key.Enter, "↵", 1.7)
        ]);
        AddRow(_numberRows, [
            Modifier(Key.LeftShift, "⇧", KeyModifiers.Shift, 1.5),
            Character(Key.OemPlus, "+", "="), Character(Key.OemMinus, "-", "_"),
            Character(Key.OemQuestion, "/", "?"), Character(Key.OemSemicolon, ":", ";"),
            Character(Key.OemQuotes, "\"", "'"), Character(Key.OemComma, ",", "<"),
            Character(Key.OemPeriod, ".", ">"), Character(Key.Oem3, "`", "~")
        ]);
        AddRow(_numberRows, [
            Command(Key.None, "ABC", 1.5), Modifier(Key.LeftCtrl, "Ctrl", KeyModifiers.Control, 1),
            Character(Key.Space, " ", " ", 5, "Space"), Command(Key.Left, "←"), Command(Key.Right, "→"),
            Command(Key.None, "More", 1.5)
        ]);
        var functions = new List<KeyFace> { Command(Key.Escape, "Esc") };
        for (var index = 0; index < 12; index++)
        {
            functions.Add(Command(Key.F1 + index, $"F{index + 1}"));
        }

        AddRow(_toolRows, functions);
        AddRow(_toolRows, [
            Modifier(Key.LeftCtrl, "Ctrl", KeyModifiers.Control, 1),
            Modifier(Key.LeftAlt, "Alt", KeyModifiers.Alt, 1), Modifier(Key.LWin, "Win", KeyModifiers.Meta, 1),
            Command(Key.CapsLock, "Caps"), Command(Key.Tab, "Tab"), Command(Key.Home, "Home"),
            Command(Key.End, "End"), Command(Key.PageUp, "PgUp"), Command(Key.PageDown, "PgDn"),
            Command(Key.Insert, "Insert"), Command(Key.Delete, "Delete")
        ]);
        AddRow(_toolRows, [
            Character(Key.OemOpenBrackets, "[", "{"), Character(Key.OemCloseBrackets, "]", "}"),
            Character(Key.OemPipe, "\\", "|"), Character(Key.Oem3, "`", "~"),
            Character(Key.OemSemicolon, ";", ":"), Character(Key.OemQuotes, "'", "\""),
            Character(Key.OemMinus, "-", "_"), Character(Key.OemPlus, "=", "+"),
            Command(Key.PrintScreen, "PrtSc"),
            Command(Key.Scroll, "Scroll Lock"), Command(Key.Pause, "Pause"), Command(Key.Apps, "Menu"),
            Command(Key.Back, "⌫")
        ]);
        AddRow(_toolRows, [
            Command(Key.None, "ABC", 1.5), Command(Key.None, "Num pad", 1.5),
            Modifier(Key.LeftShift, "⇧", KeyModifiers.Shift, 1), Character(Key.Space, " ", " ", 3, "Space"),
            Command(Key.Left, "←"), Command(Key.Down, "↓"), Command(Key.Up, "↑"), Command(Key.Right, "→"),
            Command(Key.Enter, "↵", 1.5)
        ]);
        AddRow(_padRows, [
            Command(Key.NumLock, "Num Lock"), Character(Key.Divide, "/", "/"),
            Character(Key.Multiply, "*", "*"), Character(Key.Subtract, "-", "-"), Command(Key.None, "More")
        ]);
        AddRow(_padRows, [
            Character(Key.NumPad7, "7", "7"), Character(Key.NumPad8, "8", "8"),
            Character(Key.NumPad9, "9", "9"), Character(Key.Add, "+", "+")
        ]);
        AddRow(_padRows, [
            Character(Key.NumPad4, "4", "4"), Character(Key.NumPad5, "5", "5"),
            Character(Key.NumPad6, "6", "6"), Command(Key.Back, "Backspace")
        ]);
        AddRow(_padRows, [
            Character(Key.NumPad1, "1", "1"), Character(Key.NumPad2, "2", "2"),
            Character(Key.NumPad3, "3", "3"), Character(Key.NumPad0, "0", "0"),
            Character(Key.Decimal, ".", "."), Command(Key.Enter, "↵"),
            Command(Key.None, "ABC")
        ]);
        var pages = new Grid();
        pages.Children.Add(_mainRows);
        pages.Children.Add(_numberRows);
        pages.Children.Add(_toolRows);
        pages.Children.Add(_padRows);
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
        var row = new Grid { ColumnSpacing = 1 };
        foreach (var face in faces)
        {
            var button = new Button
            {
                Content = face.Label,
                Tag = face.Key,
                MinWidth = 44,
                Height = 44,
                Padding = default,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = default,
                BorderThickness = new Thickness(2),
                FocusAdorner = null,
                FontSize = face.Printable && face.Key != Key.Space
                           || face.Key is Key.Back or Key.Enter or Key.LeftShift or Key.Left or Key.Right or Key.Up or Key.Down
                    ? 20 : 14
            };
            AutomationProperties.SetName(button, face.Key switch
            {
                Key.Back => "Backspace",
                Key.Enter => "Enter",
                Key.LeftShift or Key.RightShift => "Shift",
                _ => face.Label
            });
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
            var selected = face.Label switch
            {
                "&123" => _numberRows,
                "More" => _toolRows,
                "Num pad" => _padRows,
                _ => _mainRows
            };
            _mainRows.IsVisible = ReferenceEquals(selected, _mainRows);
            _numberRows.IsVisible = ReferenceEquals(selected, _numberRows);
            _toolRows.IsVisible = ReferenceEquals(selected, _toolRows);
            _padRows.IsVisible = ReferenceEquals(selected, _padRows);
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

    private static KeyFace Command(Key key, string label, double units = 1)
    {
        return new KeyFace(key, label, label, units);
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
        _toolRows.IsVisible = false;
        _padRows.IsVisible = false;
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
        string? LabelOverride = null)
    {
        internal string Label => LabelOverride ?? Normal;
    }
}
