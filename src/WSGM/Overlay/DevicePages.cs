using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     Describes the Device destination's pages, the GPU page, the Tools performance page and every Quick
///     Access group from the snapshots, once per refresh.
/// </summary>
/// <remarks>
///     The Device destination is a menu of pages rather than one long scrolling list, because a handheld's
///     whole surface is a few rows tall. Shell publishes the pages and their groups
///     (<see cref="DeviceOverlayGroups" />); this adds the WSGM views the Power page carries, the shared
///     performance rows and the Overview, which is the per-application toggle above the Power page's
///     groups. One renderer draws every description, so a group looks and behaves the same wherever it is.
/// </remarks>
internal static class DevicePages
{
    /// <summary>The Overview's rail key.</summary>
    internal const string OverviewKey = "device.overview";

    /// <summary>The GPU page's rail key.</summary>
    internal const string GpuKey = "device.gpu";

    internal const string DriverUpdatesId = "section.gpu.driver-updates";

    /// <summary>The focus key of the per-application toggle where it heads the Overview.</summary>
    internal const string ApplicationProfileKey = "device.application-profile";

    /// <summary>The expansion id of the Power page's shared fold.</summary>
    internal const string PowerFoldId = "section.device.unified-power";

    /// <summary>The pin id of the shared performance group.</summary>
    internal const string PerformanceId = "section.performance";

    /// <summary>The pin id of the AC and battery power assignments.</summary>
    internal const string PowerPresetsId = "section.device.power-presets";

    /// <summary>The pin id of the manual power mode selector.</summary>
    internal const string ManualTdpId = "section.device.manual-tdp";

    /// <summary>The pin id of the Windows energy plan.</summary>
    internal const string PowerProfileId = "section.system.power-profile";

    /// <summary>The pin id of the hybrid core placement.</summary>
    internal const string ProcessorCoresId = "section.system.processor-cores";

    /// <summary>The pin id of the display brightness and mode.</summary>
    internal const string DisplayId = "section.display";

    /// <summary>The headings of WSGM's composite views, by pin id.</summary>
    internal static readonly IReadOnlyDictionary<string, string> ViewTitles = new Dictionary<string, string>
    {
        [PowerPresetsId] = "Power assignments",
        [ManualTdpId] = "Manual power mode",
        [PowerProfileId] = "Windows energy plan",
        [ProcessorCoresId] = "Processor cores",
        [DisplayId] = "Display"
    };

    /// <summary>The rail key of a published section's page.</summary>
    /// <param name="sectionId">The section.</param>
    /// <returns>Its key, <c>device.section.plugin.&lt;id&gt;</c>.</returns>
    internal static string RailKey(string sectionId)
    {
        return new SectionKey("plugin." + sectionId).RailKey;
    }

    /// <summary>Describes the Overview and every published section's page.</summary>
    /// <param name="device">The Device snapshot.</param>
    /// <param name="performance">The shared performance rows, or null without a performance source.</param>
    /// <param name="controls">Which composite power views have something to show.</param>
    /// <returns>The Overview, then one page per published section, with or without rows.</returns>
    internal static IReadOnlyList<DevicePage> Build(DeviceOverlaySnapshot device,
        PerformanceOverlaySnapshot? performance, DeviceCoreControls controls)
    {
        ArgumentNullException.ThrowIfNull(device);
        var groups = DeviceOverlayGroups.Build(device);
        List<DevicePage> sections = [];
        foreach (var section in device.PluginSections)
        {
            var own = groups.Where(group => group.SectionId == section.SectionId).ToArray();
            var pageGroups = section.SectionId == DeviceSections.PowerId
                ? PowerGroups(device, own, performance, controls)
                : [.. own.Select(group => FromShell(device, group, DeviceFold.Folded, false))];
            sections.Add(new DevicePage(RailKey(section.SectionId), section.Title, section.Icon, section.SectionId,
                pageGroups)
            {
                ObservesInput = device.HostRows.Any(row => row.SectionId == section.SectionId
                                                           && row.Id is DeviceHostRowIds.ControllerTarget
                                                               or DeviceHostRowIds.GlyphSelection)
            });
        }

        // The Overview is where a handheld user lands mid-game, so it carries what they came for: the
        // per-application switch, then the Power page's groups under the same ids and keys.
        List<DeviceGroup> overview = [];
        if (performance is { Visible: true, ApplicationProfile: { } toggle })
        {
            overview.Add(new DeviceGroup("device.headline", string.Empty, null, DeviceFold.Plain,
                [new DeviceRow.Performance(toggle, ApplicationProfileKey)]));
        }

        if (sections.FirstOrDefault(page => page.SectionId == DeviceSections.PowerId) is { } power)
        {
            overview.AddRange(power.Groups);
        }

        return
        [
            new DevicePage(OverviewKey, "Overview", SectionIcon.None, null, overview)
            {
                Heading = device.Visible ? (device.Status, device.Detail) : null
            },
            .. sections
        ];
    }

