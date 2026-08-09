using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using WSGM.Core;

namespace WSGM.Input;

/// <summary>Drives Avalonia keyboard focus from gamepad input: D-pad/stick moves
/// focus in the matching visual direction, A activates (synthesized Enter), B invokes a back
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
    private DateTime _suppressConfirmKeyboardUntil;
    private DateTime _suppressConfirmPadUntil;
    private DateTime _suppressBackKeyboardUntil;
    private DateTime _suppressBackPadUntil;
    private bool _raisingSynthesizedInput;
    private bool _loggedFocusFallback;
    private bool _loggedEdge;
    private bool _loggedTextBoxCycle;
    private bool _loggedKeyboardLed;

    /// <summary>Whether this instance currently owns controller navigation for
    /// its window. Covered windows remain alive during surface handovers, so
    /// visibility alone cannot decide which one should receive a press.</summary>
    internal bool IsEnabled { get; set; } = true;

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
        if (!IsEnabled || !_window.IsVisible)
        {
            return;
        }

        var nintendoLayout = _isNintendoLayout?.Invoke() ?? false;
        var confirm = nintendoLayout ? GamepadButtons.B : GamepadButtons.A;
        var back = nintendoLayout ? GamepadButtons.A : GamepadButtons.B;
        var target = CurrentTarget();

        if (buttons.HasFlag(back))
        {
            if (DateTime.UtcNow < _suppressBackPadUntil)
            {
                return;
            }
            _suppressBackKeyboardUntil = DateTime.UtcNow + CrossSourceSuppression;
            if (target is ComboBox { IsDropDownOpen: true } openCombo)
            {
                openCombo.IsDropDownOpen = false;
                return;
            }
            _back();
            return;
        }
        if (buttons.HasFlag(confirm) || buttons.HasFlag(GamepadButtons.Start))
        {
            if (DateTime.UtcNow < _suppressConfirmPadUntil)
            {
                return;
            }
            _suppressConfirmKeyboardUntil = DateTime.UtcNow + CrossSourceSuppression;
            if (target is ComboBox combo)
            {
                // Avalonia does not give a programmatically raised Enter the
                // same popup behavior as a real keyboard event on every host.
                // Own this operation explicitly so SDL confirmation is reliable.
                combo.IsDropDownOpen = !combo.IsDropDownOpen;
            }
            else
            {
                RaiseActivation(target);
            }
            return;
        }
        // Physical west button (same position on every layout). Only wired where
        // a secondary action exists (tray-icon context menus on the taskbar).
        if (_secondary is not null && buttons.HasFlag(GamepadButtons.X))
        {
            _secondary(target);
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

        const GamepadButtons directions = GamepadButtons.DPadUp
            | GamepadButtons.DPadDown
            | GamepadButtons.DPadLeft
            | GamepadButtons.DPadRight;
        var hasDirection = (buttons & directions) != 0;
        if (hasDirection && PadStepSuppressed())
        {
            return;
        }
        // Value controls need
        // the same arrows they would receive from a keyboard: left/right nudges
        // a slider and up/down changes the current ComboBox item. Without this,
        // the taskbar audio panel was keyboard/touch-only despite being focused.
        if (target is Slider slider
            && (buttons.HasFlag(GamepadButtons.DPadLeft)
                || buttons.HasFlag(GamepadButtons.DPadRight)))
        {
            _suppressKeyboardUntil = DateTime.UtcNow + CrossSourceSuppression;
            slider.Value = AdjustSliderValue(
                slider.Value,
                slider.Minimum,
                slider.Maximum,
                slider.TickFrequency,
                buttons.HasFlag(GamepadButtons.DPadRight));
            return;
        }
        if (target is ComboBox { IsDropDownOpen: true } openSelector
            && (buttons.HasFlag(GamepadButtons.DPadUp)
                || buttons.HasFlag(GamepadButtons.DPadDown)))
        {
            _suppressKeyboardUntil = DateTime.UtcNow + CrossSourceSuppression;
            var forward = buttons.HasFlag(GamepadButtons.DPadDown);
            openSelector.SelectedIndex = AdjustComboBoxIndex(
                openSelector.SelectedIndex,
                openSelector.ItemCount,
                forward);
            return;
        }

        var direction = DirectionForButtons(buttons);
        if (direction is not null)
        {
            _suppressKeyboardUntil = DateTime.UtcNow + CrossSourceSuppression;
            MoveFocus(direction.Value);
        }
    }

    /// <summary>Maps each physical direction to Avalonia's matching spatial
    /// direction. Kept pure so the layout-navigation contract is unit-tested.</summary>
    internal static NavigationDirection? DirectionForButtons(GamepadButtons buttons)
    {
        if (buttons.HasFlag(GamepadButtons.DPadUp))
        {
            return NavigationDirection.Up;
        }
        if (buttons.HasFlag(GamepadButtons.DPadDown))
        {
            return NavigationDirection.Down;
        }
        if (buttons.HasFlag(GamepadButtons.DPadLeft))
        {
            return NavigationDirection.Left;
        }
        if (buttons.HasFlag(GamepadButtons.DPadRight))
        {
            return NavigationDirection.Right;
        }
        return null;
    }

    /// <summary>Applies one controller step to a slider and clamps it to the
    /// control's range. Invalid/non-positive tick sizes fall back to one unit.</summary>
    internal static double AdjustSliderValue(
        double value,
        double minimum,
        double maximum,
        double tickFrequency,
        bool increase)
    {
        var step = double.IsFinite(tickFrequency) && tickFrequency > 0 ? tickFrequency : 1;
        return Math.Clamp(value + (increase ? step : -step), minimum, maximum);
    }

    /// <summary>Applies one controller step to an open selector. An unselected
    /// list enters at the nearest end; established selections stop at an edge.</summary>
    internal static int AdjustComboBoxIndex(int selectedIndex, int itemCount, bool increase)
    {
        if (itemCount <= 0)
        {
            return -1;
        }
        if (selectedIndex < 0)
        {
            return increase ? 0 : itemCount - 1;
        }
        return Math.Clamp(selectedIndex + (increase ? 1 : -1), 0, itemCount - 1);
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
        if (!IsEnabled || _raisingSynthesizedInput)
        {
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (DateTime.UtcNow < _suppressBackKeyboardUntil)
            {
                e.Handled = true;
                return;
            }
            _suppressBackPadUntil = DateTime.UtcNow + CrossSourceSuppression;
            if (CurrentTarget() is ComboBox { IsDropDownOpen: true } combo)
            {
                e.Handled = true;
                combo.IsDropDownOpen = false;
            }
            return;
        }
        if (e.Key is Key.Enter or Key.Space)
        {
            // Confirmation has the same two-source path as directions. Without
            // this gate, SDL opens a ComboBox and Steam's mirrored Enter closes
            // it immediately (or a Button command fires twice).
            if (DateTime.UtcNow < _suppressConfirmKeyboardUntil)
            {
                e.Handled = true;
                return;
            }
            _suppressConfirmPadUntil = DateTime.UtcNow + CrossSourceSuppression;
            if (CurrentTarget() is ComboBox combo)
            {
                e.Handled = true;
                combo.IsDropDownOpen = !combo.IsDropDownOpen;
            }
            return;
        }
        var direction = e.Key switch
        {
            Key.Up => NavigationDirection.Up,
            Key.Down => NavigationDirection.Down,
            Key.Left => NavigationDirection.Left,
            Key.Right => (NavigationDirection?)NavigationDirection.Right,
            _ => null,
        };
        if (direction is null)
        {
            return;
        }
        // Popup content lives in a separate top level, so use the same fallback
        // target resolution as SDL; while a ComboBox popup owns OS focus this
        // still resolves to the selector that opened it.
        var focused = CurrentTarget();
        // Controls keep only the directions that operate their current state.
        // A closed ComboBox must let Up/Down leave the row; A opens it, after
        // which those directions select an item. Likewise, a horizontal Slider
        // keeps Left/Right while Up/Down continues through the visual layout.
        var controlConsumesDirection = focused is TextBox
            || (focused is Slider && e.Key is Key.Left or Key.Right)
            || (focused is ComboBox { IsDropDownOpen: true }
                && e.Key is Key.Up or Key.Down);
        if (controlConsumesDirection)
        {
            // SDL already applied this physical press directly. Consume Steam's
            // mirrored key before the control sees it and applies a second step.
            if (DateTime.UtcNow < _suppressKeyboardUntil)
            {
                e.Handled = true;
                return;
            }
            // The control consumes this key itself. Still arm the opposite input
            // source so the SDL edge for the same physical press cannot apply a
            // second value change a few milliseconds later.
            _suppressPadUntil = DateTime.UtcNow + CrossSourceSuppression;
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
        var next = NextInDirection(current, direction);
        // Skip text fields during pad/arrow traversal: focusing one makes Windows
        // pop the touch keyboard on keyboard-less handhelds. They stay reachable
        // by tapping them (which is when the keyboard IS wanted) and by Tab.
        var guard = 0;
        while (next is TextBox textBox && guard++ < 64)
        {
            next = NextInDirection(textBox, direction);
        }
        if (next is TextBox)
        {
            // Guard exhausted — a tab cycle of only TextBoxes. Leave focus where
            // it is rather than land on a text field and pop the touch keyboard.
            if (!_loggedTextBoxCycle)
            {
                _loggedTextBoxCycle = true;
                Log.Warn("Gamepad nav: TextBox-skip guard exhausted, focus unchanged.");
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
            if (!_loggedEdge)
            {
                _loggedEdge = true;
                Log.Info($"Gamepad nav: no focusable control in the {direction} direction; focus unchanged.");
            }
        }
    }

    /// <summary>Peeks at the next element in the requested visual direction without moving focus —
    /// the TextBox skip above depends on looking before landing, because merely
    /// focusing a text field pops the touch keyboard. Scoped to this window so a
    /// search cannot walk into another top level.</summary>
    private IInputElement? NextInDirection(IInputElement from, NavigationDirection direction)
        => TopLevel.GetTopLevel(_window)?.FocusManager?.FindNextElement(
            direction,
            new FindNextElementOptions { FocusedElement = from, SearchRoot = _window });

    private bool IsInWindow(InputElement element)
        => TopLevel.GetTopLevel(element) == _window;

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

    private void RaiseActivation(InputElement? element)
    {
        if (element is null)
        {
            return;
        }
        // Synthesize Enter so the control's own activation logic runs
        // (Button click + command, ToggleSwitch flip, ...).
        _raisingSynthesizedInput = true;
        try
        {
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
        finally
        {
            _raisingSynthesizedInput = false;
        }
    }

    /// <summary>Detaches controller navigation from the window and input service.</summary>
    public void Dispose()
    {
        _gamepad.ButtonPressed -= OnButtons;
        _window.RemoveHandler(InputElement.KeyDownEvent, OnWindowKeyDown);
    }
}
