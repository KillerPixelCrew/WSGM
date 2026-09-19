using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace WSGM.OverlayMockup;

internal sealed partial class MockupWindow
{
    private void ShowPowerMenu()
    {
        var cancel = ActionButton("Keep playing", DismissSurface, true);
        cancel.HorizontalAlignment = HorizontalAlignment.Stretch;
        cancel.HorizontalContentAlignment = HorizontalAlignment.Center;
        var heading = Text("Time for a pause.", 34, weight: FontWeight.SemiBold);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        var subtitle = Text("Where would you like to go from here?", muted: true);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        var actions = new[]
        {
            ("Sleep", "Keep everything ready.", "☾"),
            ("Hibernate", "Save your session for later.", "◌"),
            ("Game mode", "Go straight to Big Picture.", "▷"),
            ("Restart", "A fresh start.", "↻"),
            ("Sign out", "Finish this Windows session.", "↗"),
            ("Shut down", "All done for now.", "⏻")
        };
        var tiles = actions.Select(action => Tile(action.Item1, action.Item2, () =>
        {
            DismissSurface();
            Notice($"{action.Item1} selected · demonstration only, no system action taken");
        }, action.Item3)).ToArray();
        var close = ActionButton("×", DismissSurface);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.FontSize = 24;
        NameControl(close, "Close power menu");
        var content = Stack(close, heading, subtitle, Flow(250, tiles), cancel);
        var menu = Card(new ScrollViewer { Content = content });
        menu.Width = 640;
        menu.MaxHeight = Math.Max(400, Bounds.Height - 72);
        menu.HorizontalAlignment = HorizontalAlignment.Center;
        menu.VerticalAlignment = VerticalAlignment.Center;
        menu.Background = Brush("#F7232323");
        OpenSurface(menu, cancel);
        _shell.IsVisible = false;
    }

    private void ShowStatus(string title, bool inSection = false)
    {
        Control content = title switch
        {
            "Brightness" => Stack(
                Range("brightness", "Brightness", 68, 0, 100, "%"),
                Row("Night light", Toggle("night-light", "Night light"))),
            "Bluetooth" => Stack(
                Row("Bluetooth", Toggle("bluetooth", "Bluetooth", true)),
                Text("Headphones connected · sample connection", muted: true)),
            "Audio" => Stack(
                Range("volume", "Volume", 42, 0, 100, "%"),
                Row("Output", Picker("audio-output", "Audio output", "Speakers", "Speakers", "Headphones", "HDMI")),
                Row("Mute", Toggle("mute", "Mute"))),
            "Storage" => Stack(
                SectionTitle("Game library", "microSD · 512 GB · sample drive"),
                new ProgressBar { Value = 62, Height = 8, CornerRadius = new CornerRadius(4) },
                Text("318 GB used · 194 GB available", muted: true),
                ActionButton("Safely remove", () => Notice("Eject requested · preview only, no drive touched"))),
            _ => Stack(
                Row("Wi-Fi", Toggle("wifi", "Wi-Fi", true)),
                Row("Network",
                    Picker("network", "Wi-Fi network", "Home network", "Home network", "Guest network",
                        "Phone hotspot")),
                Row("Bluetooth", Toggle("bluetooth", "Bluetooth", true)),
                Text("Headphones connected · sample connection", muted: true))
        };
        ShowDetail(title, content, inSection);
    }

    private void ShowKeyboard()
    {
        if (_surface.IsVisible)
        {
            DismissSurface();
        }

        _returnFocus = FocusManager?.GetFocusedElement() as InputElement;
        if (_keyboard.Child is null)
        {
            var done = ActionButton("Done", HideKeyboard, true);
            var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            heading.Children.Add(SectionTitle("A word or two.", "Keyboard preview · text stays here"));
            Grid.SetColumn(done, 1);
            heading.Children.Add(done);
            var keys = new StackPanel { Spacing = 8 };
            foreach (var row in new[] { "1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm" })
            {
                var line = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center
                };
                foreach (var letter in row)
                {
                    var key = ActionButton(letter.ToString(), () => InsertText(letter.ToString()));
                    key.Width = 64;
                    key.Height = 44;
                    key.HorizontalContentAlignment = HorizontalAlignment.Center;
                    line.Children.Add(key);
                }

                keys.Children.Add(line);
            }

            var space = ActionButton("Space", () => InsertText(" "));
            space.Width = 340;
            space.HorizontalContentAlignment = HorizontalAlignment.Center;
            var backspace = ActionButton("⌫", () =>
            {
                var text = _typing.Text ?? "";
                var position = Math.Clamp(_typing.CaretIndex, 0, text.Length);
                if (position > 0)
                {
                    _typing.Text = text.Remove(position - 1, 1);
                    _typing.CaretIndex = position - 1;
                }
            });
            NameControl(backspace, "Backspace");
            var last = new StackPanel
                { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            last.Children.Add(space);
            last.Children.Add(backspace);
            keys.Children.Add(last);
            _keyboard.Child = Stack(heading, _typing, keys);
        }

        _shell.IsEnabled = false;
        _keyboard.IsVisible = true;
        KeyboardNavigation.SetTabNavigation(_keyboard, KeyboardNavigationMode.Cycle);
        Dispatcher.UIThread.Post(() => _typing.Focus());
    }

    private void InsertText(string value)
    {
        var text = _typing.Text ?? "";
        var start = Math.Clamp(Math.Min(_typing.SelectionStart, _typing.SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(_typing.SelectionStart, _typing.SelectionEnd), start, text.Length);
        _typing.Text = text.Remove(start, end - start).Insert(start, value);
        _typing.CaretIndex = start + value.Length;
        _typing.SelectionStart = _typing.CaretIndex;
        _typing.SelectionEnd = _typing.CaretIndex;
    }

    private void HideKeyboard()
    {
        if (!_keyboard.IsVisible)
        {
            return;
        }

        _keyboard.IsVisible = false;
        _shell.IsEnabled = true;
        _returnFocus?.Focus();
    }
}
