using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private void RefreshDeviceSectionPins(DeviceOverlaySnapshot snapshot)
    {
        // Preserve the focused pin button when only telemetry changes. Graphics groups follow the graphics
        // packages, so they are offered here with device integration off too.
        GraphicsPinSection[] graphics = [.. GraphicsSectionPins.Build(_graphicsSource?.Snapshot())];
        var ids = _controlPinFactories.Keys
            .Where(id => (id == "section.system.power-profile" && _powerSchemeSelection is { Offered: true })
                         || (snapshot.Visible && id.StartsWith("section.device.", StringComparison.Ordinal)))
            .Concat(_performanceSource?.Snapshot().Visible is true ? ["section.performance"] : [])
            .Concat(DevicePinSections(snapshot).Where(section => section.Capabilities.Count > 0
                                                                 || (section.Owned == DeviceOverlaySection
                                                                         .ControllerAndMotion &&
                                                                     snapshot.GlyphSelection is not null))
                .Select(section => section.Id))
            .Concat(graphics.Select(section => section.Id)).Distinct().ToArray();
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
                : graphics.FirstOrDefault(section => section.Id == id) is { } pin ? pin.PinTitle
                : DevicePinSections(snapshot).First(section => section.Id == id).Title;
            DeviceSectionPinsHost.Children.Add(CreateSectionHeader(id, title));
        }
    }

    private Control? RenderControllerPage(DeviceOverlaySnapshot snapshot, string? focusedKey, string pinId)
    {
        var glyphs = CreateSection(pinId, "Button glyphs");
        var output = new StackPanel { Spacing = 8 };
        DeviceCapabilityList.Children.Add(WrapDeviceSection(glyphs));
        DeviceCapabilityList.Children.Add(CreateFold(pinId + ".output", "Controller output", output));

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
            DeviceCapabilityList.Children.Add(WrapDeviceSection(content));
        }

        return restore;
    }
}