    /// <summary>The page shown for a section that is no longer published while it is open.</summary>
    /// <param name="sectionId">The section.</param>
    /// <returns>A page that says so, rather than an empty one that cannot explain itself.</returns>
    internal static DevicePage Missing(string sectionId)
    {
        return new DevicePage(RailKey(sectionId), sectionId, SectionIcon.None, sectionId,
        [
            new DeviceGroup("device.missing", string.Empty, null, DeviceFold.Plain,
                [new DeviceRow.Text("device.missing.text", "This device section is no longer available.")])
        ]);
    }

    /// <summary>Describes the GPU page: each graphics section's groups, or why there are none.</summary>
    /// <param name="graphics">The graphics snapshot.</param>
    /// <param name="registeredViews">Composite views registered by the session.</param>
    /// <returns>The page.</returns>
    internal static DevicePage Gpu(GraphicsOverlaySnapshot graphics, IReadOnlyCollection<string>? registeredViews = null)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        List<DeviceGroup> groups =
        [
            .. graphics.Sections.Select(section => new DeviceGroup(GraphicsSectionPins.Id(section), section.Title,
                null, DeviceFold.Section, [])
            {
                Groups = [.. GraphicsSectionPins.Groups(section).Select(Graphics)],
                Spacing = 12
            })
        ];
        if (groups.Count == 0)
        {
            groups.Add(new DeviceGroup("device.gpu.empty", string.Empty, null, DeviceFold.Plain,
            [
                new DeviceRow.Text("device.gpu.empty.text", graphics.Visible
                    ? "The graphics driver has not published any settings yet."
                    : "GPU controls will appear here when a supported Intel, NVIDIA or AMD plugin is enabled.")
            ]));
        }

        if (registeredViews?.Contains(DriverUpdatesId) == true)
        {
            groups.Insert(0, new DeviceGroup(DriverUpdatesId, "Driver updates", null, DeviceFold.Section,
                [new DeviceRow.View(DriverUpdatesId)]));
        }

