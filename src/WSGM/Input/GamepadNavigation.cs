using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using WSGM.Core;

namespace WSGM.Input;

/// <summary>Drives Avalonia keyboard focus from gamepad input: D-pad/stick moves
/// focus through the tab order, A activates (synthesized Enter), B invokes a back
/// action. Arrow keys mirror the D-pad so windows that hold real keyboard focus
/// (Settings) are also navigable by Steam Input's desktop-layout key emission.
/// Deterministic and AOT-safe.</summary>
public sealed class GamepadNavigation : IDisposable
{
    // Must exceed the OS keyboard auto-repeat interval relative to the 150 ms pad
    // repeat cadence, or synthesized arrows slip through between pad repeats and
    // double-step the focus.
    private static readonly TimeSpan KeyboardSuppression = TimeSpan.FromMilliseconds(250);

    private readonly GamepadService _gamepad;
    private readonly Window _window;
    private readonly Action _back;
    private readonly Func<bool>? _isNintendoLayout;
    private readonly Func<InputElement?>? _preferredFocus;

    /// <summary>FocusManager fallback: in a window that never gets OS-activated
    /// (the overlay), GetFocusedElement may not track our programmatic focus.</summary>
    private InputElement? _lastFocused;
    private DateTime _suppressKeyboardUntil;
    private bool _loggedFocusFallback;
    private bool _loggedWrap;

    /// <param name="isNintendoLayout">Supplies the current layout. Nintendo labels are swapped relative to Xbox at
    /// the same physical positions: the button labeled A (east, XInput B) confirms
    /// and labeled B (south, XInput A) goes back.</param>
    /// <param name="preferredFocus">The control to focus when nothing suitable holds
    /// focus (e.g. the overlay's primary action instead of its close button).</param>
    public GamepadNavigation(GamepadService gamepad, Window window, Action back,
        Func<bool>? isNintendoLayout = null, Func<InputElement?>? preferredFocus = null)
    {
        _gamepad = gamepad;
        _window = window;
        _back = back;
        _isNintendoLayout = isNintendoLayout;
        _preferredFocus = preferredFocus;
        _gamepad.ButtonPressed += OnButtons;
        // Tunnel so the arrows aren't consumed by a ScrollViewer for scrolling first.
        _window.AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnButtons(GamepadButtons buttons)
    {
        if (!_window.IsVisible)
        {
            return;
        }

        var nintendoLayout = _isNintendoLayout?.Invoke() ?? false;
        var confirm = nintendoLayout ? GamepadButtons.B : GamepadButtons.A;
        var back = nintendoLayout ? GamepadButtons.A : GamepadButtons.B;

        if (buttons.HasFlag(back))
        {
            _back();
            return;
        }
        if (buttons.HasFlag(confirm) || buttons.HasFlag(GamepadButtons.Start))
        {
            Activate(CurrentTarget());
            return;
        }

        if (buttons.HasFlag(GamepadButtons.DPadDown) || buttons.HasFlag(GamepadButtons.DPadRight))
        {
            _suppressKeyboardUntil = DateTime.UtcNow + KeyboardSuppression;
            MoveFocus(NavigationDirection.Next);
        }
        else if (buttons.HasFlag(GamepadButtons.DPadUp) || buttons.HasFlag(GamepadButtons.DPadLeft))
        {
            _suppressKeyboardUntil = DateTime.UtcNow + KeyboardSuppression;
            MoveFocus(NavigationDirection.Previous);
        }
    }

    /// <summary>Arrow keys mirror the D-pad. With Steam Input active and this window
    /// focused, Steam's desktop layout emits exactly these keys — making the pad
    /// usable even while Steam swallows it from every gamepad API.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var direction = e.Key switch
        {
            Key.Down or Key.Right => NavigationDirection.Next,
            Key.Up or Key.Left => (NavigationDirection?)NavigationDirection.Previous,
            _ => null,
        };
        if (direction is null)
        {
            return;
        }
        // Controls that consume arrows themselves keep them (caret movement,
        // dropdown selection, value nudging).
        if (GetFocused() is TextBox or ComboBox or Slider)
        {
            return;
        }
        e.Handled = true;
        // A pad event and Steam's synthesized keystroke for the same physical press
        // arrive near-simultaneously; don't double-step.
        if (DateTime.UtcNow < _suppressKeyboardUntil)
        {
            return;
        }
        MoveFocus(direction.Value);
    }

    private InputElement? GetFocused()
        => TopLevel.GetTopLevel(_window)?.FocusManager?.GetFocusedElement() as InputElement;

    /// <summary>The element navigation should act on: FocusManager's answer when it
    /// is one of ours, otherwise the last element this class focused.</summary>
    private InputElement? CurrentTarget()
    {
        var focused = GetFocused();
        if (focused is not null && focused is not Window && IsInWindow(focused))
        {
            _lastFocused = focused;
            return focused;
        }
        if (_lastFocused is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true } last && IsInWindow(last))
        {
            if (!_loggedFocusFallback)
            {
                _loggedFocusFallback = true;
                Log.Info("Gamepad nav: FocusManager lost track (never-activated window), using last focused element.");
            }
            return last;
        }
        return null;
    }

    private void MoveFocus(NavigationDirection direction)
    {
        var current = CurrentTarget();
        if (current is null)
        {
            FocusFirst();
            return;
        }
        var next = KeyboardNavigationHandler.GetNext(current, direction);
        // Skip text fields during pad/arrow traversal: focusing one makes Windows
        // pop the touch keyboard on keyboard-less handhelds. They stay reachable
        // by tapping them (which is when the keyboard IS wanted) and by Tab.
        var guard = 0;
        while (next is TextBox textBox && guard++ < 64)
        {
            next = KeyboardNavigationHandler.GetNext(textBox, direction);
        }
        if (next is InputElement input)
        {
            input.Focus(NavigationMethod.Directional);
            _lastFocused = input;
        }
        else
        {
            if (!_loggedWrap)
            {
                _loggedWrap = true;
                Log.Info("Gamepad nav: end of tab order, wrapping to default element.");
            }
            FocusFirst();
        }
    }

    private bool IsInWindow(InputElement element)
        => (element as Avalonia.Visual)?.GetVisualRoot() == _window;

    private void FocusFirst()
    {
        if (_preferredFocus?.Invoke() is { Focusable: true, IsEffectivelyEnabled: true, IsEffectivelyVisible: true } preferred)
        {
            preferred.Focus(NavigationMethod.Directional);
            _lastFocused = preferred;
            return;
        }
        foreach (var descendant in _window.GetVisualDescendants())
        {
            if (descendant is InputElement { Focusable: true, IsEffectivelyEnabled: true, IsEffectivelyVisible: true } input
                and not TextBox)
            {
                input.Focus(NavigationMethod.Directional);
                _lastFocused = input;
                return;
            }
        }
        Log.Warn("Gamepad nav: no focusable element found in window.");
    }

    private static void Activate(InputElement? element)
    {
        if (element is null)
        {
            return;
        }
        // Synthesize Enter so the control's own activation logic runs
        // (Button click + command, ToggleSwitch flip, ...).
        element.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            Source = element,
        });
        element.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyUpEvent,
            Key = Key.Enter,
            Source = element,
        });
    }

    public void Dispose()
    {
        _gamepad.ButtonPressed -= OnButtons;
        _window.RemoveHandler(InputElement.KeyDownEvent, OnWindowKeyDown);
    }
}
