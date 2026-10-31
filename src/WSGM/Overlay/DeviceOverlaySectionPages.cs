using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Settings;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     Turns a Device snapshot into the section list the destination's root page shows.
/// </summary>
/// <remarks>
///     The Device destination is a menu of pages rather than one long scrolling list, because a
///     handheld's whole surface is a few rows tall and a list that needs scrolling is a list a
///     controller cannot navigate quickly.
///     <para>
///         A section appears only when the plugin published something for it. That keeps the menu honest on
///         every device — a handheld with no lighting shows no Lighting page rather than an empty one — and
///         it means no section here is a fixture that a future plugin has to satisfy.
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

    /// <summary>The fixed order sections are offered in.</summary>
    /// <remarks>
    ///     Ordered by how often a handheld user reaches for them, not by the enum. Power comes first
    ///     because it is the reason the Device page is opened mid-game; diagnostics comes last because
    ///     it is the reason it is opened when something is wrong.
    /// </remarks>
    private static readonly DeviceOverlaySection[] Order =
    [
        DeviceOverlaySection.Overview,
        DeviceOverlaySection.PowerAndThermals,
        DeviceOverlaySection.ControllerAndMotion,
        DeviceOverlaySection.Oem,
        DeviceOverlaySection.LightingAndFeatures,
        DeviceOverlaySection.Diagnostics
    ];

    /// <summary>
    ///     The declared section key a WSGM-owned section is the same subject as, or null when it has
    ///     no plugin counterpart.
    /// </summary>
    /// <remarks>
    ///     This is what stops the menu showing two Power cards and two Controller cards. A plugin
    ///     describes its own layout, and WSGM has fixed homes for the rows it owns itself; before this
    ///     the two sat side by side, so a device that declared a Power section produced that page plus
    ///     WSGM's "Power and thermals" — with the power limits on one and the frame limit on the other.
    ///     A WSGM section whose key matches a declared section is folded into it instead, and only
    ///     falls back to a page of its own when the plugin declares nothing that covers it.
    /// </remarks>
    private static SettingSectionKey? DeclaredKeyFor(DeviceOverlaySection section)
    {
        return section switch
        {
            DeviceOverlaySection.Overview => SettingSectionKey.General,
            DeviceOverlaySection.PowerAndThermals => SettingSectionKey.Power,
            DeviceOverlaySection.ControllerAndMotion => SettingSectionKey.Controller,
            DeviceOverlaySection.LightingAndFeatures => SettingSectionKey.Lighting,
            DeviceOverlaySection.Diagnostics => SettingSectionKey.Diagnostics,
            // OEM assignments are WSGM policy over a plugin's controls, and a plugin has no vocabulary
            // for that subject, so this one keeps its own page whatever the device declares.
            _ => null
        };
    }

    /// <summary>The declared section a WSGM-owned section folds into, or null to keep its own page.</summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <param name="section">The WSGM-owned section.</param>
    /// <returns>The declared section id that absorbs it.</returns>
    private static string? AbsorbedBy(DeviceOverlaySnapshot snapshot, DeviceOverlaySection section)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (DeclaredKeyFor(section) is not { } key)
        {
            return null;
        }

        return snapshot.PluginSections
            .FirstOrDefault(candidate => candidate.Key == key)?.SectionId;
    }

    /// <summary>The WSGM-owned section a declared section absorbs, or null when it absorbs none.</summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <param name="sectionId">The declared section being drawn.</param>
    /// <returns>The WSGM-owned section whose rows belong on that page.</returns>
    internal static DeviceOverlaySection? SectionAbsorbedInto(
        DeviceOverlaySnapshot snapshot,
        string sectionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var section in Order)
        {
            if (string.Equals(AbsorbedBy(snapshot, section), sectionId, StringComparison.Ordinal))
            {
                return section;
            }
        }

        return null;
    }

    /// <summary>The page a section opens.</summary>
    /// <param name="section">The section.</param>
    /// <returns>Its navigation page.</returns>
    internal static OverlayPage PageFor(DeviceOverlaySection section)
    {
        return section switch
        {
            DeviceOverlaySection.Overview => OverlayPage.DeviceOverview,
            DeviceOverlaySection.PowerAndThermals => OverlayPage.DevicePowerAndThermals,
            DeviceOverlaySection.ControllerAndMotion => OverlayPage.DeviceControllerAndMotion,
            DeviceOverlaySection.Oem => OverlayPage.DeviceOem,
            DeviceOverlaySection.LightingAndFeatures => OverlayPage.DeviceLightingAndFeatures,
            DeviceOverlaySection.Diagnostics => OverlayPage.DeviceDiagnostics,
            _ => throw new ArgumentOutOfRangeException(nameof(section))
        };
    }

    /// <summary>The section a page belongs to, or null when the page is not a Device section.</summary>
    /// <param name="page">The navigation page.</param>
    /// <returns>Its section.</returns>
    internal static DeviceOverlaySection? SectionFor(OverlayPage page)
    {
        return page switch
        {
            OverlayPage.DeviceOverview => DeviceOverlaySection.Overview,
            OverlayPage.DevicePowerAndThermals => DeviceOverlaySection.PowerAndThermals,
            OverlayPage.DeviceControllerAndMotion => DeviceOverlaySection.ControllerAndMotion,
            OverlayPage.DeviceOem => DeviceOverlaySection.Oem,
            OverlayPage.DeviceLightingAndFeatures => DeviceOverlaySection.LightingAndFeatures,
            OverlayPage.DeviceDiagnostics => DeviceOverlaySection.Diagnostics,
            _ => null
        };
    }

    /// <summary>The stable focus key for an entry's card on the root page.</summary>
    /// <param name="entry">The menu entry.</param>
    /// <returns>Its focus key.</returns>
    internal static string FocusKey(DeviceOverlaySectionEntry entry)
    {
        return entry.PluginSectionId is { } id
            ? new SectionKey("plugin." + id).PinKey
            : FocusKey(entry.Section);
    }

    internal static string FocusKey(DeviceOverlaySection section)
    {
        return new SectionKey(section switch
        {
            DeviceOverlaySection.Overview => "overview",
            DeviceOverlaySection.PowerAndThermals => "power",
            DeviceOverlaySection.ControllerAndMotion => "controller",
            DeviceOverlaySection.Oem => "oem",
            DeviceOverlaySection.LightingAndFeatures => "lighting",
            DeviceOverlaySection.Diagnostics => "diagnostics",
            _ => "unknown"
        }).PinKey;
    }

    /// <summary>Builds the section menu for a snapshot.</summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <param name="performance">The shared performance rows that render on Power.</param>
    /// <returns>Sections that currently have something to show, in presentation order.</returns>
    internal static IReadOnlyList<DeviceOverlaySectionEntry> Build(
        DeviceOverlaySnapshot snapshot,
        PerformanceOverlaySnapshot? performance = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Windows energy plans keep Power available even with device integration disabled.
        Dictionary<DeviceOverlaySection, int> counts = new() { [DeviceOverlaySection.PowerAndThermals] = 1 };
        Dictionary<string, int> pluginCounts = [];
        foreach (var capability in snapshot.Capabilities)
        {
            if (capability.PluginSectionId is { } pluginSection)
            {
                pluginCounts[pluginSection] = pluginCounts.GetValueOrDefault(pluginSection) + 1;
                continue;
            }

            counts[capability.Section] = counts.GetValueOrDefault(capability.Section) + 1;
        }

        // WSGM's own rows never appear in the capability list, so each has to be counted into its
        // section explicitly. Without this a section holding only a direct row has a count of zero
        // and is dropped from the menu, which makes the row unreachable — the case for AutoTDP on a
        // device that publishes no power capability, and for the controller target on any device,
        // since no plugin publishes one.
        foreach (var (section, row) in DirectRows(snapshot))
        {
            if (row is not null)
            {
                counts[section] = counts.GetValueOrDefault(section) + 1;
            }
        }

        if (performance is { Visible: true })
        {
            // The performance rows render on Power and thermals (frame limit, overlay level, and the
            // per-application detail rows), so they count toward that page. The per-application enable
            // toggle is not counted here at all: it is the headline toggle on the Device root, not a
            // row inside any section.
            counts[DeviceOverlaySection.PowerAndThermals] += performance.Rows.Count + performance.ProfileRows
                .Count(row => !string.Equals(row.Id, ApplicationProfileRowId, StringComparison.Ordinal));
        }

        // A WSGM-owned section whose subject the plugin already declares is not a page: its rows
        // move onto the declared one, and the menu shows one card for that subject
        // instead of two that each hold half of it.
        Dictionary<string, DeviceOverlaySection> absorbed = [];
        foreach (var section in Order)
        {
            if (AbsorbedBy(snapshot, section) is { } host)
            {
                absorbed[host] = section;
            }
        }

        List<DeviceOverlaySectionEntry> entries = [];

        // The plugin's declared layout leads: it is the device describing itself. The WSGM-owned
        // sections that remain — anything the device claimed no subject for — follow it.
        foreach (var pluginSection in snapshot.PluginSections)
        {
            var pluginCount = pluginCounts.GetValueOrDefault(pluginSection.SectionId);
            if (absorbed.TryGetValue(pluginSection.SectionId, out var owned))
            {
                pluginCount += counts.GetValueOrDefault(owned);
            }

            if (pluginCount == 0)
            {
                continue;
            }

            entries.Add(new DeviceOverlaySectionEntry(
                absorbed.GetValueOrDefault(pluginSection.SectionId, DeviceOverlaySection.Overview),
                OverlayPage.DevicePluginSection,
                pluginSection.Title,
                pluginCount)
            {
                PluginSectionId = pluginSection.SectionId,
                Icon = pluginSection.Icon
            });
        }

        entries.AddRange(Order
            .Select(section => (Section: section, Count: counts.GetValueOrDefault(section)))
            .Where(item => item.Count != 0 && AbsorbedBy(snapshot, item.Section) is null)
            .Select(item => new DeviceOverlaySectionEntry(
                item.Section,
                PageFor(item.Section),
                TitleFor(item.Section),
                item.Count)));

        return entries;
    }

    /// <summary>The direct rows and the section each belongs to, in presentation order.</summary>
    /// <remarks>
    ///     One table for the menu counting above and for the section renderer, so a row can
    ///     never be counted into one section and drawn on another.
    /// </remarks>
    private static IEnumerable<(DeviceOverlaySection Section, DescriptorRow? Row)> DirectRows(
        DeviceOverlaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        yield return (DeviceOverlaySection.PowerAndThermals, snapshot.AutoTdp);
        yield return (DeviceOverlaySection.PowerAndThermals, snapshot.AutoTdpTarget);
        // The authored fan curve sits with power and thermals: it is a decision about how the device
        // cools, and the per-game switch that scopes it is the toggle on the Device root.
        yield return (DeviceOverlaySection.PowerAndThermals, snapshot.AuthoredProfile);
        yield return (DeviceOverlaySection.LightingAndFeatures, snapshot.LightingProfile);
        yield return (DeviceOverlaySection.ControllerAndMotion, snapshot.Controller);
        // Glyph selection sits with the controller it draws, not on a page of its own.
        yield return (DeviceOverlaySection.ControllerAndMotion, snapshot.GlyphSelection);
        yield return (DeviceOverlaySection.Diagnostics, snapshot.Recovery);
    }

    /// <summary>Selects the capabilities belonging to one section.</summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <param name="section">The section to filter to.</param>
    /// <returns>That section's capabilities, in snapshot order.</returns>
    internal static IReadOnlyList<DeviceOverlayCapability> CapabilitiesIn(
        DeviceOverlaySnapshot snapshot,
        DeviceOverlaySection section)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return
        [
            .. snapshot.Capabilities.Where(capability =>
                capability.PluginSectionId is null && capability.Section == section)
        ];
    }

    /// <summary>Selects the capabilities of one plugin-declared section, in placement order.</summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <param name="sectionId">The declared section.</param>
    /// <returns>That section's capabilities ordered by sort order, then snapshot order.</returns>
    internal static IReadOnlyList<DeviceOverlayCapability> CapabilitiesInPluginSection(
        DeviceOverlaySnapshot snapshot,
        string sectionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
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

    private static string TitleFor(DeviceOverlaySection section)
    {
        return section switch
        {
            DeviceOverlaySection.Overview => "Overview",
            DeviceOverlaySection.PowerAndThermals => "Power",
            DeviceOverlaySection.ControllerAndMotion => "Controller",
            DeviceOverlaySection.Oem => "OEM buttons",
            DeviceOverlaySection.LightingAndFeatures => "Lighting and features",
            DeviceOverlaySection.Diagnostics => "Diagnostics and recovery",
            _ => "Device"
        };
    }
}
