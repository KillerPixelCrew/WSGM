using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentAvalonia.UI.Controls;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Overlay;

/// <summary>A setting container with one interactive footer and no competing header focus stop.</summary>
internal sealed class DeviceSettingRow : FASettingsExpanderItem
{
    private readonly Action<CapabilityValue?> _refresh;

    /// <summary>Creates a noninteractive label container around one actual value editor.</summary>
    /// <param name="key">Stable row/editor focus key.</param>
    /// <param name="title">Visible and accessible editor name.</param>
    /// <param name="description">Supporting and accessible help text.</param>
    /// <param name="editor">Control mounted as the sole interactive footer.</param>
    /// <param name="refresh">Readback callback invoked under Refreshing; it must not submit a user edit.</param>
    internal DeviceSettingRow(string key, string title, string description, Control editor,
        Action<CapabilityValue?> refresh)
    {
        Content = title;
        Description = description;
        Footer = editor;
        Tag = key;
        Focusable = false;
        IsClickEnabled = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        editor.Tag = key;
        AutomationProperties.SetName(editor, title);
        AutomationProperties.SetHelpText(editor, description);
        _refresh = refresh;
        Classes.Add("device-setting");
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(FASettingsExpanderItem);

    /// <summary>Whether published state is being synchronized; change handlers must suppress writes while true.</summary>
    internal bool Refreshing { get; private set; }
    /// <summary>Mounted interactive footer; ownership remains with this row.</summary>
    internal Control Editor => (Control)Footer!;

    /// <summary>Updates availability and description, preserving a focused editor or open dropdown draft.</summary>
    /// <param name="value">Published value supplied to the synchronization callback when not editing.</param>
    /// <param name="enabled">Whether the editor currently accepts input.</param>
    /// <param name="description">Current availability or value explanation.</param>
    internal void Refresh(CapabilityValue? value, bool enabled, string description)
    {
        Description = description;
        Refreshing = true;
        try
        {
            Editor.IsEnabled = enabled;
            if (Editor.IsKeyboardFocusWithin || Editor is ComboBox { IsDropDownOpen: true })
            {
                return;
            }

            _refresh(value);
        }
        finally
        {
            Refreshing = false;
        }
    }
}
