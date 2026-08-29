using System;
using System.Collections.Generic;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>One Device section as the root page presents it.</summary>
/// <param name="Section">The section this entry opens.</param>
/// <param name="Page">The navigation page it pushes.</param>
/// <param name="Title">Heading shown on the card and the page.</param>
/// <param name="Description">What the user finds there.</param>
/// <param name="Count">How many capabilities the section currently holds.</param>
/// <param name="Status">The most serious status among them.</param>
internal sealed record DeviceOverlaySectionEntry(
    DeviceOverlaySection Section,
    OverlayPage Page,
    string Title,
    string Description,
    int Count,
    DeviceOverlayStatus Status);

/// <summary>
/// Turns a Device snapshot into the section list the destination's root page shows.
/// </summary>
/// <remarks>
/// The Device destination is a menu of pages rather than one long scrolling list, because a
/// handheld's whole surface is a few rows tall and a list that needs scrolling is a list a
/// controller cannot navigate quickly.
/// <para>
/// A section appears only when the plugin published something for it. That keeps the menu honest on
/// every device — a handheld with no lighting shows no Lighting page rather than an empty one — and
/// it means no section here is a fixture that a future plugin has to satisfy.
/// </para>
/// </remarks>
internal static class DeviceOverlaySectionPages
{
    /// <summary>The fixed order sections are offered in.</summary>
    /// <remarks>
    /// Ordered by how often a handheld user reaches for them, not by the enum. Power comes first
    /// because it is the reason the Device page is opened mid-game; diagnostics comes last because
    /// it is the reason it is opened when something is wrong.
    /// </remarks>
    private static readonly DeviceOverlaySection[] Order =
    [
        DeviceOverlaySection.Overview,
        DeviceOverlaySection.PowerAndThermals,
        DeviceOverlaySection.Profiles,
        DeviceOverlaySection.ControllerAndMotion,
        DeviceOverlaySection.Oem,
        DeviceOverlaySection.LightingAndFeatures,
        DeviceOverlaySection.Glyphs,
        DeviceOverlaySection.Diagnostics,
    ];

    /// <summary>The page a section opens.</summary>
    /// <param name="section">The section.</param>
    /// <returns>Its navigation page.</returns>
    internal static OverlayPage PageFor(DeviceOverlaySection section) => section switch
    {
        DeviceOverlaySection.Overview => OverlayPage.DeviceOverview,
        DeviceOverlaySection.Profiles => OverlayPage.DeviceProfiles,
        DeviceOverlaySection.PowerAndThermals => OverlayPage.DevicePowerAndThermals,
        DeviceOverlaySection.ControllerAndMotion => OverlayPage.DeviceControllerAndMotion,
        DeviceOverlaySection.Oem => OverlayPage.DeviceOem,
        DeviceOverlaySection.LightingAndFeatures => OverlayPage.DeviceLightingAndFeatures,
        DeviceOverlaySection.Glyphs => OverlayPage.DeviceGlyphs,
        DeviceOverlaySection.Diagnostics => OverlayPage.DeviceDiagnostics,
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };

    /// <summary>The section a page belongs to, or null when the page is not a Device section.</summary>
    /// <param name="page">The navigation page.</param>
    /// <returns>Its section.</returns>
    internal static DeviceOverlaySection? SectionFor(OverlayPage page) => page switch
    {
        OverlayPage.DeviceOverview => DeviceOverlaySection.Overview,
        OverlayPage.DeviceProfiles => DeviceOverlaySection.Profiles,
        OverlayPage.DevicePowerAndThermals => DeviceOverlaySection.PowerAndThermals,
        OverlayPage.DeviceControllerAndMotion => DeviceOverlaySection.ControllerAndMotion,
        OverlayPage.DeviceOem => DeviceOverlaySection.Oem,
        OverlayPage.DeviceLightingAndFeatures => DeviceOverlaySection.LightingAndFeatures,
        OverlayPage.DeviceGlyphs => DeviceOverlaySection.Glyphs,
        OverlayPage.DeviceDiagnostics => DeviceOverlaySection.Diagnostics,
        _ => null,
    };

    /// <summary>The stable focus key for a section's card on the root page.</summary>
    /// <param name="section">The section.</param>
    /// <returns>Its focus key.</returns>
    internal static string FocusKey(DeviceOverlaySection section) =>
        "device.section." + section switch
        {
            DeviceOverlaySection.Overview => "overview",
            DeviceOverlaySection.Profiles => "profiles",
            DeviceOverlaySection.PowerAndThermals => "power",
            DeviceOverlaySection.ControllerAndMotion => "controller",
            DeviceOverlaySection.Oem => "oem",
            DeviceOverlaySection.LightingAndFeatures => "lighting",
            DeviceOverlaySection.Glyphs => "glyphs",
            DeviceOverlaySection.Diagnostics => "diagnostics",
            _ => "unknown",
        };

