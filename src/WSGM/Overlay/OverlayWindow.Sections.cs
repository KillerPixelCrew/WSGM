using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Labs.Panels;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    // Several steps of one render or pin lookup read the same device snapshot; its pin sections are
    // derived once per snapshot instance rather than once per step.
    private (DeviceOverlaySnapshot Snapshot, DevicePinSection[] Sections)? _pinSections;

    private void RestoreSectionHeaderFocus(string? key)
    {
        if (key?.StartsWith("section.", StringComparison.Ordinal) is not true)
        {
            return;
        }

        FocusSearch.First<Button>(this, button => Equals(button.Tag, key) && button.IsEffectivelyVisible)
            ?.Focus(NavigationMethod.Directional);
    }

    private bool PinnedSectionProvidersAvailable()
    {
        var snapshot = _deviceBridge?.Snapshot();
        var available = snapshot is null
            ? []
            : DevicePinSections(snapshot).Select(section => section.Id).ToHashSet(StringComparer.Ordinal);
        return _pins.All(id => _controlPinFactories.ContainsKey(id)
                               || (id == "section.performance" && _performanceSource?.Snapshot().Visible is true)
                               || (!id.StartsWith("section.device.", StringComparison.Ordinal) &&
                                   id != "section.performance")
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
            Spacing = 8,
            Tag = pinned ? PinTagPrefix + id : id,
            VerticalAlignment = VerticalAlignment.Top
        };
        panel.Children.Add(CreateSectionHeader(id, title, pinned));
        return panel;
    }

    private static Border WrapDeviceSection(StackPanel content)
    {
        return new Border
        {
            Classes = { "device-group" }, Child = content, Tag = content.Tag,
            VerticalAlignment = VerticalAlignment.Top
        };
    }

    private DevicePinSection[] DevicePinSections(DeviceOverlaySnapshot snapshot)
    {
        if (_pinSections is { } cached && ReferenceEquals(cached.Snapshot, snapshot))
        {
            return cached.Sections;
        }

        DevicePinSection[] sections = [.. BuildDevicePinSections(snapshot)];
        _pinSections = (snapshot, sections);
        return sections;
    }

    private static IEnumerable<DevicePinSection> BuildDevicePinSections(DeviceOverlaySnapshot snapshot)
    {
        foreach (var section in snapshot.PluginSections)
        {
            var capabilities = snapshot.Visible
                ? DeviceOverlaySectionPages.CapabilitiesInPluginSection(snapshot, section.SectionId)
                : [];
            var owned = DeviceOverlaySectionPages.SectionAbsorbedInto(snapshot, section.SectionId);
            if (!snapshot.Visible && owned != DeviceOverlaySection.ControllerAndMotion)
            {
                owned = null;
            }

            var power = owned == DeviceOverlaySection.PowerAndThermals;
            var prefix = "section.device.plugin." + section.SectionId;
            var lead = capabilities.Where(capability => capability.CategoryId is null
                                                        || (power &&
                                                            capability.Role is CapabilityRole.PowerSustainedLimit
                                                                or CapabilityRole.PowerSlowLimit))
                .OrderBy(capability => power
                    ? capability.Role switch
                    {
                        CapabilityRole.ScenarioMode => 0,
                        CapabilityRole.PowerSustainedLimit => 1,
                        CapabilityRole.PowerSlowLimit => 2,
                        _ => 3
                    }
                    : capability.SortOrder).ToArray();
            if (lead.Length > 0)
            {
                yield return new DevicePinSection(prefix + ".main", power ? "Manual power and display" : section.Title,
                    section.SectionId, lead);
            }

            var categories = power
                ? section.Categories.OrderBy(category => capabilities.Any(capability =>
                    capability.CategoryId == category.Id
                    && capability.Role is CapabilityRole.FanMode or CapabilityRole.FanCurve)
                    ? 0
                    : 1)
                : section.Categories.AsEnumerable();
            foreach (var category in categories)
            {
                var rows = capabilities
                    .Where(capability => capability.CategoryId == category.Id && !lead.Contains(capability)).ToArray();
                if (rows.Length > 0)
                {
                    yield return new DevicePinSection(prefix + ".category." + category.Id, category.Title,
                        section.SectionId, rows);
                }
            }

            // Host controls have their own stable pin identity even when the plugin publishes
            // only categorized rows or no capabilities at all.
            if (owned is { } host)
            {
                yield return new DevicePinSection(prefix + ".configuration",
                    power ? "Automatic control and saved profiles" : "Configuration",
                    section.SectionId, [], host);
            }
        }

        foreach (var section in DeviceOverlaySectionPages.Build(snapshot).Where(section =>
                     section.PluginSectionId is null
                     && (snapshot.Visible || section.Section == DeviceOverlaySection.ControllerAndMotion)))
        {
            yield return new DevicePinSection(
                DeviceOverlaySectionPages.FocusKey(section.Section)
                    .Replace("device.section.", "section.device.", StringComparison.Ordinal), section.Title, null,
                snapshot.Visible ? DeviceOverlaySectionPages.CapabilitiesIn(snapshot, section.Section) : [],
                section.Section);
        }
    }

    private Control? AddDeviceSectionRows(DeviceOverlaySnapshot snapshot, DevicePinSection section,
        Panel target, string? focusedKey = null, bool pinned = false)
    {
        Control? restoreFocus = null;
        if (section.Owned is { } hostSection)
        {
            restoreFocus = RenderOwnedDeviceRows(snapshot, hostSection, focusedKey, target, !pinned);
            if (pinned)
            {
                // A host row may sit inside its game-override marker, which carries no key of its own.
                foreach (var control in target.Children
                             .Select(child => child is ProfileOverrideMarker marker ? marker.Row : child)
                             .Where(control => control.Tag is string))
                {
                    if (control.Tag is string key && !key.StartsWith(PinTagPrefix, StringComparison.Ordinal))
                    {
                        control.Tag = PinTagPrefix + key;
                        if (control is DeviceSettingRow setting)
                        {
                            setting.Editor.Tag = control.Tag;
                        }
                    }
                }
            }
        }

        var readings = new FlexPanel { Wrap = FlexWrap.Wrap, ColumnSpacing = 12, RowSpacing = 4 };
        var ordered = OrderDeviceCapabilities(section.Capabilities);
        foreach (var capability in ordered)
        {
            var presentation = PresentDeviceCapability(capability);
            var key = (pinned ? PinTagPrefix : "") + DeviceRowKey(capability);
            var row = CreateDeviceCapabilityRow(presentation, key);
            ToolTip.SetTip(row, capability.Description);
            if (!capability.Writable && capability.ValueKind != CapabilityValueKind.None
                                     && capability.Prominence != CapabilityProminence.Primary)
            {
                row.MinWidth = capability.Prominence == CapabilityProminence.Compact ? 112 : 160;
                Flex.SetGrow(row, 1);
                readings.Children.Add(row);
            }
            else
            {
                if (readings.Children.Count > 0)
                {
                    target.Children.Add(readings);
                    readings = new FlexPanel { Wrap = FlexWrap.Wrap, ColumnSpacing = 12, RowSpacing = 4 };
                }

                target.Children.Add(row);
            }

            if (key == focusedKey)
            {
                restoreFocus = row;
            }
        }

        if (readings.Children.Count > 0)
        {
            target.Children.Add(readings);
        }

        return restoreFocus;
    }

    private static IEnumerable<DeviceOverlayCapability> OrderDeviceCapabilities(
        IReadOnlyList<DeviceOverlayCapability> capabilities)
    {
        HashSet<string> emitted = new(StringComparer.Ordinal);
        foreach (var capability in capabilities.OrderBy(capability => capability.SortOrder))
        {
            if (!emitted.Add(DeviceRowKey(capability)))
            {
                continue;
            }

            yield return capability;
            if (capability.LayoutPair is { } pair && capabilities.FirstOrDefault(candidate =>
                                                      candidate.CapabilityId == pair.CapabilityId &&
                                                      candidate.InstanceId == pair.InstanceId) is { } companion
                                                  && emitted.Add(DeviceRowKey(companion)))
            {
                yield return companion;
            }
        }
    }

    private Control? CreatePinnedSection(string id)
    {
        if (_controlPinFactories.TryGetValue(id, out var host))
        {
            if (PinnedSectionsGrid.Children.FirstOrDefault(row => Equals(row.Tag, PinTagPrefix + id)) is { } existing)
            {
                return existing;
            }

            var panel = CreateSection(id, host.Title, true);
            panel.Children.Add(host.Create());
            return WrapDeviceSection(panel);
        }

        if (id == "section.performance" && _performanceSource?.Snapshot() is { Visible: true } performance)
        {
            var group = PinnedSectionsGrid.Children.OfType<Border>()
                            .FirstOrDefault(row => Equals(row.Tag, PinTagPrefix + id))
                        ?? WrapDeviceSection(CreateSection(id, "Performance", true));
            ReconcilePerformanceRows((StackPanel)group.Child!, performance.ProfileRows.Concat(performance.Rows), true);

            return group;
        }

        if (_deviceBridge?.Snapshot() is { } snapshot &&
            DevicePinSections(snapshot).FirstOrDefault(candidate => candidate.Id == id) is { } section)
        {
            if (_pinnedLayouts.TryGetValue(id, out var previous) && SameDeviceLayout(previous, snapshot)
                                                                 && PinnedSectionsGrid.Children.FirstOrDefault(row =>
                                                                         Equals(row.Tag, PinTagPrefix + id)) is
                                                                     { } existing)
            {
                RefreshDeviceValues(existing, snapshot);
                return existing;
            }

            _pinnedLayouts[id] = snapshot;
            var panel = CreateSection(id, section.Title, true);
            AddDeviceSectionRows(snapshot, section, panel, pinned: true);
            return WrapDeviceSection(panel);
        }

        if (!id.StartsWith("section.", StringComparison.Ordinal))
        {
            return null;
        }

        var unavailable = CreateSection(id, "Section unavailable", true);
        unavailable.Children.Add(new TextBlock
        {
            Text = "Its controls will return when the provider is available.", Classes = { "caption" },
            TextWrapping = TextWrapping.Wrap
        });
        return WrapDeviceSection(unavailable);
    }

    private sealed record DevicePinSection(
        string Id,
        string Title,
        string? PluginSectionId,
        IReadOnlyList<DeviceOverlayCapability> Capabilities,
        DeviceOverlaySection? Owned = null);
}
