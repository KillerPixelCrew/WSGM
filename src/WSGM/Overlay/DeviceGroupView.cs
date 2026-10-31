using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Layout;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>One drawn <see cref="DeviceGroup" />: its frame, its rows by key and the description it was built from.</summary>
/// <remarks>
///     <see cref="DeviceGroupRenderer" /> keeps the view while a new description has the same layout and refreshes
///     its rows in place; any layout change replaces the view, and only this one.
/// </remarks>
internal sealed class DeviceGroupView : Border
{
    /// <summary>Creates the frame; the renderer fills it.</summary>
    /// <param name="group">The description the view is built from.</param>
    /// <param name="pinned">Whether the view is a Quick Access copy, whose keys carry the <c>pin:</c> scope.</param>
    internal DeviceGroupView(DeviceGroup group, bool pinned)
    {
        Group = group;
        Pinned = pinned;
        Tag = (pinned ? QuickAccessPins.TagPrefix : string.Empty) + group.Id;
        VerticalAlignment = VerticalAlignment.Top;
    }

    /// <inheritdoc />
    /// <remarks>Styled as a Border, so a card takes the shared <c>Border.device-group</c> frame.</remarks>
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>The description the view currently shows.</summary>
    internal DeviceGroup Group { get; private set; }

    /// <summary>Whether the view is a Quick Access copy.</summary>
    internal bool Pinned { get; }

    /// <summary>The row controls by unscoped row key.</summary>
    internal Dictionary<string, Control> Rows { get; } = [];

    /// <summary>The nested group views, in description order, after the rows in <see cref="Body" />.</summary>
    internal List<DeviceGroupView> Groups { get; } = [];

    /// <summary>The panel holding the rows and then the nested groups.</summary>
    internal Panel? Body { get; set; }

    /// <summary>The group's fold, or null for a group drawn without one.</summary>
    internal CollapsibleSection? Fold { get; set; }

    /// <summary>Shows a description of the same layout by refreshing the rows in place; the renderer matches nested groups.</summary>
    /// <param name="group">The description as published now.</param>
    internal void Refresh(DeviceGroup group)
    {
        Group = group;
        if (Fold is not null)
        {
            Fold.Summary = group.Summary;
        }

        foreach (var row in group.Rows)
        {
            switch (Rows.GetValueOrDefault(row.Key))
            {
                case DeviceCapabilityControl control:
                    control.Refresh(row);
                    break;
                case ManualTdpModeView manualTdp:
                    // The mode, its availability and its game override arrive with the device's change.
                    manualTdp.Refresh();
                    break;
            }
        }
    }
}
