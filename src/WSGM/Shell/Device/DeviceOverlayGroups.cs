using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Splits each published Device page into the groups the Overlay draws, pins and offers to pin.</summary>
/// <remarks>
///     One list serves the page, the pin chooser and Quick Access, so a group has the same title, rows and
///     pin id wherever it appears. The page layout lives here as data: the Power page's limits lead, its fan
///     group comes first among the categories and folds on its own, and its groups take the Power titles; the
///     Controller page leads with its configuration.
/// </remarks>
internal static class DeviceOverlayGroups
{
    /// <summary>The prefix every Device group's pin id starts with.</summary>
    internal const string PinPrefix = "section.device.plugin.";

    /// <summary>Every group of every published page that has rows, in page order.</summary>
    /// <param name="snapshot">The Device snapshot.</param>
    /// <returns>The groups; capabilities only while integration is on.</returns>
    internal static IReadOnlyList<DeviceOverlayGroup> Build(DeviceOverlaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<DeviceOverlayGroup> groups = [];
        foreach (var section in snapshot.PluginSections)
        {
            var first = groups.Count;
            var capabilities = CapabilitiesIn(snapshot, section.SectionId);
            var allHostRows = HostRowsIn(snapshot, section.SectionId);
            // The extended VRR range belongs with the display rows it changes, not with the saved profiles.
            var displayRows = allHostRows.Where(row => row.Id == DeviceHostRowIds.ExtendedVrr).ToArray();
            var hostRows = allHostRows.Except(displayRows).ToArray();
            var power = section.SectionId == DeviceSections.PowerId;
            var prefix = PinPrefix + section.SectionId;
            // The limits are what the Power page is visited for, so they lead even when categorized.
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
                groups.Add(new DeviceOverlayGroup(prefix + ".main", power ? "Manual power and display" : section.Title,
                    section.SectionId, Ordered(lead), [], !power || lead.Any(IsFanRow)));
            }

            if (displayRows.Length > 0)
            {
                groups.Add(new DeviceOverlayGroup(prefix + ".display-range", "Variable refresh range", section.SectionId,
                    [], displayRows, !power));
            }

            // Cooling is visited on its own, not on the way to a limit, so the fan category comes first
            // and folds beside Power rather than inside it.
            var categories = power
                ? section.Categories.OrderBy(category => IsFanCategory(capabilities, category.Id) ? 0 : 1)
                : section.Categories.AsEnumerable();
            foreach (var category in categories)
            {
                var rows = capabilities
                    .Where(capability => capability.CategoryId == category.Id && !lead.Contains(capability)).ToArray();
                if (rows.Length > 0)
                {
                    groups.Add(new DeviceOverlayGroup(prefix + ".category." + category.Id, category.Title,
                        section.SectionId, Ordered(rows), [], !power || rows.Any(IsFanRow)));
                }
            }

            // Host controls have their own stable pin identity even when the plugin publishes only
            // categorized rows or no capabilities at all. The controller target and glyphs are what the
            // Controller page is visited for, so there they lead; elsewhere they follow the device's rows.
            if (hostRows.Length > 0)
            {
                groups.Insert(section.SectionId == DeviceSections.ControllerId ? first : groups.Count,
                    new DeviceOverlayGroup(prefix + ".configuration",
                        power ? "Automatic control and saved profiles" : "Configuration", section.SectionId, [],
                        hostRows, !power));
            }
        }

        return groups;
    }

    /// <summary>Selects the capabilities a section's page draws, in placement order.</summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <param name="sectionId">The section.</param>
    /// <returns>Its capabilities ordered by sort order, then snapshot order; none while integration is off.</returns>
    internal static IReadOnlyList<DeviceOverlayCapability> CapabilitiesIn(DeviceOverlaySnapshot snapshot,
        string sectionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Visible)
        {
            return [];
        }

        return
        [
            .. snapshot.Capabilities
                .Select((capability, index) => (Capability: capability, Index: index))
                .Where(item => string.Equals(item.Capability.PluginSectionId, sectionId, StringComparison.Ordinal))
                .OrderBy(item => item.Capability.SortOrder)
                .ThenBy(item => item.Index)
                .Select(item => item.Capability)
        ];
    }

    /// <summary>Selects the host-owned rows a section's page draws, in presentation order.</summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <param name="sectionId">The section.</param>
    /// <returns>The rows Shell placed on it.</returns>
    internal static IReadOnlyList<DescriptorRow> HostRowsIn(DeviceOverlaySnapshot snapshot, string sectionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return [.. snapshot.HostRows.Where(row => string.Equals(row.SectionId, sectionId, StringComparison.Ordinal))];
    }

    /// <summary>A row's stable key: its capability id, and its instance after a hash when it has one.</summary>
    /// <param name="capability">The row.</param>
    /// <returns>The key every surface uses for the row.</returns>
    internal static string RowKey(DeviceOverlayCapability capability)
    {
        return new DeviceCapabilityKey(capability.CapabilityId, capability.InstanceId).ToString();
    }

    private static bool IsFanCategory(IReadOnlyList<DeviceOverlayCapability> capabilities, string categoryId)
    {
        return capabilities.Any(capability => capability.CategoryId == categoryId
                                              && capability.Role is CapabilityRole.FanMode or CapabilityRole.FanCurve);
    }

    private static bool IsFanRow(DeviceOverlayCapability capability)
    {
        return capability.Role is CapabilityRole.FanMode or CapabilityRole.FanCurve or CapabilityRole.FanDuty
            or CapabilityRole.FanTargetRpm;
    }

    /// <summary>Orders rows by sort order and places each layout pair's companion right after it.</summary>
    private static DeviceOverlayCapability[] Ordered(IReadOnlyList<DeviceOverlayCapability> capabilities)
    {
        List<DeviceOverlayCapability> ordered = [];
        HashSet<string> emitted = new(StringComparer.Ordinal);
        foreach (var capability in capabilities.OrderBy(capability => capability.SortOrder))
        {
            if (!emitted.Add(RowKey(capability)))
            {
                continue;
            }

            ordered.Add(capability);
            if (capability.LayoutPair is { } pair && capabilities.FirstOrDefault(candidate =>
                        candidate.CapabilityId == pair.CapabilityId && candidate.InstanceId == pair.InstanceId) is
                    { } companion && emitted.Add(RowKey(companion)))
            {
                ordered.Add(companion);
            }
        }

        return [.. ordered];
    }
}
