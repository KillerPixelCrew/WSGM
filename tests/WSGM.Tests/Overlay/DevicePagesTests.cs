using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests.Overlay;

public sealed class DevicePagesTests
{
    private static readonly DeviceCoreControls NoControls = default;

    [Fact]
    public void OnlySectionsWithSomethingInThemAreOffered()
    {
        var snapshot = Snapshot(
            Placed("power.limit", DeviceSections.PowerId),
            Placed("oem.button", DeviceSections.ControllerId));

        // A handheld with no lighting shows no RGB page rather than an empty one.
        Assert.Equal([DeviceSections.PowerId, DeviceSections.ControllerId], Offered(snapshot));
    }

    [Fact]
    public void SectionsFollowThePublishedOrder()
    {
        var snapshot = Snapshot(Placed("fan", "cooling"), Placed("limit", "power")) with
        {
            PluginSections = [Section("cooling"), Section("power")]
        };

        Assert.Equal(["cooling", "power"], Offered(snapshot));
    }

    [Fact]
    public void ASectionCountsTheRowsItDraws()
    {
        var snapshot = Snapshot(
            Placed("a", DeviceSections.PowerId),
            Placed("b", DeviceSections.PowerId, DescriptorStatus.Faulted),
            Placed("c", DeviceSections.PowerId));

        Assert.Equal(3, Page(snapshot, DeviceSections.PowerId).Count);
    }

    [Fact]
    public void PowerCountsTheWindowsControlsItDrawsAndKeepsThemWithIntegrationOff()
    {
        var snapshot = Snapshot() with { Visible = false };

        Assert.Empty(Offered(snapshot));
        var controls = new DeviceCoreControls(true, true, true, true);
        var power = Page(snapshot, DeviceSections.PowerId, controls: controls);
        // The device views need integration; the Windows plan and core placement do not.
        Assert.Equal(2, power.Count);
        Assert.Equal([DevicePages.PowerFoldId, DevicePages.ProcessorCoresId], power.Groups.Select(group => group.Id));
        Assert.Equal([DevicePages.PowerProfileId], power.Groups[0].Groups.Select(group => group.Id));
    }

    [Fact]
    public void ThePowerPageFoldsWsgmViewsAndOpenGroupsTogetherAndFansOnTheirOwn()
    {
        var snapshot = Snapshot(
                Placed("limit", DeviceSections.PowerId) with { Role = CapabilityRole.PowerSustainedLimit },
                Placed("fan", DeviceSections.PowerId) with { Role = CapabilityRole.FanMode, CategoryId = "fans" }) with
            {
                PluginSections =
                [
                    new DeviceOverlayPluginSection(DeviceSections.PowerId, "Power", string.Empty, SectionIcon.Power,
                        [new DeviceOverlayCategory("fans", "Fans")])
                ]
            };

        var power = Page(snapshot, DeviceSections.PowerId, Performance(),
            new DeviceCoreControls(true, true, false, false));

        Assert.Equal(
        [
            (DevicePages.PowerFoldId, DeviceFold.Shared),
            ("section.device.plugin.power.category.fans", DeviceFold.Folded),
            (DevicePages.PerformanceId, DeviceFold.Folded)
        ], power.Groups.Select(group => (group.Id, group.Fold)));
        Assert.Null(power.Groups[0].PinTitle);
        Assert.Equal(
        [
            (DevicePages.PowerPresetsId, DeviceFold.Card),
            (DevicePages.PowerProfileId, DeviceFold.Card),
            ("section.device.plugin.power.main", DeviceFold.Open)
        ], power.Groups[0].Groups.Select(group => (group.Id, group.Fold)));
        // Presets, plan, limit, fan, then the performance rows without the Overview's toggle.
        Assert.Equal(2 + 2 + 2, power.Count);
    }

