using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private readonly Dictionary<string, DeviceOverlaySnapshot> _pinnedLayouts = new(StringComparer.Ordinal);
    private DeviceOverlaySnapshot? _deviceLayout;

    private static bool SameDeviceLayout(DeviceOverlaySnapshot? previous, DeviceOverlaySnapshot current)
    {
        if (previous is null || previous.Visible != current.Visible
                             || previous.Capabilities.Count != current.Capabilities.Count
                             || previous.PluginSections.Count != current.PluginSections.Count
                             || previous.HostSelections.Count != current.HostSelections.Count
                             || previous.GlyphPreview?.ProfileName != current.GlyphPreview?.ProfileName
                             || previous.GlyphPreview?.Detail != current.GlyphPreview?.Detail
                             || previous.GlyphPreview?.InputTestAvailable != current.GlyphPreview?.InputTestAvailable
                             || previous.GlyphSelection is null != current.GlyphSelection is null
                             || previous.Controller is null != current.Controller is null
                             || previous.AutoTdp is null != current.AutoTdp is null
                             || previous.AuthoredProfile is null != current.AuthoredProfile is null
                             || previous.Recovery is null != current.Recovery is null)
        {
            return false;
        }

        for (var index = 0; index < current.Capabilities.Count; index++)
        {
            var before = previous.Capabilities[index];
            var after = current.Capabilities[index];
            if (before.CapabilityId != after.CapabilityId || before.InstanceId != after.InstanceId
                                                          || before.CycleGeneration != after.CycleGeneration ||
                                                          before.DescriptorGeneration != after.DescriptorGeneration
                                                          || before.ValueKind != after.ValueKind ||
                                                          before.Writable != after.Writable
                                                          || before.SupportsAction != after.SupportsAction ||
                                                          before.Title != after.Title
                                                          || before.PluginSectionId != after.PluginSectionId ||
                                                          before.CategoryId != after.CategoryId)
            {
                return false;
            }
        }

        for (var index = 0; index < current.PluginSections.Count; index++)
        {
            var before = previous.PluginSections[index];
            var after = current.PluginSections[index];
            if (before.SectionId != after.SectionId || before.Title != after.Title || before.Key != after.Key
                || !before.Categories.SequenceEqual(after.Categories))
            {
                return false;
            }
        }

        foreach (var (key, selection) in current.HostSelections)
        {
            if (!previous.HostSelections.TryGetValue(key, out var before)
                || !before.Choices.SequenceEqual(selection.Choices))
            {
                return false;
            }
        }

        return true;
    }

    private void RefreshDeviceValues(Control root, DeviceOverlaySnapshot snapshot)
    {
        var performance = _performanceSource?.Snapshot();
        var marker = snapshot.Capabilities.FirstOrDefault(capability =>
                capability.Role == CapabilityRole.Telemetry && capability.Unit == CapabilityUnit.Celsius)?.CurrentValue
            ?.IntegerValue;
        foreach (var view in root.GetLogicalDescendants().OfType<DeviceCapabilityControl>())
        {
            foreach (var capability in snapshot.Capabilities)
            {
                if (view.CapabilityId == capability.CapabilityId && view.InstanceId == capability.InstanceId)
                {
                    view.Refresh(PresentDeviceCapability(capability), marker);
                    break;
                }
            }
        }

        foreach (var row in root.GetLogicalDescendants().OfType<DeviceSettingRow>())
        {
            var key = row.Tag as string ?? string.Empty;
            if (key.StartsWith(PinTagPrefix, StringComparison.Ordinal))
            {
                key = key[PinTagPrefix.Length..];
            }

            if (key == "device.glyph-selection" && snapshot.GlyphSelection is { } glyphs)
            {
                row.Refresh(
                    new CapabilityValue
                        { Kind = CapabilityValueKind.Choice, ChoiceValue = ((int)snapshot.GlyphMode).ToString() },
                    glyphs.CanInvoke, glyphs.Description);
            }

            if (snapshot.HostSelections.TryGetValue(key, out var selection))
            {
                var descriptor = HostDescriptor(snapshot, key);
                row.Refresh(new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = selection.Value },
                    descriptor?.CanInvoke ?? false, descriptor?.Description ?? string.Empty);
            }
        }

        foreach (var view in root.GetLogicalDescendants().OfType<DescriptorControlView>())
        {
            var descriptor = Equals(view.Tag, "device.application-profile")
                ? performance?.ProfileRows.FirstOrDefault(row => row.Id == "application-profile")
                : HostDescriptor(snapshot, view.Tag as string ?? string.Empty);
            if (descriptor is not null)
            {
                view.Refresh(descriptor);
            }
        }
    }

    private static DescriptorRow? HostDescriptor(DeviceOverlaySnapshot snapshot, string id)
    {
        return id switch
        {
            "device.auto-tdp" => snapshot.AutoTdp,
            "device.controller-target" => snapshot.Controller,
            "device.authored-profile" => snapshot.AuthoredProfile,
            "device.retry" or "pin:device.retry" => snapshot.Recovery,
            _ => null
        };
    }

    private static DeviceOverlayCapability PresentDeviceCapability(DeviceOverlayCapability capability)
    {
        var title = capability.Role == CapabilityRole.ScenarioMode ? "Firmware power mode" : capability.Title;
        var description = capability.Status == DescriptorStatus.Available
                          && (capability.Description.StartsWith("Observed ", StringComparison.Ordinal)
                              || capability.Description.StartsWith("Verified ", StringComparison.Ordinal))
            ? string.Empty
            : capability.Description;
        return title == capability.Title && description == capability.Description
            ? capability
            : capability with { Title = title, Description = description };
    }

    private Control CreateHostDeviceRow(DeviceOverlaySnapshot snapshot, DescriptorRow descriptor)
    {
        if (snapshot.HostSelections.TryGetValue(descriptor.Id, out var selection))
        {
            return DeviceControlRows.Choice(descriptor.Id, descriptor.Title, descriptor.Description, selection.Choices,
                selection.Value, descriptor.CanInvoke, value => _ = RunDeviceCommandAsync(descriptor.Title,
                    (source, token) => source.SetHostSelectionAsync(descriptor.Id, value, token)));
        }

        return new DescriptorControlView(descriptor, descriptor.Id, async current =>
        {
            if (descriptor.Id == "device.retry")
            {
                await RunDeviceCommandAsync("Device integration retry",
                    (source, token) => source.RetryDeviceCycleAsync(token));
            }
        });
    }
}
