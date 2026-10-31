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
                             || previous.AutoTdpTarget is null != current.AutoTdpTarget is null
                             || previous.AuthoredProfile is null != current.AuthoredProfile is null
                             || previous.LightingProfile is null != current.LightingProfile is null
                             || previous.Recovery is null != current.Recovery is null
                             || !DeviceOverlayCapability.SameLayouts(previous.Capabilities, current.Capabilities))
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

    /// <summary>Refreshes every Device row under <paramref name="root" /> in place, in one walk of the tree.</summary>
    /// <param name="root">The host or pinned group holding the rows.</param>
    /// <param name="snapshot">The Device snapshot.</param>
    /// <param name="performance">The performance snapshot the application profile switch follows, if any.</param>
    private static void RefreshDeviceValues(Control root, DeviceOverlaySnapshot snapshot,
        PerformanceOverlaySnapshot? performance)
    {
        var temperature = DeviceTemperature(snapshot);
        Dictionary<(string?, string, string?), DeviceOverlayCapability>? published = null;
        // Materialized because a refresh may replace an editor inside the row it refreshes.
        foreach (var control in root.GetLogicalDescendants().ToArray())
        {
            switch (control)
            {
                case DeviceCapabilityControl capability:
                    published ??= CapabilityRowRenderer.Index(snapshot.Capabilities);
                    CapabilityRowRenderer.Refresh(capability, published, temperature);
                    break;
                case DeviceSettingRow row:
                    RefreshHostSetting(row, snapshot);
                    break;
                case DescriptorControlView view:
                    var key = UnpinnedKey(view.Tag);
                    var descriptor = key == ApplicationProfileFocusKey
                        ? performance?.ProfileRows.FirstOrDefault(candidate =>
                            candidate.Id == DeviceOverlaySectionPages.ApplicationProfileRowId)
                        : HostDescriptor(snapshot, key);
                    if (descriptor is not null)
                    {
                        view.Refresh(descriptor);
                    }

                    break;
            }
        }
    }

    private static void RefreshHostSetting(DeviceSettingRow row, DeviceOverlaySnapshot snapshot)
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
            DeviceHostRowIds.AutoTdpTarget => snapshot.AutoTdpTarget,
            DeviceHostRowIds.ControllerTarget => snapshot.Controller,
            DeviceHostRowIds.AuthoredProfile => snapshot.AuthoredProfile,
            DeviceHostRowIds.LightingProfile => snapshot.LightingProfile,
            DeviceHostRowIds.Retry => snapshot.Recovery,
            _ => null
        };
    }

    /// <summary>AutoTDP's target slider, committing through the Device source like the switch above it.</summary>
    private DescriptorControlView CreateAutoTdpTargetRow(DescriptorRow descriptor)
    {
        return new DescriptorControlView(descriptor, descriptor.Id, _ => Task.CompletedTask,
            value => _ = RunCommandAsync(_deviceBridge, $"AutoTDP target set to {value} FPS",
                (source, token) => source.SetAutoTdpTargetAsync(value, token)),
            UseGlobalOnDevice);
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