    [Fact]
    public void OtherSectionsFoldEveryGroupOnItsOwn()
    {
        var snapshot = Snapshot(Placed("led", DeviceSections.RgbId)) with
        {
            LightingProfile = new DescriptorRow(DeviceHostRowIds.LightingProfile, "Colours", "", "", true)
                { SectionId = DeviceSections.RgbId }
        };

        var rgb = Page(snapshot, DeviceSections.RgbId);

        Assert.All(rgb.Groups, group => Assert.Equal(DeviceFold.Folded, group.Fold));
        Assert.Equal(["section.device.plugin.rgb.main", "section.device.plugin.rgb.configuration"],
            rgb.Groups.Select(group => group.Id));
        Assert.Equal(DevicePages.RailKey(DeviceSections.RgbId), rgb.RailKey);
        Assert.Equal("device.section.plugin.rgb", rgb.RailKey);
    }

    [Fact]
    public void TheOverviewIsTheApplicationToggleAboveThePowerPagesGroups()
    {
        var snapshot = Snapshot(Placed("limit", DeviceSections.PowerId));
        var pages = DevicePages.Build(snapshot, Performance(), new DeviceCoreControls(false, true, false, false));

        var overview = pages[0];
        var power = pages.Single(page => page.SectionId == DeviceSections.PowerId);
        Assert.Equal(DevicePages.OverviewKey, overview.RailKey);
        Assert.Equal(("Device", "Ready"), overview.Heading);
        var toggle = Assert.IsType<DeviceRow.Performance>(Assert.Single(overview.Groups[0].Rows));
        Assert.Equal(DevicePages.ApplicationProfileKey, toggle.Key);
        Assert.Equal(DeviceFold.Plain, overview.Groups[0].Fold);
        Assert.Equal(power.Groups, overview.Groups.Skip(1));
        // The toggle heads the Overview alone; the Power page does not repeat it.
        Assert.DoesNotContain(power.AllGroups.SelectMany(group => group.Rows),
            row => row is DeviceRow.Performance { Value.Id: PerformanceOverlaySnapshot.ApplicationProfileRowId });
    }

    [Fact]
    public void WithIntegrationOffTheOverviewKeepsWindowsControlsButNoDeviceRowsOrHeading()
    {
        var snapshot = Snapshot(Placed("limit", DeviceSections.PowerId)) with { Visible = false };

        var overview = DevicePages.Build(snapshot, null, new DeviceCoreControls(true, true, true, false))[0];

        Assert.Null(overview.Heading);
        var fold = Assert.Single(overview.Groups);
        Assert.Equal([DevicePages.PowerProfileId], fold.Groups.Select(group => group.Id));
        Assert.DoesNotContain(overview.AllGroups.SelectMany(group => group.Rows), row => row is DeviceRow.Capability);
    }

    [Fact]
    public void TheControllerConfigurationIsOneGroupOnThePageAndOnQuickAccess()
    {
        var snapshot = Snapshot() with
        {
            GlyphSelection = new DescriptorRow(DeviceHostRowIds.GlyphSelection, "Glyphs", "", "", true)
                { SectionId = DeviceSections.ControllerId }
        };

        var page = Page(snapshot, DeviceSections.ControllerId);
        var group = Assert.Single(page.Groups);
        const string id = "section.device.plugin.controller.configuration";
        Assert.Equal(id, group.Id);
        Assert.True(page.ObservesInput);
        // The page says why controller output is missing; the pinned copy carries only the controls.
        Assert.IsType<DeviceRow.Text>(group.Rows[0]);
        Assert.Equal(1, page.Count);
        var pinned = DevicePages.Pinned(id, snapshot, null, null, _ => false);
        Assert.NotNull(pinned);
        Assert.Equal(DeviceHostRowIds.GlyphSelection, Assert.Single(pinned.Rows).Key);
        Assert.Contains((id, "Configuration"), DevicePages.PinChoices(DevicePages.Build(snapshot, null, NoControls),
            DevicePages.Gpu(GraphicsOverlaySnapshot.Empty)));
    }