    /// <summary>Builds the section menu for a snapshot.</summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <returns>Sections that currently have something to show, in presentation order.</returns>
    internal static IReadOnlyList<DeviceOverlaySectionEntry> Build(DeviceOverlaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Dictionary<DeviceOverlaySection, int> counts = [];
        Dictionary<DeviceOverlaySection, DeviceOverlayStatus> statuses = [];
        foreach (DeviceOverlayCapability capability in snapshot.Capabilities)
        {
            counts[capability.Section] = counts.GetValueOrDefault(capability.Section) + 1;
            statuses[capability.Section] = MoreSerious(
                statuses.GetValueOrDefault(capability.Section, DeviceOverlayStatus.None),
                capability.Status);
        }

        // WSGM's own rows never appear in the capability list, so each has to be counted into its
        // section explicitly. Without this a section holding only a direct row has a count of zero
        // and is dropped from the menu, which makes the row unreachable — the case for AutoTDP on a
        // device that publishes no power capability, and for the controller target on any device,
        // since no plugin publishes one.
        void AddDirectRow(DeviceOverlaySection section, DeviceOverlayStatus status)
        {
            counts[section] = counts.GetValueOrDefault(section) + 1;
            statuses[section] = MoreSerious(
                statuses.GetValueOrDefault(section, DeviceOverlayStatus.None),
                status);
        }

        if (snapshot.GlyphSelection is { } glyphs)
        {
            AddDirectRow(DeviceOverlaySection.Glyphs, glyphs.Status);
        }

        if (snapshot.AutoTdp is { } autoTdp)
        {
            AddDirectRow(DeviceOverlaySection.PowerAndThermals, autoTdp.Status);
        }

        if (snapshot.Controller is { } controller)
        {
            AddDirectRow(DeviceOverlaySection.ControllerAndMotion, controller.Status);
        }

        if (snapshot.Recovery is { } recovery)
        {
            AddDirectRow(DeviceOverlaySection.Diagnostics, recovery.Status);
        }

        List<DeviceOverlaySectionEntry> entries = [];
        foreach (DeviceOverlaySection section in Order)
        {
            int count = counts.GetValueOrDefault(section);
            if (count == 0)
            {
                continue;
            }

            entries.Add(new DeviceOverlaySectionEntry(
                section,
                PageFor(section),
                TitleFor(section),
                DescriptionFor(section),
                count,
                statuses.GetValueOrDefault(section, DeviceOverlayStatus.None)));
        }

        return entries;
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
        List<DeviceOverlayCapability> matching = [];
        foreach (DeviceOverlayCapability capability in snapshot.Capabilities)
        {
            if (capability.Section == section)
            {
                matching.Add(capability);
            }
        }

        return matching;
    }

    /// <summary>Picks the status a section should advertise from those of its rows.</summary>
    /// <param name="left">One status.</param>
    /// <param name="right">The other.</param>
    /// <returns>The more serious of the two.</returns>
    /// <remarks>
    /// A section card shows the worst thing inside it. Showing the best, or the first, would let a
    /// faulted control hide behind a healthy one on a page the user has not opened.
    /// </remarks>
    internal static DeviceOverlayStatus MoreSerious(
        DeviceOverlayStatus left,
        DeviceOverlayStatus right) =>
        Severity(right) > Severity(left) ? right : left;

    private static int Severity(DeviceOverlayStatus status) => status switch
    {
        DeviceOverlayStatus.Faulted => 5,
        DeviceOverlayStatus.ExternallyOwned => 4,
        DeviceOverlayStatus.Warning => 3,
        DeviceOverlayStatus.Stale => 2,
        DeviceOverlayStatus.Unsupported => 1,
        _ => 0,
    };

    private static string TitleFor(DeviceOverlaySection section) => section switch
    {
        DeviceOverlaySection.Overview => "Overview",
        DeviceOverlaySection.Profiles => "Profiles",
        DeviceOverlaySection.PowerAndThermals => "Power and thermals",
        DeviceOverlaySection.ControllerAndMotion => "Controller and motion",
        DeviceOverlaySection.Oem => "OEM buttons",
        DeviceOverlaySection.LightingAndFeatures => "Lighting and features",
        DeviceOverlaySection.Glyphs => "Glyphs",
        DeviceOverlaySection.Diagnostics => "Diagnostics and recovery",
        _ => "Device",
    };

    private static string DescriptionFor(DeviceOverlaySection section) => section switch
    {
        DeviceOverlaySection.Overview => "Device identity and performance mode",
        DeviceOverlaySection.Profiles => "Named hardware profiles",
        DeviceOverlaySection.PowerAndThermals => "Power limits, fans, charging, and temperatures",
        DeviceOverlaySection.ControllerAndMotion => "Built-in controller, motion, and rumble",
        DeviceOverlaySection.Oem => "Device buttons and their assignments",
        DeviceOverlaySection.LightingAndFeatures => "Lighting and remaining device features",
        DeviceOverlaySection.Glyphs => "Button artwork, preview, and input test",
        DeviceOverlaySection.Diagnostics => "Health, readings, and recovery",
        _ => string.Empty,
    };
}