        var note = string.Join(" ", graphics.Publishers
            .Where(publisher => publisher.Note is not null)
            .Select(publisher => publisher.Note));
        return new DevicePage(GpuKey, "GPU", SectionIcon.Display, null, groups) { Note = note };
    }

    /// <summary>The shared performance group.</summary>
    /// <param name="performance">The performance snapshot.</param>
    /// <param name="withToggle">Whether it carries the per-application toggle, which otherwise heads the Overview.</param>
    /// <param name="pinned">Whether it is the Quick Access copy.</param>
    /// <returns>The group.</returns>
    internal static DeviceGroup Performance(PerformanceOverlaySnapshot performance, bool withToggle, bool pinned)
    {
        ArgumentNullException.ThrowIfNull(performance);
        var rows = withToggle ? performance.ProfileRows.Concat(performance.Rows) : performance.PageRows;
        return new DeviceGroup(PerformanceId, "Performance", "Performance", DeviceFold.Folded,
            [.. rows.Select(row => new DeviceRow.Performance(row, "performance." + row.Id))])
        {
            Spacing = pinned ? 8 : 4,
            Summary = pinned ? null : performance.Status
        };
    }

    /// <summary>Every group a Device page offers to pin, in page order.</summary>
    /// <param name="pages">The section pages; the Overview repeats Power and is skipped.</param>
    /// <param name="gpu">The GPU page.</param>
    /// <returns>Each pinnable group's id and pin title, once.</returns>
    internal static IReadOnlyList<(string Id, string Title)> PinChoices(IEnumerable<DevicePage> pages, DevicePage gpu)
    {
        return
        [
            .. pages.Where(page => page.RailKey != OverviewKey).Append(gpu)
                .SelectMany(page => page.AllGroups)
                .Where(group => group.PinTitle is not null)
                .Select(group => (group.Id, Title: group.PinTitle!))
                .DistinctBy(choice => choice.Id)
        ];
    }

    /// <summary>The Quick Access copy of a pinned group, drawn live from the current snapshots.</summary>
    /// <param name="id">The pin id.</param>
    /// <param name="device">The Device snapshot, or null without a device source.</param>
    /// <param name="performance">The performance snapshot, or null without one.</param>
    /// <param name="graphics">The graphics snapshot, or null without one.</param>
    /// <param name="views">Whether a composite view of that id is registered.</param>
    /// <returns>The group, or null when nothing publishes the id now (see <see cref="Unavailable" />).</returns>
    internal static DeviceGroup? Pinned(string id, DeviceOverlaySnapshot? device,
        PerformanceOverlaySnapshot? performance, GraphicsOverlaySnapshot? graphics, Func<string, bool> views)
    {
        if (views(id) && ViewTitles.TryGetValue(id, out var title))
        {
            return new DeviceGroup(id, title, title, DeviceFold.Folded, [new DeviceRow.View(id)]);
        }

        if (id == PerformanceId && performance is { Visible: true })
        {
            return Performance(performance, true, true);
        }

        if (GraphicsSectionPins.Resolve(graphics, id) is { } pin)
        {
            return Graphics(pin);
        }

        if (device is not null && DeviceOverlayGroups.Build(device).FirstOrDefault(group => group.Id == id) is
                { } section)
        {
            return FromShell(device, section, DeviceFold.Folded, true);
        }

        return null;
    }

    /// <summary>The Quick Access copy of a pinned group that nothing publishes now.</summary>
    /// <param name="id">The pin id.</param>
    /// <returns>A group that says its controls will return for a <c>section.</c> id; null for any other id.</returns>
    internal static DeviceGroup? Unavailable(string id)
    {
        return id.StartsWith("section.", StringComparison.Ordinal)
            ? new DeviceGroup(id, "Section unavailable", "Section unavailable", DeviceFold.Folded,
                [new DeviceRow.Text(id + ".unavailable", "Its controls will return when the provider is available.")])
            : null;
    }

    /// <summary>The Power page: its shared fold of WSGM views and open groups, its own folds, performance, cores.</summary>
    private static IReadOnlyList<DeviceGroup> PowerGroups(DeviceOverlaySnapshot device,
        IReadOnlyList<DeviceOverlayGroup> groups, PerformanceOverlaySnapshot? performance,
        DeviceCoreControls controls)
    {
        // AC and battery assignments lead, as in Steam's Quick Access; the Windows plan follows them.
        List<DeviceGroup> shared = [];
        if (device.Visible && controls.PowerPresets)
        {
            shared.Add(Card(PowerPresetsId, 0));
        }

        if (controls.WindowsPlan)
        {
            shared.Add(Card(PowerProfileId, 8));
        }

        if (device.Visible && controls.ManualTdp)
        {
            shared.Add(Card(ManualTdpId, 0));
        }

        shared.AddRange(groups.Where(group => !group.OwnFold)
            .Select(group => FromShell(device, group, DeviceFold.Open, false)));
        List<DeviceGroup> page = [];
        if (shared.Count > 0)
        {
            page.Add(new DeviceGroup(PowerFoldId, "Power", null, DeviceFold.Shared, [])
                { Groups = shared, Spacing = 12 });
        }

        page.AddRange(groups.Where(group => group.OwnFold)
            .Select(group => FromShell(device, group, DeviceFold.Folded, false)));
        if (performance is { Visible: true } && performance.SectionId == DeviceSections.PowerId)
        {
            page.Add(Performance(performance, false, false));
        }

        // Windows policy like the energy plan, so it stays available with device integration off.
        if (controls.ProcessorCores)
        {
            var title = ViewTitles[ProcessorCoresId];
            page.Add(new DeviceGroup(ProcessorCoresId, title, title, DeviceFold.Folded,
                [new DeviceRow.View(ProcessorCoresId)]));
        }

        return page;

        static DeviceGroup Card(string id, double spacing)
        {
            var title = ViewTitles[id];
            return new DeviceGroup(id, title, title, DeviceFold.Card, [new DeviceRow.View(id)]) { Spacing = spacing };
        }
    }

    /// <summary>One of Shell's Device groups as rows: its host rows, then its capabilities.</summary>
    private static DeviceGroup FromShell(DeviceOverlaySnapshot device, DeviceOverlayGroup group, DeviceFold fold,
        bool pinned)
    {
        List<DeviceRow> rows = [];
        var glyphs = group.HostRows.Any(row => row.Id == DeviceHostRowIds.GlyphSelection);
        if (!pinned && glyphs && device.Controller is null)
        {
            // In the controller row's place, so the page says why the control is missing.
            rows.Add(new DeviceRow.Text("device.controller-target.unavailable", device.Visible
                ? "Controller output is unavailable for this device."
                : "Device integration is off. Enable it in WSGM Settings to configure controller output."));
        }

        foreach (var row in group.HostRows)
        {
            rows.Add(new DeviceRow.Host(row, device.HostSelections.GetValueOrDefault(row.Id)));
            // After the selection row it is the result of, so changing the selection and seeing what it
            // produced reads top to bottom.
            if (!pinned && row.Id == DeviceHostRowIds.GlyphSelection && device.GlyphPreview is { } preview)
            {
                rows.Add(new DeviceRow.GlyphPreview(preview));
            }
        }

        rows.AddRange(group.Capabilities.Select(capability =>
            new DeviceRow.Capability(capability, false, device.FanTemperature)));
        return new DeviceGroup(group.Id, group.Title, group.Title, fold, rows);
    }

    private static DeviceGroup Graphics(GraphicsPinSection group)
    {
        return new DeviceGroup(group.Id, group.PinTitle, group.PinTitle, DeviceFold.Folded,
            [.. group.Rows.Select(row => new DeviceRow.Capability(row, true))]);
    }
}
