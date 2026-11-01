using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>
///     Records a key combination from a keyboard: every key held at once, in press order, finishing when they
///     are all released. Esc is left to the surface, which it closes.
/// </summary>
/// <remarks>
///     WSGM cannot tell a keyboard from the handheld's own HID keyboards, so the field reads the window's own
///     key events while it is in the tree. They tunnel through the window ahead of the controls, after the
///     controller navigation: the field takes back focus that a navigation key moved, and keeps Enter, Space
///     and Tab from operating the surface's buttons. Keys in the first moments are ignored, because the press
///     that opened the field, or Steam's keyboard echo of a controller press, arrives then.
/// </remarks>
internal sealed class HotkeyCapture : Border
{
    private static readonly TimeSpan DefaultSettle = TimeSpan.FromMilliseconds(300);
    private readonly HashSet<int> _held = [];
    private readonly List<int> _keys = [];
    private readonly TimeSpan _settle;

    private readonly ContentControl _shown = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Focusable = false
    };

    private long _acceptAfter;
    private bool _finished;
    private TopLevel? _window;

    /// <summary>Creates an empty field.</summary>
    /// <param name="settle">How long after it appears keys are ignored; null is 300 ms.</param>
    internal HotkeyCapture(TimeSpan? settle = null)
    {
        _settle = settle ?? DefaultSettle;
        Classes.Add("device-group");
        Classes.Add("hotkey-capture");
        Focusable = true;
        MinHeight = 76;
        Padding = new Thickness(12);
        Child = _shown;
        AutomationProperties.SetName(this, "Press the keys");
        ShowKeys();
        AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this);
            _acceptAfter = Environment.TickCount64 + (long)_settle.TotalMilliseconds;
            _window?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, true);
            _window?.AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel, true);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _window?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
            _window?.RemoveHandler(KeyUpEvent, OnWindowKeyUp);
            _window = null;
        };
    }

    /// <summary>Raised once with the keys, in press order, when every captured key has been released.</summary>
    internal event Action<IReadOnlyList<int>>? Captured;

    /// <summary>Maps an Avalonia key to the Windows virtual key a hotkey sends.</summary>
    /// <param name="key">The pressed key.</param>
    /// <returns>The virtual key; left and right modifiers both map to the switch's key. Null for other keys.</returns>
    internal static int? VirtualKey(Key key)
    {
        return key switch
        {
            >= Key.A and <= Key.Z => 'A' + (key - Key.A),
            >= Key.D0 and <= Key.D9 => '0' + (key - Key.D0),
            >= Key.F1 and <= Key.F24 => 0x70 + (key - Key.F1),
            >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (key - Key.NumPad0),
            Key.LeftCtrl or Key.RightCtrl => HotkeyKeys.Control,
            Key.LeftAlt or Key.RightAlt => HotkeyKeys.Alt,
            Key.LeftShift or Key.RightShift => HotkeyKeys.Shift,
            Key.LWin or Key.RWin => HotkeyKeys.Windows,
            Key.Insert => HotkeyKeys.Insert,
            Key.Delete => 0x2E,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Up => 0x26,
            Key.Down => 0x28,
            Key.Left => 0x25,
            Key.Right => 0x27,
            Key.Space => 0x20,
            Key.Enter => 0x0D,
            Key.Tab => 0x09,
            Key.Back => 0x08,
            Key.PrintScreen => 0x2C,
            Key.Pause => 0x13,
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Subtract => 0x6D,
            Key.Decimal => 0x6E,
            Key.Divide => 0x6F,
            Key.NumLock => 0x90,
            Key.OemMinus => 0xBD,
            Key.OemPlus => 0xBB,
            Key.OemOpenBrackets => 0xDB,
            Key.OemCloseBrackets => 0xDD,
            Key.OemPipe => 0xDC,
            Key.OemSemicolon => 0xBA,
            Key.OemQuotes => 0xDE,
            Key.OemComma => 0xBC,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemTilde => 0xC0,
            _ => null
        };
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e is OnScreenKeyboard.EditorKeyEventArgs || _finished || e.Key == Key.Escape)
        {
            return;
        }

        e.Handled = true;
        if (Environment.TickCount64 < _acceptAfter || VirtualKey(e.Key) is not { } key)
        {
            return;
        }

        if (_held.Add(key) && !_keys.Contains(key))
        {
            _keys.Add(key);
            ShowKeys();
        }

        // A navigation key moved focus before this handler saw it; the field keeps it.
        Dispatcher.UIThread.Post(() =>
        {
            if (IsEffectivelyVisible && !IsFocused)
            {
                Focus(NavigationMethod.Directional);
            }
        });
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e is OnScreenKeyboard.EditorKeyEventArgs || _finished || VirtualKey(e.Key) is not { } key
            || !_held.Remove(key))
        {
            return;
        }

        e.Handled = true;
        if (_held.Count == 0 && _keys.Count > 0)
        {
            _finished = true;
            Captured?.Invoke([.. _keys]);
        }
    }

    private void ShowKeys()
    {
        _shown.Content = _keys.Count == 0
            ? new TextBlock { Text = "Waiting for keys…", Classes = { "caption" } }
            : Keycaps.Create(_keys, true);
    }
}
