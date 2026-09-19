using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace WSGM.OverlayMockup;

internal sealed partial class MockupWindow
{
    private PreviewGamepad? _gamepad;

    private void CycleDestination(int direction)
    {
        if (_surface.IsVisible || _keyboard.IsVisible || _closing)
        {
            return;
        }

        var index = Array.IndexOf(_destinations, _destination);
        Navigate(_destinations[(index + direction + _destinations.Length) % _destinations.Length]);
        Dispatcher.UIThread.Post(() =>
        {
            var first = _page.GetVisualDescendants().OfType<Control>().FirstOrDefault(IsNavigationTarget);
            first?.Focus(NavigationMethod.Directional);
        });
    }

    private void ControllerKey(Key key)
    {
        if (key == Key.Escape)
        {
            OnKeyDown(this, new KeyEventArgs { Key = Key.Escape });
            return;
        }

        var focused = FocusManager?.GetFocusedElement() as Control;
        if (key == Key.Enter)
        {
            switch (focused)
            {
                case ToggleSwitch toggle:
                    toggle.IsChecked = toggle.IsChecked != true;
                    break;
                case ToggleButton toggle:
                    toggle.IsChecked = toggle.IsChecked != true;
                    break;
                case Button button:
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    break;
                case ComboBox combo:
                    combo.SelectedIndex = (combo.SelectedIndex + 1) % Math.Max(1, combo.ItemCount);
                    break;
            }

            return;
        }

        if (key is Key.Left or Key.Right)
        {
            var delta = key == Key.Left ? -1 : 1;
            if (focused is Slider slider)
            {
                slider.Value = Math.Clamp(slider.Value + delta * slider.TickFrequency, slider.Minimum, slider.Maximum);
                return;
            }

            if (focused is ComboBox combo)
            {
                combo.SelectedIndex = Math.Clamp(combo.SelectedIndex + delta, 0, Math.Max(0, combo.ItemCount - 1));
                return;
            }
        }

        MoveFocus(key, focused);
    }

    private static bool IsNavigationTarget(Control control)
    {
        return control.Focusable && control.IsEffectivelyVisible && control.IsEffectivelyEnabled
               && control.Bounds.Width > 0 && control.Bounds.Height > 0
               && !control.GetVisualAncestors().Any(x => x is Slider or ComboBox)
               && control is Button or Slider or ComboBox or ToggleSwitch or TextBox;
    }

    private void MoveFocus(Key key, Control? focused)
    {
        Control scope = _keyboard.IsVisible ? _keyboard : _surface.IsVisible ? _surface : _shell;
        var candidates = scope.GetVisualDescendants().OfType<Control>().Where(IsNavigationTarget).ToArray();
        if (focused is null || !candidates.Contains(focused))
        {
            candidates.FirstOrDefault()?.Focus(NavigationMethod.Directional);
            return;
        }

        var origin = focused.TranslatePoint(new Point(focused.Bounds.Width / 2, focused.Bounds.Height / 2), scope);
        if (origin is null)
        {
            return;
        }

        Control? best = null;
        var bestScore = double.MaxValue;
        foreach (var candidate in candidates)
        {
            if (candidate == focused)
            {
                continue;
            }

            var point = candidate.TranslatePoint(new Point(candidate.Bounds.Width / 2, candidate.Bounds.Height / 2),
                scope);
            if (point is null)
            {
                continue;
            }

            var dx = point.Value.X - origin.Value.X;
            var dy = point.Value.Y - origin.Value.Y;
            var forward = key switch { Key.Left => -dx, Key.Right => dx, Key.Up => -dy, Key.Down => dy, _ => -1 };
            var sideways = Math.Abs(key is Key.Left or Key.Right ? dy : dx);
            if (forward <= 1)
            {
                continue;
            }

            var score = forward + sideways * 3;
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (best is not null)
        {
            best.Focus(NavigationMethod.Directional);
            best.BringIntoView();
        }
    }
}