    [Fact]
    public void HostRowsArePassedOnUnchanged()
    {
        var autoTdp = new DescriptorRow(DeviceHostRowIds.AutoTdp, "AutoTDP", "", "", true)
        {
            SectionId = DeviceSections.PowerId, OverrideId = "AutoTdp"
        };
        var snapshot = Snapshot() with { AutoTdp = autoTdp };

        var row = Page(snapshot, DeviceSections.PowerId).AllGroups.SelectMany(group => group.Rows)
            .OfType<DeviceRow.Host>().Single();

        Assert.Same(autoTdp, row.Value);
    }

    [Fact]
    public void PinChoicesFollowPageOrderAndSkipTheOverview()
    {
        var snapshot = Snapshot(Placed("led", DeviceSections.RgbId), Placed("limit", DeviceSections.PowerId));

        var choices = DevicePages.PinChoices(DevicePages.Build(snapshot, Performance(),
            new DeviceCoreControls(false, true, false, false)), DevicePages.Gpu(GraphicsOverlaySnapshot.Empty));

        Assert.Equal(
        [
            DevicePages.PowerProfileId, "section.device.plugin.power.main", DevicePages.PerformanceId,
            "section.device.plugin.rgb.main"
        ], choices.Select(choice => choice.Id));
    }

    [Fact]
    public void APinnedIdNothingPublishesDrawsAsUnavailable()
    {
        var group = DevicePages.Pinned("section.device.plugin.gone.main", Snapshot(), null, null, _ => false);

        Assert.NotNull(group);
        Assert.Equal("Section unavailable", group.Title);
        Assert.Equal(DeviceFold.Folded, group.Fold);
        Assert.Null(DevicePages.Pinned("program:x", Snapshot(), null, null, _ => false));
    }

    [Fact]
    public void TheGpuPageSaysWhyItIsEmpty()
    {
        var empty = DevicePages.Gpu(GraphicsOverlaySnapshot.Empty);

        var text = Assert.IsType<DeviceRow.Text>(Assert.Single(Assert.Single(empty.Groups).Rows));
        Assert.Contains("plugin is enabled", text.Value);
        Assert.Equal(0, empty.Count);
    }

    private static IEnumerable<string?> Offered(DeviceOverlaySnapshot snapshot)
    {
        return DevicePages.Build(snapshot, null, NoControls)
            .Where(page => page.SectionId is not null && page.Count > 0)
            .Select(page => page.SectionId);
    }

    private static DevicePage Page(DeviceOverlaySnapshot snapshot, string sectionId,
        PerformanceOverlaySnapshot? performance = null, DeviceCoreControls controls = default)
    {
        return DevicePages.Build(snapshot, performance, controls).Single(page => page.SectionId == sectionId);
    }

    private static PerformanceOverlaySnapshot Performance()
    {
        return new PerformanceOverlaySnapshot(true, "Ready",
            [new DescriptorRow("frame-limit", "Frame limit", string.Empty, "Off", true)],
            [
                new DescriptorRow(PerformanceOverlaySnapshot.ApplicationProfileRowId, "Per-application",
                    string.Empty, "Off", true),
                new DescriptorRow("reset-profile", "Reset", string.Empty, "Reset", true)
            ]);
    }

    private static DeviceOverlaySnapshot Snapshot(params DeviceOverlayCapability[] capabilities)
    {
        return new DeviceOverlaySnapshot(true, "Device", "Ready", null, capabilities);
    }

    private static DeviceOverlayPluginSection Section(string id)
    {
        return new DeviceOverlayPluginSection(id, id, string.Empty, SectionIcon.None, [])
            { Key = id == "power" ? SettingSectionKey.Power : SettingSectionKey.Custom };
    }

    private static DeviceOverlayCapability Placed(string id, string sectionId,
        DescriptorStatus status = DescriptorStatus.Available)
    {
        return new DeviceOverlayCapability(id, null, status, id, id, string.Empty, true, NextValue: null)
        {
            PluginSectionId = sectionId
        };
    }
}
