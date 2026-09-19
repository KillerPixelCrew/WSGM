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
    private readonly Dictionary<string, Button> _sectionButtons = [];
    private readonly Stack<(Control Content, InputElement? Focus)> _sectionHistory = new();
    private readonly Dictionary<string, string> _sectionSelections = [];
    private PreviewGamepad? _gamepad;
    private ContentControl? _sectionDetail;
    private string _selectedSection = "";
    private bool _selectingSection;

    private void SelectSection(string title, Action open)
    {
        _sectionHistory.Clear();
        _selectedSection = title;
        _sectionSelections[_destination] = title;
        foreach (var (name, button) in _sectionButtons)
        {
            button.Classes.Set("selected", name == title);
        }

        _selectingSection = true;
        try
        {
            open();
        }
        finally
        {
            _selectingSection = false;
        }
    }

    private void ShowSectionDetail(string title, Control content)
    {
        if (_sectionDetail is null)
        {
            return;
        }

        if (_selectingSection)
        {
            _sectionDetail.Content = Stack(SectionTitle(title, _integration || _destination != "Device"
                ? "Preview controls · Changes stay in memory"
                : "Device integration is off · Windows controls remain available"), content);
            return;
        }

        if (_sectionDetail.Content is Control previous)
        {
            _sectionHistory.Push((previous, FocusManager?.GetFocusedElement() as InputElement));
        }

        var back = ActionButton("← Back", BackFromSectionDetail);
        _sectionDetail.Content = Stack(back, content);
        Dispatcher.UIThread.Post(() => back.Focus(NavigationMethod.Directional));
    }

    private void BackFromSectionDetail()
    {
        if (_sectionHistory.TryPop(out var previous) && _sectionDetail is not null)
        {
            _sectionDetail.Content = previous.Content;
            Dispatcher.UIThread.Post(() => previous.Focus?.Focus(NavigationMethod.Directional));
        }
    }

    private bool FocusSectionMenu()
    {
        if (_sectionDetail is null
            || FocusManager?.GetFocusedElement() is not Control focused
            || !focused.GetVisualAncestors().Contains(_sectionDetail))
        {
            return false;
        }

        _sectionButtons[_selectedSection].Focus(NavigationMethod.Directional);
        return true;
    }

    private void DismissDetail()
    {
        if (_surface.IsVisible)
        {
            DismissSurface();
        }
        else
        {
            BackFromSectionDetail();
        }
    }

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
        if (focused is Button section && _sectionButtons.ContainsValue(section))
        {
            if (key is Key.Up or Key.Down)
            {
                var buttons = _sectionButtons.Values.ToArray();
                var index = Array.IndexOf(buttons, section);
                var next = buttons[Math.Clamp(index + (key == Key.Down ? 1 : -1), 0, buttons.Length - 1)];
                next.Focus(NavigationMethod.Directional);
                next.BringIntoView();
                return;
            }

            if (key == Key.Right)
            {
                section.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.Post(() =>
                {
                    var first = _sectionDetail?.GetVisualDescendants().OfType<Control>()
                        .FirstOrDefault(IsNavigationTarget);
                    first?.Focus(NavigationMethod.Directional);
                    first?.BringIntoView();
                }, DispatcherPriority.Loaded);
                return;
            }
        }

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
