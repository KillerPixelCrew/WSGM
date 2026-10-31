using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests.Shell.Device;

public sealed class DeviceOverlayGroupsTests
{
    [Fact]
    public void ASectionSplitsIntoLeadCategoryAndConfigurationGroupsUnderStablePinIds()
    {
        var snapshot = Snapshot(
                Placed("lead", "custom"),
                Placed("cat", "custom") with { CategoryId = "extra" }) with
            {
                PluginSections =
                [
                    new DeviceOverlayPluginSection("custom", "Custom", string.Empty, SectionIcon.None,
                        [new DeviceOverlayCategory("extra", "Extra")])
                ],
                LightingProfile = new DescriptorRow(DeviceHostRowIds.LightingProfile, "Colours", "", "", true)
                    { SectionId = "custom" }
            };

        var groups = DeviceOverlayGroups.Build(snapshot);

        Assert.Equal(
        [
            ("section.device.plugin.custom.main", "Custom", true),
            ("section.device.plugin.custom.category.extra", "Extra", true),
            ("section.device.plugin.custom.configuration", "Configuration", true)
        ], groups.Select(group => (group.Id, group.Title, group.OwnFold)));
        Assert.Equal(["lead"], groups[0].Capabilities.Select(capability => capability.CapabilityId));
        Assert.Equal([DeviceHostRowIds.LightingProfile], groups[2].HostRows.Select(row => row.Id));
    }

    [Fact]
    public void PowerLeadsWithItsLimitsPutsFansFirstAndFoldsOnlyFansOnTheirOwn()
    {
        var snapshot = Snapshot(
                Placed("scenario", DeviceSections.PowerId) with { Role = CapabilityRole.ScenarioMode },
                Placed("pl2", DeviceSections.PowerId) with
                {
                    Role = CapabilityRole.PowerSlowLimit, CategoryId = "limits"
                },
                Placed("pl1", DeviceSections.PowerId) with
                {
                    Role = CapabilityRole.PowerSustainedLimit, CategoryId = "limits"
                },
                Placed("charge", DeviceSections.PowerId) with { CategoryId = "charging" },
                Placed("fan", DeviceSections.PowerId) with { Role = CapabilityRole.FanMode, CategoryId = "fans" }) with
            {
                PluginSections =
                [
                    new DeviceOverlayPluginSection(DeviceSections.PowerId, "Power", string.Empty, SectionIcon.Power,
                    [
                        new DeviceOverlayCategory("limits", "Limits"),
                        new DeviceOverlayCategory("charging", "Charging"),
                        new DeviceOverlayCategory("fans", "Fans")
                    ])
                ],
                AutoTdp = new DescriptorRow(DeviceHostRowIds.AutoTdp, "AutoTDP", "", "", true)
                    { SectionId = DeviceSections.PowerId }
            };

        var groups = DeviceOverlayGroups.Build(snapshot);

        Assert.Equal(
        [
            ("section.device.plugin.power.main", "Manual power and display", false),
            ("section.device.plugin.power.category.fans", "Fans", true),
            ("section.device.plugin.power.category.charging", "Charging", false),
            ("section.device.plugin.power.configuration", "Automatic control and saved profiles", false)
        ], groups.Select(group => (group.Id, group.Title, group.OwnFold)));
        Assert.Equal(["scenario", "pl1", "pl2"], groups[0].Capabilities.Select(capability => capability.CapabilityId));
    }

    [Fact]
    public void TheControllerPageLeadsWithItsConfiguration()
    {
        var snapshot = Snapshot(Placed("rumble", DeviceSections.ControllerId)) with
        {
            GlyphSelection = new DescriptorRow(DeviceHostRowIds.GlyphSelection, "Glyphs", "", "", true)
                { SectionId = DeviceSections.ControllerId }
        };

        Assert.Equal(["section.device.plugin.controller.configuration", "section.device.plugin.controller.main"],
            DeviceOverlayGroups.Build(snapshot).Select(group => group.Id));
    }

    [Fact]
    public void ALayoutPairsCompanionFollowsIt()
    {
        var snapshot = Snapshot(
            Placed("a", DeviceSections.InfoId) with
            {
                SortOrder = 1, LayoutPair = new CapabilityLayoutPair("c")
            },
            Placed("b", DeviceSections.InfoId) with { SortOrder = 2 },
            Placed("c", DeviceSections.InfoId) with { SortOrder = 3 });

        Assert.Equal(["a", "c", "b"],
            Assert.Single(DeviceOverlayGroups.Build(snapshot)).Capabilities.Select(row => row.CapabilityId));
    }

    [Fact]
    public void ASectionPageShowsOnlyItsOwnRowsInPlacementOrder()
    {
        var snapshot = Snapshot(
            Placed("late", DeviceSections.PowerId) with { SortOrder = 1 },
            Placed("oem", DeviceSections.ControllerId),
            Placed("leadA", DeviceSections.PowerId),
            Placed("leadB", DeviceSections.PowerId));

        Assert.Equal(
            ["leadA", "leadB", "late"],
            DeviceOverlayGroups.CapabilitiesIn(snapshot, DeviceSections.PowerId)
                .Select(capability => capability.CapabilityId));
    }

    [Fact]
    public void WithIntegrationOffOnlyHostRowsAreGrouped()
    {
        var snapshot = Snapshot(Placed("limit", DeviceSections.PowerId)) with
        {
            Visible = false,
            GlyphSelection = new DescriptorRow(DeviceHostRowIds.GlyphSelection, "Glyphs", "", "", true)
                { SectionId = DeviceSections.ControllerId }
        };

        Assert.Empty(DeviceOverlayGroups.CapabilitiesIn(snapshot, DeviceSections.PowerId));
        var group = Assert.Single(DeviceOverlayGroups.Build(snapshot));
        Assert.Equal("section.device.plugin.controller.configuration", group.Id);
        Assert.Single(DeviceOverlayGroups.HostRowsIn(snapshot, DeviceSections.ControllerId));
        Assert.Empty(DeviceOverlayGroups.HostRowsIn(snapshot, DeviceSections.PowerId));
    }

    private static DeviceOverlaySnapshot Snapshot(params DeviceOverlayCapability[] capabilities)
    {
        return new DeviceOverlaySnapshot(true, "Device", "Ready", null, capabilities);
    }

    private static DeviceOverlayCapability Placed(string id, string sectionId)
    {
        return new DeviceOverlayCapability(id, null, DescriptorStatus.Available, id, id, string.Empty, true,
            NextValue: null) { PluginSectionId = sectionId };
    }
}
