using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private void RefreshDeviceSectionPins(DeviceOverlaySnapshot snapshot)
    {
        // Preserve the focused pin button when only telemetry changes.
        var ids = _controlPinFactories.Keys
            .Where(id => id == "section.system.power-profile"
                         || (snapshot.Visible && id.StartsWith("section.device.", StringComparison.Ordinal)))
            .Concat(_performanceSource?.Snapshot().Visible is true ? ["section.performance"] : [])
            .Concat(DevicePinSections(snapshot).Where(section => section.Capabilities.Count > 0
                                                                 || (section.Owned == DeviceOverlaySection
                                                                         .ControllerAndMotion &&
                                                                     snapshot.GlyphSelection is not null))
                .Select(section => section.Id)).Distinct().ToArray();
        if (DeviceSectionPinsHost.Children.OfType<SectionPinHeader>().Select(header => header.SectionId)
            .SequenceEqual(ids))
        {
            return;
        }

        DeviceSectionPinsHost.Children.Clear();
        foreach (var id in ids)
        {
            var title = _controlPinFactories.TryGetValue(id, out var host) ? host.Title
                : id == "section.performance" ? "Performance"
                : DevicePinSections(snapshot).First(section => section.Id == id).Title;
            DeviceSectionPinsHost.Children.Add(CreateSectionHeader(id, title));
        }
    }

    private DescriptorStatusRow? RenderControllerPage(DeviceOverlaySnapshot snapshot, string? focusedKey, string pinId)
    {
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
        var glyphs = CreateSection(pinId, "Button glyphs");
        var output = new StackPanel { Spacing = 8 };
        output.Children.Add(new TextBlock { Text = "Controller output", Classes = { "setting-title" }, FontSize = 18 });
        columns.Children.Add(new Border
        {
            Classes = { "device-group", "device-overview-card" }, Child = glyphs,
            VerticalAlignment = VerticalAlignment.Top
        });
        var outputCard = new Border
        {
            Classes = { "device-group", "device-overview-card" }, Child = output,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(outputCard, 1);
        columns.Children.Add(outputCard);
        DeviceCapabilityList.Children.Add(columns);

        var restore = RenderOwnedDeviceRows(snapshot with { Controller = null },
            DeviceOverlaySection.ControllerAndMotion, focusedKey, glyphs);
        restore = RenderOwnedDeviceRows(snapshot with { GlyphSelection = null, GlyphPreview = null },
            DeviceOverlaySection.ControllerAndMotion, focusedKey, output) ?? restore;
        if (snapshot.Controller is null)
        {
            output.Children.Add(new TextBlock
            {
                Text = snapshot.Visible
                    ? "Controller output is unavailable for this device."
                    : "Device integration is off. Enable it in WSGM Settings to configure controller output.",
                Classes = { "caption" },
                TextWrapping = TextWrapping.Wrap
            });
        }

        foreach (var section in DevicePinSections(snapshot).Where(section => section.Capabilities.Count > 0
                                                                             && (section.PluginSectionId is { } id
                                                                                 ? DeviceOverlaySectionPages
                                                                                     .SectionAbsorbedInto(snapshot,
                                                                                         id) == DeviceOverlaySection
                                                                                     .ControllerAndMotion
                                                                                 : section.Owned ==
                                                                                 DeviceOverlaySection
                                                                                     .ControllerAndMotion)))
        {
            var content = CreateSection(section.Id, section.Title);
            restore = AddDeviceSectionRows(snapshot, section with { Owned = null }, content, focusedKey) ?? restore;
            DeviceCapabilityList.Children.Add(new Border
                { Classes = { "device-group" }, Child = content, Margin = new Thickness(0, 4, 0, 0) });
        }

        return restore;
    }
}
