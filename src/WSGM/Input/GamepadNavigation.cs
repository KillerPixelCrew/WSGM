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
    // A single physical D-pad press reaches this class twice when Steam Input is
    // live under a keyboard-focused window (Settings): once as WSGM's own SDL pad
    // edge, once as Steam's desktop-layout arrow key. Whichever path moves focus
    // first arms a window that swallows the other's duplicate for the same press.
    // The window must exceed the OS keyboard auto-repeat interval relative to the
    // 150 ms pad repeat cadence, or the follower slips through between repeats and
    // double-steps.
    private static readonly TimeSpan CrossSourceSuppression = TimeSpan.FromMilliseconds(250);

    private readonly GamepadService _gamepad;
    private readonly Window _window;
    private readonly Action _back;
    private readonly Func<bool>? _isNintendoLayout;
    private readonly Func<InputElement?>? _preferredFocus;
    private readonly Action<InputElement?>? _secondary;
    private readonly Action? _tabPrevious;
    private readonly Action? _tabNext;

    /// <summary>FocusManager fallback: in a window that never gets OS-activated
    /// (the overlay), GetFocusedElement may not track our programmatic focus.</summary>
    private InputElement? _lastFocused;
    private DateTime _suppressKeyboardUntil;
    private DateTime _suppressPadUntil;
    private bool _loggedFocusFallback;
    private bool _loggedWrap;
    private bool _loggedTextBoxCycle;
    private bool _loggedKeyboardLed;

    /// <summary>Attaches controller navigation to a window.</summary>
    /// <param name="gamepad">The source of controller button presses.</param>
    /// <param name="window">The window whose focusable controls are navigated.</param>
    /// <param name="back">The action invoked for the controller Back button.</param>
    /// <param name="isNintendoLayout">Supplies the current layout. Nintendo labels are swapped relative to Xbox at
    /// the same physical positions: the button labeled A (east, XInput B) confirms
    /// and labeled B (south, XInput A) goes back.</param>
    /// <param name="preferredFocus">The control to focus when nothing suitable holds
    /// focus (e.g. the overlay's primary action instead of its close button).</param>
    /// <param name="secondary">Optional secondary action for the physical west
    /// button (Xbox X), invoked with the currently focused element — the
    /// taskbar's tray-icon context menu.</param>
    /// <param name="tabPrevious">Optional action for the left shoulder button (LB),
    /// fired once per press — switches to the previous tab where a tab strip
    /// exists. Null leaves the button unhandled.</param>
    /// <param name="tabNext">Optional action for the right shoulder button (RB),
    /// fired once per press — switches to the next tab where a tab strip exists.
    /// Null leaves the button unhandled.</param>
    public GamepadNavigation(GamepadService gamepad, Window window, Action back,
        Func<bool>? isNintendoLayout = null, Func<InputElement?>? preferredFocus = null,
        Action<InputElement?>? secondary = null, Action? tabPrevious = null,
        Action? tabNext = null)
    {
        _gamepad = gamepad;
        _window = window;
        _back = back;
        _isNintendoLayout = isNintendoLayout;
        _preferredFocus = preferredFocus;
        _secondary = secondary;
        _tabPrevious = tabPrevious;
        _tabNext = tabNext;
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
        // Physical west button (same position on every layout). Only wired where
        // a secondary action exists (tray-icon context menus on the taskbar).
        if (_secondary is not null && buttons.HasFlag(GamepadButtons.X))
        {
            _secondary(CurrentTarget());
            return;
        }
        // Shoulder buttons cycle tab strips where the host wired them up.
        // ButtonPressed is edge-triggered, so each physical press fires once.
        if (_tabPrevious is not null && buttons.HasFlag(GamepadButtons.LeftShoulder))
        {
            _tabPrevious();
            return;
        }
        if (_tabNext is not null && buttons.HasFlag(GamepadButtons.RightShoulder))
        {
            _tabNext();
            return;
        }

        if (buttons.HasFlag(GamepadButtons.DPadDown) || buttons.HasFlag(GamepadButtons.DPadRight))
        {
            if (PadStepSuppressed())
            {
                return;
            }
            _suppressKeyboardUntil = DateTime.UtcNow + CrossSourceSuppression;
            MoveFocus(NavigationDirection.Next);
        }
        else if (buttons.HasFlag(GamepadButtons.DPadUp) || buttons.HasFlag(GamepadButtons.DPadLeft))
        {
            if (PadStepSuppressed())
            {
                return;
            }
            _suppressKeyboardUntil = DateTime.UtcNow + CrossSourceSuppression;
            MoveFocus(NavigationDirection.Previous);
        }
    }

    /// <summary>True when Steam's mirrored arrow key already moved focus for the
    /// press this pad edge belongs to. The arrow is injected the instant the
    /// button goes down, while the pad is only seen on the next 16 ms poll, so in
    /// a keyboard-focused window the arrow usually leads and this is what stops
    /// the pad edge from stepping a second time.</summary>
    private bool PadStepSuppressed()
    {
        if (DateTime.UtcNow >= _suppressPadUntil)
        {
            return false;
        }
        if (!_loggedKeyboardLed)
        {
            _loggedKeyboardLed = true;
            Log.Info("Gamepad nav: Steam's mirrored arrow key led the pad edge for the same press; suppressing the duplicate pad step.");
        }
        return true;
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
        // arrive near-simultaneously; don't double-step. Whichever lands first
        // moves and suppresses the other: the pad already arms the keyboard window,
        // so when the arrow leads it must arm the pad window symmetrically.
        if (DateTime.UtcNow < _suppressKeyboardUntil)
        {
            return;
        }
        _suppressPadUntil = DateTime.UtcNow + CrossSourceSuppression;
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
        if (next is TextBox)
        {
            // Guard exhausted — a tab cycle of only TextBoxes. Leave focus where
            // it is rather than land on a text field and pop the touch keyboard.
            if (!_loggedTextBoxCycle)
            {
                _loggedTextBoxCycle = true;
                Log.Warn("Gamepad nav: TextBox-skip guard exhausted (only text boxes in tab order), focus unchanged.");
            }
            return;
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
        => element.GetVisualRoot() == _window;

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

    /// <summary>Detaches controller navigation from the window and input service.</summary>
    public void Dispose()
    {
        _gamepad.ButtonPressed -= OnButtons;
        _window.RemoveHandler(InputElement.KeyDownEvent, OnWindowKeyDown);
    }
}
