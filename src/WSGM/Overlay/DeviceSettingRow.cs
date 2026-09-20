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

    protected override Type StyleKeyOverride => typeof(FASettingsExpanderItem);

    internal bool Refreshing { get; private set; }
    internal Control Editor => (Control)Footer!;

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
