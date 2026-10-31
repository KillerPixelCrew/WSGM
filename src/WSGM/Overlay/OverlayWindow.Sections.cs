using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private Panel? _deviceCapabilityParent;
    private int _deviceCapabilityIndex;
    private bool _unifiedPowerDetails;

    private void RestoreSectionHeaderFocus(string? key)
    {
        if (key?.StartsWith("section.", StringComparison.Ordinal) is not true)
        {
            return;
        }

        FocusSearch.First<Button>(this, button => Equals(button.Tag, key) && button.IsEffectivelyVisible)
            ?.Focus(NavigationMethod.Directional);
    }

    private SectionPinHeader CreateSectionHeader(string id, string title, bool pinnedSurface = false)
    {
        var header = new SectionPinHeader(id, title, key => PinToggleRequested?.Invoke(key), pinnedSurface);
        header.Refresh(_pins.Contains(id));
        // Pin indicator updates visit only the headers in the tree, not every control in the window.
        header.AttachedToLogicalTree += (_, _) =>
        {
            _pinHeaders.Add(header);
            header.Refresh(_pins.Contains(header.SectionId));
        };
        header.DetachedFromLogicalTree += (_, _) => _pinHeaders.Remove(header);
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

    private Border WrapDeviceSection(StackPanel content)
    {
        return new Border
        {
            Child = _unifiedPowerDetails && content.Tag is string key
                    && !key.StartsWith(PinTagPrefix, StringComparison.Ordinal)
                ? content : FoldSection(content), Tag = content.Tag,
            VerticalAlignment = VerticalAlignment.Top
        };
    }

    private CollapsibleSection FoldSection(StackPanel content)
    {
        var header = (SectionPinHeader)content.Children[0];
        // The fold's heading carries the title; the header keeps only its pin action there.
        content.Children.RemoveAt(0);
        header.ShowPinOnly();
        var key = (string)content.Tag!;
        return CreateFold(key, header.Title, content, header);
    }

    private CollapsibleSection CreateFold(string key, string title, Control content, Control? action = null)
    {
        var section = new CollapsibleSection(title, content, action)
        {
            IsExpanded = _session.ExpandedSections.Contains(key)
        };
        section.Heading.Tag = key + ".expand";
        section.ExpansionChanged += expanded =>
        {
            if (expanded)
            {
                _session.ExpandedSections.Add(key);
            }
            else
            {
                _session.ExpandedSections.Remove(key);
            }
        };
        return section;
    }

    private void FoldStaticSections()
    {
        Fold(PerformanceSection);
        if (_deviceCapabilityParent is null)
        {
            _deviceCapabilityParent = (Panel)DeviceCapabilityList.Parent!;
            _deviceCapabilityIndex = _deviceCapabilityParent.Children.IndexOf(DeviceCapabilityList);
            var powerBody = DevicePowerControlsCard.Child!;
            DevicePowerControlsCard.Child = null;
            DevicePowerControlsCard.Child = CreateFold("section.device.unified-power", "Power", powerBody);
        }
        PanelSystemDisplay.Tag = "section.display";
        Fold(PanelSystemDisplay);

        return;

        void Fold(StackPanel panel)
        {
            if (panel.Children.FirstOrDefault() is not SectionPinHeader)
            {
                return;
            }

            var body = new StackPanel { Spacing = panel.Spacing, Tag = panel.Tag };
            foreach (var child in panel.Children.ToArray())
            {
                panel.Children.Remove(child);
                body.Children.Add(child);
            }

            if (panel.Parent is Border border)
            {
                border.Classes.Remove("device-group");
            }

            panel.Children.Add(FoldSection(body));
        }
    }

    private void PlaceUnifiedPowerControls(DeviceOverlaySnapshot snapshot,
        IReadOnlyList<DeviceOverlaySectionEntry> sectionPages)
    {
        _unifiedPowerDetails = _navigation.Page == OverlayPage.DevicePowerAndThermals
                               || _navigation.Page == OverlayPage.DevicePluginSection
                               && DeviceOverlaySectionPages.SectionAbsorbedInto(snapshot,
                                   _navigation.SectionId ?? string.Empty)
                               == DeviceOverlaySection.PowerAndThermals
                               || _navigation.Page == OverlayPage.Device
                               && OverviewPowerEntry(snapshot, sectionPages) is not null;
        if (_unifiedPowerDetails && !ReferenceEquals(DeviceCapabilityList.Parent, UnifiedDevicePowerBody))
        {
            ((Panel)DeviceCapabilityList.Parent!).Children.Remove(DeviceCapabilityList);
            UnifiedDevicePowerBody.Children.Add(DeviceCapabilityList);
        }
        else if (!_unifiedPowerDetails && ReferenceEquals(DeviceCapabilityList.Parent, UnifiedDevicePowerBody))
        {
            UnifiedDevicePowerBody.Children.Remove(DeviceCapabilityList);
            _deviceCapabilityParent!.Children.Insert(_deviceCapabilityIndex, DeviceCapabilityList);
        }
    }

    /// <summary>The Power page the Overview repeats, or null while integration publishes no power controls.</summary>
    private static DeviceOverlaySectionEntry? OverviewPowerEntry(DeviceOverlaySnapshot snapshot,
        IReadOnlyList<DeviceOverlaySectionEntry> sectionPages)
    {
        return snapshot.Visible
            ? sectionPages.FirstOrDefault(entry => entry.Section == DeviceOverlaySection.PowerAndThermals)
            : null;
    }

    /// <summary>Every pinnable Device group the snapshot publishes, derived once per render or pin pass.</summary>
    /// <param name="snapshot">The Device snapshot.</param>
    /// <param name="sectionPages">The section menu already built from it, or null to build it here.</param>
    /// <returns>The groups in presentation order.</returns>
    private static DevicePinSection[] DevicePinSections(DeviceOverlaySnapshot snapshot,
        IReadOnlyList<DeviceOverlaySectionEntry>? sectionPages = null)
    {
        return [.. BuildDevicePinSections(snapshot, sectionPages ?? DeviceOverlaySectionPages.Build(snapshot))];
    }

    private static IEnumerable<DevicePinSection> BuildDevicePinSections(DeviceOverlaySnapshot snapshot,
        IReadOnlyList<DeviceOverlaySectionEntry> sectionPages)
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

        foreach (var section in sectionPages.Where(section =>
                     section.PluginSectionId is null
                     && (snapshot.Visible || section.Section == DeviceOverlaySection.ControllerAndMotion)))
        {
            yield return new DevicePinSection(
                SectionKey.FromKey(DeviceOverlaySectionPages.FocusKey(section.Section)).FocusKey, section.Title, null,
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

        var temperature = DeviceTemperature(snapshot);
        foreach (var capability in OrderDeviceCapabilities(section.Capabilities))
        {
            var key = (pinned ? PinTagPrefix : "") + DeviceRowKey(capability);
            var row = CreateDeviceCapabilityRow(CapabilityRowRenderer.Present(capability), key, temperature);
            ToolTip.SetTip(row, capability.Description);
            CapabilityRowRenderer.AddRow(target, row,
                !capability.Writable && capability.ValueKind != CapabilityValueKind.None
                                     && capability.Prominence != CapabilityProminence.Primary
                    ? capability.Prominence == CapabilityProminence.Compact ? 112 : 160
                    : null);
            if (key == focusedKey)
            {
                restoreFocus = row;
            }
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

    /// <summary>Draws one pinned group, or keeps and refreshes the one already drawn.</summary>
    /// <param name="id">The pin id.</param>
    /// <param name="device">The Device snapshot and its groups, or null without a device source.</param>
    /// <param name="performance">The performance snapshot, or null without one.</param>
    /// <param name="graphics">The graphics snapshot, or null without one.</param>
    /// <returns>The group, or null when the id names nothing this build can draw.</returns>
    private Control? CreatePinnedSection(string id,
        (DeviceOverlaySnapshot Snapshot, DevicePinSection[] Sections)? device,
        PerformanceOverlaySnapshot? performance, GraphicsOverlaySnapshot? graphics)
    {
        if (id.StartsWith(ProgramsService.PinPrefix, StringComparison.Ordinal))
        {
            return CreateProgramPin(id);
        }

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

        if (id == "section.performance" && performance is { Visible: true })
        {
            var group = PinnedSectionsGrid.Children.OfType<Border>()
                            .FirstOrDefault(row => Equals(row.Tag, PinTagPrefix + id))
                        ?? WrapDeviceSection(CreateSection(id, "Performance", true));
            var folding = (CollapsibleSection)group.Child!;
            ReconcilePerformanceRows((StackPanel)folding.Body, performance.ProfileRows.Concat(performance.Rows),
                true);

            return group;
        }

        if (id.StartsWith(GraphicsSectionPins.Prefix, StringComparison.Ordinal)
            && CreatePinnedGraphicsSection(id, graphics) is { } pinnedGraphics)
        {
            return pinnedGraphics;
        }

        if (device is { } pinnedDevice
            && pinnedDevice.Sections.FirstOrDefault(candidate => candidate.Id == id) is { } section)
        {
            var snapshot = pinnedDevice.Snapshot;
            if (_pinnedLayouts.TryGetValue(id, out var previous) && SameDeviceLayout(previous, snapshot)
                                                                 && PinnedSectionsGrid.Children.FirstOrDefault(row =>
                                                                         Equals(row.Tag, PinTagPrefix + id)) is
                                                                     { } existing)
            {
                RefreshDeviceValues(existing, snapshot, performance);
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
