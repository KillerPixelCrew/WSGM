using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class GameToolsView
{
    private readonly HashSet<string> _expandedSettings = new(StringComparer.Ordinal);
    private int _dllsShown = 24;

    private Control NumericSetting(string key, GameToolsSetting setting, string value, bool enabled, Action<string> commit)
    {
        var number = new NumericUpDown
        {
            MinWidth = 135, Increment = (decimal)setting.Step,
            Minimum = setting.Minimum is { } min ? (decimal)min : decimal.MinValue,
            Maximum = setting.Maximum is { } max ? (decimal)max : decimal.MaxValue,
            FormatString = "0.###", IsEnabled = enabled,
            Value = decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
            Tag = key + ".number"
        };
        var set = Command(key + ".apply", "Set", enabled
            ? () =>
            {
                if (number.Value is { } current)
                {
                    commit(current.ToString(CultureInfo.InvariantCulture));
                }
            } : null);
        number.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter && enabled && number.Value is { } current)
            {
                args.Handled = true;
                commit(current.ToString(CultureInfo.InvariantCulture));
            }
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(number);
        row.Children.Add(set);
        return row;
    }

    private void EditToolHotkey(GameToolsSetting setting, string value, Action<string> accept)
    {
        if (TopLevel.GetTopLevel(this) is not OverlayWindow window)
        {
            return;
        }
        GameToolHotkeys.TryParse(value, setting.HotkeyFormat, out var keys);
        var main = keys.FirstOrDefault(key => !HotkeyKeys.IsModifier(key));
        var content = new StackPanel { Spacing = 12, Tag = "tool.hotkey" };
        content.Children.Add(Caption("Choose a key with the controller, or capture a keyboard combination. Esc or B cancels."));
        var picker = Choice([("0", "Not bound"), .. setting.KeyChoices.Select(key => (key.Value, key.Label))],
            main.ToString(CultureInfo.InvariantCulture), true, _ => { });
        content.Children.Add(picker);
        var modifiers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var switches = new Dictionary<int, ToggleSwitch>();
        foreach (var (code, label) in new[] { (HotkeyKeys.Control, "Ctrl"), (HotkeyKeys.Shift, "Shift"), (HotkeyKeys.Alt, "Alt") })
        {
            var toggle = new ToggleSwitch { IsChecked = keys.Contains(code), Content = label };
            switches[code] = toggle;
            modifiers.Children.Add(toggle);
        }
        content.Children.Add(modifiers);
        var holder = new ContentControl();
        var status = Caption("");
        content.Children.Add(Command("tool.hotkey.capture", "Capture keyboard", () =>
        {
            var capture = new HotkeyCapture();
            capture.Captured += captured =>
            {
                if (captured.Contains(HotkeyKeys.Windows) || captured.Count(key => !HotkeyKeys.IsModifier(key)) != 1)
                {
                    status.Text = "Use one key with Ctrl, Shift or Alt. Capture again or use the key picker.";
                    return;
                }
                accept(GameToolHotkeys.Format(captured, setting.HotkeyFormat));
                window.Surfaces.CloseActive();
            };
            holder.Content = capture;
            capture.Focus();
        }));
        content.Children.Add(holder);
        content.Children.Add(status);
        content.Children.Add(CommandStrip(
            Command("tool.hotkey.cancel", "Cancel", () => window.Surfaces.CloseActive()),
            Command("tool.hotkey.accept", "Set key", () =>
            {
                // Choice stores tagged values on its items; use its selected index for stable controller selection.
                var index = Math.Max(0, picker.SelectedIndex);
                var code = index == 0 ? 0 : int.Parse(setting.KeyChoices[index - 1].Value, CultureInfo.InvariantCulture);
                var chord = switches.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();
                if (code > 0)
                {
                    chord.Add(code);
                }
                accept(GameToolHotkeys.Format(chord, setting.HotkeyFormat));
                window.Surfaces.CloseActive();
            })));
        window.Surfaces.ShowConfirm(content, "Set " + setting.Label, picker);
    }

    private Control LibraryIdentities(GameToolsState state)
    {
        var rows = new StackPanel { Spacing = 8 };
        if (state.Libraries.Count == 0)
        {
            rows.Children.Add(Caption(state.Loading ? "Reading DLL identities…" : "No DLLs were found beside the selected program."));
        }
        foreach (var file in state.Libraries.Take(_dllsShown))
        {
            rows.Children.Add(Caption(file.RelativePath + " · " + file.Name + " · " + PeImage.Label(file.Architecture)
                + (file.Pipelines.Length > 0 ? " · " + string.Join(", ", file.Pipelines) : " · pipeline unknown")));
        }
        if (_dllsShown < state.Libraries.Count)
        {
            rows.Children.Add(Command("game.dlls.more", "Load more DLLs", () => { _dllsShown += 24; CurrentLevel?.Invoke(); }));
        }
        return CollapsibleSection.Persisted("DLLs in this folder", rows,
            _expandedSettings, _appId + ":dlls", "game.dlls");
    }
}
