using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace OpenFSE.Input;

/// <summary>Drives Avalonia keyboard focus from gamepad input: D-pad/stick moves
/// focus through the tab order, A activates (synthesized Enter), B invokes a back
/// action. Deterministic and AOT-safe.</summary>
public sealed class GamepadNavigation : IDisposable
{
    private readonly GamepadService _gamepad;
    private readonly Window _window;
    private readonly Action _back;
    private readonly bool _nintendoLayout;

    /// <param name="nintendoLayout">Nintendo labels are swapped relative to Xbox at
    /// the same physical positions: the button labeled A (east, XInput B) confirms
    /// and labeled B (south, XInput A) goes back.</param>
    public GamepadNavigation(GamepadService gamepad, Window window, Action back, bool nintendoLayout = false)
    {
        _gamepad = gamepad;
        _window = window;
        _back = back;
        _nintendoLayout = nintendoLayout;
        _gamepad.ButtonPressed += OnButtons;
    }

    private void OnButtons(GamepadButtons buttons)
    {
        if (!_window.IsVisible)
        {
            return;
        }

        var confirm = _nintendoLayout ? GamepadButtons.B : GamepadButtons.A;
        var back = _nintendoLayout ? GamepadButtons.A : GamepadButtons.B;

        if (buttons.HasFlag(back))
        {
            _back();
            return;
        }
        if (buttons.HasFlag(confirm) || buttons.HasFlag(GamepadButtons.Start))
        {
            Activate(GetFocused());
            return;
        }

        if (buttons.HasFlag(GamepadButtons.DPadDown) || buttons.HasFlag(GamepadButtons.DPadRight))
        {
            MoveFocus(NavigationDirection.Next);
        }
        else if (buttons.HasFlag(GamepadButtons.DPadUp) || buttons.HasFlag(GamepadButtons.DPadLeft))
        {
            MoveFocus(NavigationDirection.Previous);
        }
    }

    private InputElement? GetFocused()
        => TopLevel.GetTopLevel(_window)?.FocusManager?.GetFocusedElement() as InputElement;

    private void MoveFocus(NavigationDirection direction)
    {
        var current = GetFocused();
        if (current is null || current is Window || !IsInWindow(current))
        {
            FocusFirst();
            return;
        }
        var next = KeyboardNavigationHandler.GetNext(current, direction);
        (next as InputElement)?.Focus(NavigationMethod.Directional);
        if (next is null)
        {
            FocusFirst();
        }
    }

    private bool IsInWindow(InputElement element)
        => (element as Avalonia.Visual)?.GetVisualRoot() == _window;

    private void FocusFirst()
    {
        foreach (var descendant in _window.GetVisualDescendants())
        {
            if (descendant is InputElement { Focusable: true, IsEffectivelyEnabled: true, IsEffectivelyVisible: true } input)
            {
                input.Focus(NavigationMethod.Directional);
                return;
            }
        }
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

    public void Dispose() => _gamepad.ButtonPressed -= OnButtons;
}
