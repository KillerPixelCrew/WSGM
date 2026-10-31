using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests.Overlay;

public sealed class DeviceOverlaySectionPagesTests
{
    [Fact]
    public void OnlySectionsWithSomethingInThemAreOffered()
    {
        var snapshot = Snapshot(
            Placed("power.limit", DeviceSections.PowerId),
            Placed("oem.button", DeviceSections.ControllerId));

        var entries = DeviceOverlaySectionPages.Build(snapshot);

        // A handheld with no lighting shows no RGB page rather than an empty one.
        Assert.Equal(
            [DeviceSections.PowerId, DeviceSections.ControllerId],
            entries.Select(entry => entry.SectionId));
    }

    [Fact]
    public void SectionsFollowThePublishedOrder()
    {
        var snapshot = Snapshot(
                Placed("fan", "cooling"),
                Placed("limit", "power")) with
            {
                PluginSections = [Section("cooling"), Section("power")]
            };

        Assert.Equal(["cooling", "power"], DeviceOverlaySectionPages.Build(snapshot).Select(entry => entry.SectionId));
    }

    [Fact]
    public void ASectionCountsTheRowsItDraws()
    {
        var snapshot = Snapshot(
            Placed("a", DeviceSections.PowerId),
            Placed("b", DeviceSections.PowerId, DescriptorStatus.Faulted),
            Placed("c", DeviceSections.PowerId));

        Assert.Equal(3, Assert.Single(DeviceOverlaySectionPages.Build(snapshot)).Count);
    }

    [Fact]
    public void PowerCountsTheWindowsPowerControlsItDraws()
    {
        var snapshot = Snapshot();

        Assert.Empty(DeviceOverlaySectionPages.Build(snapshot));
        var power = Assert.Single(DeviceOverlaySectionPages.Build(snapshot, powerControls: 2));
        Assert.Equal(DeviceSections.PowerId, power.SectionId);
        Assert.Equal(2, power.Count);
    }

    [Fact]
    public void AHostRowCountsOnTheSectionShellGaveIt()
    {
        var snapshot = Snapshot() with
        {
            GlyphSelection = new DescriptorRow(
                DeviceHostRowIds.GlyphSelection,
                "Glyphs",
                "Automatic",
                "AUTO",
                true,
                DescriptorStatus.Available) { SectionId = DeviceSections.ControllerId }
        };

        var entry = Assert.Single(DeviceOverlaySectionPages.Build(snapshot));

        Assert.Equal(DeviceSections.ControllerId, entry.SectionId);
        Assert.Equal(1, entry.Count);
        Assert.Single(DeviceOverlaySectionPages.HostRowsIn(snapshot, DeviceSections.ControllerId));
        Assert.Empty(DeviceOverlaySectionPages.HostRowsIn(snapshot, DeviceSections.PowerId));
    }

    [Fact]
    public void PerformanceRowsCountOnTheirSectionExceptTheApplicationToggle()
    {
        PerformanceOverlaySnapshot performance = new(true, "Ready",
            [new DescriptorRow("frame-limit", "Frame limit", string.Empty, "Off", true)],
            [
                new DescriptorRow(DeviceOverlaySectionPages.ApplicationProfileRowId, "Per-application", string.Empty,
                    "Off", true),
                new DescriptorRow("reset-profile", "Reset", string.Empty, "Reset", true)
            ]);

        var entry = Assert.Single(DeviceOverlaySectionPages.Build(Snapshot(), performance));

        Assert.Equal(performance.SectionId, entry.SectionId);
        Assert.Equal(2, entry.Count);
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
            DeviceOverlaySectionPages.CapabilitiesIn(snapshot, DeviceSections.PowerId)
                .Select(capability => capability.CapabilityId));
    }

    [Fact]
    public void NoCapabilityIsDrawnWhileIntegrationIsOff()
    {
        var snapshot = Snapshot(Placed("limit", DeviceSections.PowerId)) with { Visible = false };

        Assert.Empty(DeviceOverlaySectionPages.CapabilitiesIn(snapshot, DeviceSections.PowerId));
        Assert.Empty(DeviceOverlaySectionPages.Build(snapshot));
    }

    [Fact]
    public void ARailKeyNamesItsSection()
    {
        DeviceOverlaySectionEntry entry = new(DeviceSections.PowerId, "Power", SectionIcon.Power, 1);

        Assert.Equal("device.section.plugin.power", DeviceOverlaySectionPages.FocusKey(entry));
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

    private static DeviceOverlayCapability Placed(
        string id,
        string sectionId,
        DescriptorStatus status = DescriptorStatus.Available)
    {
        return new DeviceOverlayCapability(id, null, status, id, id, string.Empty, true, NextValue: null)
        {
            PluginSectionId = sectionId
        };
    }
}
