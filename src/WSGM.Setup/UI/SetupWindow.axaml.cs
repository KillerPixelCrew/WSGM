using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace WSGM.Setup.UI;

/// <summary>Small converters the setup window binds with.</summary>
internal static class Converters
{
    public static readonly IValueConverter BoldWhenTrue =
        new FuncValueConverter<bool, FontWeight>(value => value ? FontWeight.SemiBold : FontWeight.Normal);
}

/// <summary>The setup window, driven by keyboard, touch and gamepad alike.</summary>
internal sealed partial class SetupWindow : Window
{
    private readonly DispatcherTimer _gamepad = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private ushort _lastButtons;

    public SetupWindow()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _gamepad.Tick += (_, _) => PollGamepad();
        Opened += (_, _) =>
        {
            if (DataContext is SetupViewModel model)
            {
                model.CloseRequested += Close;
            }

            _gamepad.Start();
        };
        Closed += (_, _) => _gamepad.Stop();
    }

    // Arrows move focus in reading order, Escape is B, Enter on a focused control is A.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down or Key.Right:
                e.Handled = MoveFocus(NavigationDirection.Next);
                break;
            case Key.Up or Key.Left:
                e.Handled = MoveFocus(NavigationDirection.Previous);
                break;
            case Key.Escape:
                (DataContext as SetupViewModel)?.BackCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private bool MoveFocus(NavigationDirection direction)
    {
        // Avalonia's own Tab handling walks the focus order; a synthetic Tab reuses it for arrows and the
        // D-pad instead of reimplementing it.
        if (FocusManager?.GetFocusedElement() is not Interactive focused)
        {
            this.FindControl<Button>("PrimaryButton")?.Focus(NavigationMethod.Directional);
            return true;
        }

        focused.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = KeyDownEvent,
            Key = Key.Tab,
            KeyModifiers = direction is NavigationDirection.Previous ? KeyModifiers.Shift : KeyModifiers.None,
            Source = focused
        });
        return true;
    }

    private void ActivateFocused()
    {
        switch (FocusManager?.GetFocusedElement())
        {
            case RadioButton radio:
                radio.IsChecked = true;
                break;
            case ToggleButton toggle:
                toggle.IsChecked = toggle.IsChecked != true;
                break;
            case Button button:
                button.Command?.Execute(button.CommandParameter);
                break;
            default:
                (DataContext as SetupViewModel)?.PrimaryCommand.Execute(null);
                break;
        }
    }

    private void PollGamepad()
    {
        if (!XInput.TryRead(out var buttons))
        {
            return;
        }

        var pressed = (ushort)(buttons & ~_lastButtons);
        _lastButtons = buttons;
        if ((pressed & (XInput.DpadDown | XInput.DpadRight)) != 0)
        {
            MoveFocus(NavigationDirection.Next);
        }
        else if ((pressed & (XInput.DpadUp | XInput.DpadLeft)) != 0)
        {
            MoveFocus(NavigationDirection.Previous);
        }
        else if ((pressed & XInput.A) != 0)
        {
            ActivateFocused();
        }
        else if ((pressed & XInput.B) != 0)
        {
            (DataContext as SetupViewModel)?.BackCommand.Execute(null);
        }
    }
}
