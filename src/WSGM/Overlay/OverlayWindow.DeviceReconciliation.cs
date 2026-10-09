using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private const string ApplicationProfileFocusKey = "device.application-profile";

    private readonly Dictionary<string, DeviceOverlaySnapshot> _pinnedLayouts = new(StringComparer.Ordinal);
    private DeviceOverlaySnapshot? _deviceLayout;

    private static bool SameDeviceLayout(DeviceOverlaySnapshot? previous, DeviceOverlaySnapshot current)
    {
        if (previous is null || previous.Visible != current.Visible
                             || previous.PluginSections.Count != current.PluginSections.Count
                             || previous.HostSelections.Count != current.HostSelections.Count
                             || previous.GlyphPreview?.ProfileName != current.GlyphPreview?.ProfileName
                             || previous.GlyphPreview?.Detail != current.GlyphPreview?.Detail
                             || previous.GlyphPreview?.InputTestAvailable != current.GlyphPreview?.InputTestAvailable
                             || previous.GlyphSelection is null != current.GlyphSelection is null
                             || previous.Controller is null != current.Controller is null
                             || previous.AutoTdp is null != current.AutoTdp is null
                             || previous.AuthoredProfile is null != current.AuthoredProfile is null
                             || previous.LightingProfile is null != current.LightingProfile is null
                             || previous.Recovery is null != current.Recovery is null
                             || !CapabilityRowRenderer.SameRowLayouts(previous.Capabilities, current.Capabilities))
        {
            return false;
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
        CapabilityRowRenderer.RefreshValues(root, snapshot.Capabilities, marker);

        foreach (var row in root.GetLogicalDescendants().OfType<DeviceSettingRow>())
        {
            var key = UnpinnedKey(row.Tag);
            if (key == DeviceHostRowIds.GlyphSelection && snapshot.GlyphSelection is { } glyphs)
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
                (row.Parent as ProfileOverrideMarker)?.Refresh(descriptor?.OverrideId);
            }
        }

        foreach (var view in root.GetLogicalDescendants().OfType<DescriptorControlView>())
        {
            var key = UnpinnedKey(view.Tag);
            var descriptor = key == ApplicationProfileFocusKey
                ? performance?.ProfileRows.FirstOrDefault(row =>
                    row.Id == DeviceOverlaySectionPages.ApplicationProfileRowId)
                : HostDescriptor(snapshot, key);
            if (descriptor is not null)
            {
                view.Refresh(descriptor);
            }
        }
    }

    private Task UseGlobalOnDevice(string overrideId)
    {
        return RunCommandAsync(_deviceBridge, "Use global",
            (source, token) => source.UseGlobalAsync(overrideId, token));
    }

    private static string UnpinnedKey(object? tag)
    {
        var key = tag as string ?? string.Empty;
        return key.StartsWith(PinTagPrefix, StringComparison.Ordinal) ? key[PinTagPrefix.Length..] : key;
    }

    private static DescriptorRow? HostDescriptor(DeviceOverlaySnapshot snapshot, string id)
    {
        return id switch
        {
            DeviceHostRowIds.AutoTdp => snapshot.AutoTdp,
            DeviceHostRowIds.ControllerTarget => snapshot.Controller,
            DeviceHostRowIds.AuthoredProfile => snapshot.AuthoredProfile,
            DeviceHostRowIds.LightingProfile => snapshot.LightingProfile,
            DeviceHostRowIds.Retry => snapshot.Recovery,
            _ => null
        };
    }

    private Control CreateHostDeviceRow(DeviceOverlaySnapshot snapshot, DescriptorRow descriptor)
    {
        if (snapshot.HostSelections.TryGetValue(descriptor.Id, out var selection))
        {
            var choice = DeviceControlRows.Choice(descriptor.Id, descriptor.Title, descriptor.Description,
                selection.Choices, selection.Value, descriptor.CanInvoke, value => _ = RunCommandAsync(
                    _deviceBridge, descriptor.Title,
                    (source, token) => source.SetHostSelectionAsync(descriptor.Id, value, token)));
            var marker = new ProfileOverrideMarker(choice, UseGlobalOnDevice);
            marker.Refresh(descriptor.OverrideId);
            return marker;
        }

        return new DescriptorControlView(descriptor, descriptor.Id, async _ =>
        {
            if (descriptor.Id == DeviceHostRowIds.Retry)
            {
                await RunCommandAsync(_deviceBridge, "Device integration retry",
                    (source, token) => source.RetryDeviceCycleAsync(token));
            }
        });
    }
}
