using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private void RestoreSectionHeaderFocus(string? key)
    {
        if (key?.StartsWith("section.", StringComparison.Ordinal) is not true) { return; }
        FocusSearch.First<Button>(this, button => Equals(button.Tag, key) && button.IsEffectivelyVisible)
            ?.Focus(Avalonia.Input.NavigationMethod.Directional);
    }

    private sealed record DevicePinSection(string Id, string Title, string? PluginSectionId,
        IReadOnlyList<DeviceOverlayCapability> Capabilities, DeviceOverlaySection? Owned = null);

    private bool PinnedSectionProvidersAvailable()
    {
        var snapshot = _deviceBridge?.Snapshot();
        var available = snapshot is null ? [] : DevicePinSections(snapshot).Select(section => section.Id).ToHashSet(StringComparer.Ordinal);
        return _pins.All(id => _controlPinFactories.ContainsKey(id)
            || id == "section.performance" && _performanceSource?.Snapshot().Visible is true
            || !id.StartsWith("section.device.", StringComparison.Ordinal) && id != "section.performance"
            || available.Contains(id));
    }

    private SectionPinHeader CreateSectionHeader(string id, string title, bool pinnedSurface = false)
    {
        var header = new SectionPinHeader(id, title, key => PinToggleRequested?.Invoke(key), pinnedSurface);
        header.Refresh(_pins.Contains(id));
        return header;
    }

    private StackPanel CreateSection(string id, string title, bool pinned = false)
    {
        var panel = new StackPanel
        {
            Spacing = 4,
            Tag = pinned ? PinTagPrefix + id : id,
            VerticalAlignment = VerticalAlignment.Top,
        };
        panel.Children.Add(CreateSectionHeader(id, title, pinned));
        return panel;
    }

    private static IEnumerable<DevicePinSection> DevicePinSections(DeviceOverlaySnapshot snapshot)
    {
        foreach (var section in snapshot.PluginSections)
        {
            var capabilities = snapshot.Visible
                ? DeviceOverlaySectionPages.CapabilitiesInPluginSection(snapshot, section.SectionId) : [];
            var owned = DeviceOverlaySectionPages.SectionAbsorbedInto(snapshot, section.SectionId);
            if (!snapshot.Visible && owned != DeviceOverlaySection.ControllerAndMotion) { owned = null; }
            bool power = owned == DeviceOverlaySection.PowerAndThermals;
            string prefix = "section.device.plugin." + section.SectionId;
            var lead = capabilities.Where(capability => capability.CategoryId is null
                || (power && capability.Role is CapabilityRole.PowerSustainedLimit or CapabilityRole.PowerSlowLimit))
                .OrderBy(capability => power ? capability.Role switch
                {
                    CapabilityRole.ScenarioMode => 0,
                    CapabilityRole.PowerSustainedLimit => 1,
                    CapabilityRole.PowerSlowLimit => 2,
                    _ => 3,
                } : capability.SortOrder).ToArray();
            if (lead.Length > 0)
            {
                yield return new(prefix + ".main", power ? "Manual power and display" : section.Title,
                    section.SectionId, lead);
            }
            var categories = power
                ? section.Categories.OrderBy(category => capabilities.Any(capability => capability.CategoryId == category.Id
                    && capability.Role is CapabilityRole.FanMode or CapabilityRole.FanCurve) ? 0 : 1)
                : section.Categories.AsEnumerable();
            foreach (var category in categories)
            {
                var rows = capabilities.Where(capability => capability.CategoryId == category.Id && !lead.Contains(capability)).ToArray();
                if (rows.Length > 0) { yield return new(prefix + ".category." + category.Id, category.Title, section.SectionId, rows); }
            }
            if (owned is { } host)
            {
                yield return new(prefix + ".configuration", power ? "Automatic control and saved profiles" : "Configuration",
                    section.SectionId, [], host);
            }
        }
        foreach (var section in DeviceOverlaySectionPages.Build(snapshot).Where(section => section.PluginSectionId is null
            && (snapshot.Visible || section.Section == DeviceOverlaySection.ControllerAndMotion)))
        {
            yield return new(DeviceOverlaySectionPages.FocusKey(section.Section).Replace("device.section.", "section.device.", StringComparison.Ordinal), section.Title, null,
                snapshot.Visible ? DeviceOverlaySectionPages.CapabilitiesIn(snapshot, section.Section) : [], section.Section);
        }
    }

    private DescriptorStatusRow? AddDeviceSectionRows(DeviceOverlaySnapshot snapshot, DevicePinSection section,
        Panel target, string? focusedKey = null, bool pinned = false)
    {
        DescriptorStatusRow? restoreFocus = null;
        foreach (var capability in section.Capabilities)
        {
            var presentation = capability with
            {
                Title = capability.Role == CapabilityRole.ScenarioMode ? "Firmware power mode" : capability.Title,
                Description = capability.Status == DescriptorStatus.Available
                    && (capability.Description.StartsWith("Observed ·", StringComparison.Ordinal)
                        || capability.Description.StartsWith("Verified ·", StringComparison.Ordinal))
                    ? string.Empty : capability.Description,
            };
            string key = (pinned ? PinTagPrefix : "") + DeviceRowKey(capability);
            Control row = TryCreateDeviceControl(presentation, key) ?? CreateDeviceCapabilityRow(presentation, key);
            ToolTip.SetTip(row, capability.Description);
            target.Children.Add(row);
            if (key == focusedKey && row is DescriptorStatusRow button) { restoreFocus = button; }
        }
        if (section.Owned is { } owned)
        {
            restoreFocus = RenderOwnedDeviceRows(snapshot, owned, focusedKey, target, includePreview: !pinned) ?? restoreFocus;
        }
        return restoreFocus;
    }

    private Control? CreatePinnedSection(string id)
    {
        if (_controlPinFactories.TryGetValue(id, out var host))
        {
            if (PinnedSectionsGrid.Children.FirstOrDefault(row => Equals(row.Tag, PinTagPrefix + id)) is { } existing) { return existing; }
            var panel = CreateSection(id, host.Title, pinned: true);
            panel.Children.Add(host.Create());
            return panel;
        }
        if (id == "section.performance" && _performanceSource?.Snapshot() is { Visible: true } performance)
        {
            var panel = CreateSection(id, "Performance", pinned: true);
            foreach (var descriptor in performance.ProfileRows.Concat(performance.Rows))
            {
                string key = PinTagPrefix + "performance." + descriptor.Id;
                panel.Children.Add(TryCreatePerformanceControl(descriptor, key) ?? CreatePerformanceRow(descriptor, key));
            }
            return panel;
        }
        if (_deviceBridge?.Snapshot() is { } snapshot && DevicePinSections(snapshot).FirstOrDefault(section => section.Id == id) is { } section)
        {
            var panel = CreateSection(id, section.Title, pinned: true);
            AddDeviceSectionRows(snapshot, section, panel, pinned: true);
            return panel;
        }
        if (id.StartsWith("section.", StringComparison.Ordinal))
        {
            var panel = CreateSection(id, "Section unavailable", pinned: true);
            panel.Children.Add(new TextBlock { Text = "Its controls will return when the provider is available.", Classes = { "caption" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            return panel;
        }
        return null;
    }
}
