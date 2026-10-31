using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     Turns a Device snapshot into the section list the destination's rail shows.
/// </summary>
/// <remarks>
///     The Device destination is a menu of pages rather than one long scrolling list, because a
///     handheld's whole surface is a few rows tall and a list that needs scrolling is a list a
///     controller cannot navigate quickly.
///     <para>
///         Shell publishes the pages, the shared Power, RGB, Controller and Info pages always among them,
///         and gives every row one of them. A page appears only while it has rows to draw, so a handheld
///         with no lighting shows no RGB page rather than an empty one.
///     </para>
/// </remarks>
internal static class DeviceOverlaySectionPages
{
    /// <summary>The id of the per-application enable toggle among the performance profile rows.</summary>
    /// <remarks>
    ///     It is the headline toggle on the Device root — the one control that turns a per-application
    ///     profile on — so it is pulled out of the section rows both here (it is not counted into any
    ///     section) and in the renderer (it is drawn at the top of the root, not in a page). One id, so
    ///     the two never disagree about which row is the toggle.
    /// </remarks>
    internal const string ApplicationProfileRowId = "application-profile";

    /// <summary>The stable rail and focus key for an entry.</summary>
    /// <param name="entry">The rail entry.</param>
    /// <returns>Its key.</returns>
    internal static string FocusKey(DeviceOverlaySectionEntry entry)
    {
        return new SectionKey("plugin." + entry.SectionId).PinKey;
    }

    /// <summary>Builds the section list for a snapshot.</summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <param name="performance">The shared performance rows, drawn on their own section's page.</param>
    /// <param name="powerControls">
    ///     The Windows and device power controls the Power page draws above the device's rows.
    /// </param>
    /// <returns>Sections that currently draw something, in presentation order, each with its row count.</returns>
    internal static IReadOnlyList<DeviceOverlaySectionEntry> Build(
        DeviceOverlaySnapshot snapshot,
        PerformanceOverlaySnapshot? performance = null,
        int powerControls = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<DeviceOverlaySectionEntry> entries = [];
        foreach (var section in snapshot.PluginSections)
        {
            var count = CapabilitiesIn(snapshot, section.SectionId).Count
                        + HostRowsIn(snapshot, section.SectionId).Count;
            if (performance is { Visible: true } && performance.SectionId == section.SectionId)
            {
                // The per-application enable toggle heads the Device root instead of a page.
                count += performance.Rows.Count + performance.ProfileRows
                    .Count(row => !string.Equals(row.Id, ApplicationProfileRowId, StringComparison.Ordinal));
            }

            if (section.SectionId == DeviceSections.PowerId)
            {
                count += powerControls;
            }

            if (count > 0)
            {
                entries.Add(new DeviceOverlaySectionEntry(section.SectionId, section.Title, section.Icon, count));
            }
        }

        return entries;
    }

    /// <summary>Selects the capabilities a section's page draws, in placement order.</summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <param name="sectionId">The section.</param>
    /// <returns>
    ///     Its capabilities ordered by sort order, then snapshot order; none while integration is off.
    /// </returns>
    internal static IReadOnlyList<DeviceOverlayCapability> CapabilitiesIn(
        DeviceOverlaySnapshot snapshot,
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
                .Where(item => string.Equals(
                    item.Capability.PluginSectionId,
                    sectionId,
                    StringComparison.Ordinal))
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
}
