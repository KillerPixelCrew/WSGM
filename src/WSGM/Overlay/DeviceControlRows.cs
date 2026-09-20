using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Overlay;

/// <summary>
///     The non-slider device capability controls — toggle, dropdown, text editor — that a capability's
///     value kind asks for, so a boolean is a switch and a choice is a dropdown instead of a
///     value-cycling button. Each is a themed tile whose single interactive control is the focus
///     target; <c>GamepadNavigation</c> already routes A/Left/Right to a focused ToggleSwitch,
///     ComboBox and edit row, so pad, touch and keyboard drive them with no extra plumbing.
/// </summary>
internal static class DeviceControlRows
{
    /// <summary>
    ///     Builds the shared tile skeleton: heading, optional caption, and a right-aligned
    ///     interactive control on the header line.
    /// </summary>
    private static DeviceSettingRow Tile(string key, string title, string description, Control control,
        Action<CapabilityValue?> refresh)
    {
        return new DeviceSettingRow(key, title, description, control, refresh);
    }

    /// <summary>A boolean capability as a switch.</summary>
    /// <param name="key">Stable focus key.</param>
    /// <param name="title">Row heading.</param>
    /// <param name="description">Supporting line.</param>
    /// <param name="isOn">Current state.</param>
    /// <param name="enabled">Whether input is accepted.</param>
    /// <param name="onChanged">Invoked with the new state.</param>
    /// <returns>The tile.</returns>
    internal static DeviceSettingRow Toggle(
        string key,
        string title,
        string description,
        bool isOn,
        bool enabled,
        Action<bool> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        var toggle = new ToggleSwitch
        {
            IsChecked = isOn,
            IsEnabled = enabled,
            Focusable = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            OffContent = null,
            OnContent = null
        };
        DeviceSettingRow? row = null;
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (row?.Refreshing is false)
            {
                onChanged(toggle.IsChecked ?? false);
            }
        };
        row = Tile(key, title, description, toggle, value => toggle.IsChecked = value?.BooleanValue ?? false);
        return row;
    }

    /// <summary>A choice capability as a dropdown.</summary>
    /// <param name="key">Stable focus key.</param>
    /// <param name="title">Row heading.</param>
    /// <param name="description">Supporting line.</param>
    /// <param name="choices">Legal values with their display labels.</param>
    /// <param name="selected">Currently selected value, or null.</param>
    /// <param name="enabled">Whether input is accepted.</param>
    /// <param name="onChanged">Invoked with the chosen value.</param>
    /// <returns>The tile.</returns>
    internal static DeviceSettingRow Choice(
        string key,
        string title,
        string description,
        IReadOnlyList<CapabilityChoice> choices,
        string? selected,
        bool enabled,
        Action<string> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        var items = choices
            .Select(choice => new ChoiceItem(choice.Value, LabelFor(choice)))
            .ToList();
        var combo = new ComboBox
        {
            ItemsSource = items,
            DisplayMemberBinding = CompiledBinding.Create((ChoiceItem item) => item.Label),
            SelectedIndex = items.FindIndex(item =>
                string.Equals(item.Value, selected, StringComparison.Ordinal)),
            IsEnabled = enabled,
            Focusable = true,
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DeviceSettingRow? row = null;
        var committed = selected;
        var open = false;
        combo.DropDownOpened += (_, _) => open = true;
        combo.DropDownClosed += (_, _) =>
        {
            open = false;
            CommitChoice();
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (!open && !combo.IsDropDownOpen)
            {
                CommitChoice();
            }
        };
        row = Tile(key, title, description, combo, value =>
        {
            committed = value?.ChoiceValue;
            combo.SelectedIndex = items.FindIndex(item => item.Value == committed);
        });
        return row;

        void CommitChoice()
        {
            if (row?.Refreshing is not false || !combo.IsEnabled || combo.SelectedItem is not ChoiceItem item
                || string.Equals(item.Value, committed, StringComparison.Ordinal))
            {
                return;
            }

            committed = item.Value;
            onChanged(item.Value);
        }
    }

    /// <summary>A text capability edited through the shared controller keyboard.</summary>
    /// <param name="key">Stable focus key.</param>
    /// <param name="title">Row heading.</param>
    /// <param name="text">Current text.</param>
    /// <param name="maximumLength">Maximum accepted length, or null.</param>
    /// <param name="enabled">Whether input is accepted.</param>
    /// <param name="onCommit">Invoked with the committed text.</param>
    /// <returns>The tile.</returns>
    internal static DeviceSettingRow Text(
        string key,
        string title,
        string? text,
        int? maximumLength,
        bool enabled,
        Action<string> onCommit)
    {
        ArgumentNullException.ThrowIfNull(onCommit);
        var draft = text ?? string.Empty;
        var editor = new Button { Content = string.IsNullOrEmpty(draft) ? "Edit" : draft, IsEnabled = enabled };
        var row = Tile(key, title, string.Empty, editor, value =>
        {
            draft = value?.TextValue ?? string.Empty;
            editor.Content = string.IsNullOrEmpty(draft) ? "Edit" : draft;
        });
        editor.Click += (_, _) =>
        {
            if (!KeyboardService.Request(title, draft, maximumLength ?? 4096, value =>
                {
                    draft = value;
                    editor.Content = value;
                    onCommit(value);
                }))
            {
                row.Description = "Keyboard unavailable. Reopen the overlay to retry.";
            }
        };
        return row;
    }

    private static string LabelFor(CapabilityChoice choice)
    {
        return CapabilityDisplayLabels.For(choice.Display, choice.Value);
    }

    private sealed record ChoiceItem(string Value, string Label);
}
