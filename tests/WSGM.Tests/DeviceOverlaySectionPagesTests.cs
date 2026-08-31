using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DeviceOverlaySectionPagesTests
{
    [Fact]
    public void OnlySectionsWithSomethingInThemAreOffered()
    {
        DeviceOverlaySnapshot snapshot = Snapshot(
            Capability("power.limit", DeviceOverlaySection.PowerAndThermals),
            Capability("oem.button", DeviceOverlaySection.Oem));

        IReadOnlyList<DeviceOverlaySectionEntry> entries =
            DeviceOverlaySectionPages.Build(snapshot);

        // A handheld with no lighting shows no Lighting page rather than an empty one.
        Assert.Equal(
            [DeviceOverlaySection.PowerAndThermals, DeviceOverlaySection.Oem],
            entries.Select(entry => entry.Section));
    }

    [Fact]
    public void SectionsAreOfferedInReachOrderNotEnumOrder()
    {
        DeviceOverlaySnapshot snapshot = Snapshot(
            Capability("diag", DeviceOverlaySection.Diagnostics),
            Capability("light", DeviceOverlaySection.LightingAndFeatures),
            Capability("power", DeviceOverlaySection.PowerAndThermals),
            Capability("overview", DeviceOverlaySection.Overview));

        IReadOnlyList<DeviceOverlaySectionEntry> entries =
            DeviceOverlaySectionPages.Build(snapshot);

        Assert.Equal(
            [
                DeviceOverlaySection.Overview,
                DeviceOverlaySection.PowerAndThermals,
                DeviceOverlaySection.LightingAndFeatures,
                DeviceOverlaySection.Diagnostics,
            ],
            entries.Select(entry => entry.Section));
    }

    [Fact]
    public void ASectionCardCountsItsRows()
    {
        DeviceOverlaySnapshot snapshot = Snapshot(
            Capability("a", DeviceOverlaySection.PowerAndThermals),
            Capability("b", DeviceOverlaySection.PowerAndThermals),
            Capability("c", DeviceOverlaySection.PowerAndThermals));

        Assert.Equal(3, Assert.Single(DeviceOverlaySectionPages.Build(snapshot)).Count);
    }

    [Fact]
    public void ASectionCardShowsTheMostSeriousStatusInside()
    {
        DeviceOverlaySnapshot snapshot = Snapshot(
            Capability("ok", DeviceOverlaySection.PowerAndThermals, DescriptorStatus.Available),
            Capability("bad", DeviceOverlaySection.PowerAndThermals, DescriptorStatus.Faulted),
            Capability("warn", DeviceOverlaySection.PowerAndThermals, DescriptorStatus.Warning));

        // A fault must not hide behind a healthy row on a page the user has not opened.
        Assert.Equal(
            DescriptorStatus.Faulted,
            Assert.Single(DeviceOverlaySectionPages.Build(snapshot)).Status);
    }

    [Fact]
    public void SeverityOrderIsStable()
    {
        // Worst first. Each is more serious than everything after it, in both argument orders.
        DescriptorStatus[] descending =
        [
            DescriptorStatus.Faulted,
            DescriptorStatus.ExternallyOwned,
            DescriptorStatus.Warning,
            DescriptorStatus.Stale,
            DescriptorStatus.Unsupported,
            DescriptorStatus.Available,
        ];

        for (int worse = 0; worse < descending.Length; worse++)
        {
            for (int better = worse + 1; better < descending.Length; better++)
            {
                Assert.Equal(
                    descending[worse],
                    DeviceOverlaySectionPages.MoreSerious(descending[better], descending[worse]));
                Assert.Equal(
                    descending[worse],
                    DeviceOverlaySectionPages.MoreSerious(descending[worse], descending[better]));
            }
        }
    }

    [Fact]
    public void GlyphSelectionGivesTheGlyphsSectionAPageOfItsOwn()
    {
        DeviceOverlaySnapshot snapshot = Snapshot() with
        {
            GlyphSelection = new DescriptorRow(
                "device.glyph-selection",
                "Glyphs",
                "Automatic",
                "AUTO",
                CanInvoke: true,
                DescriptorStatus.Available),
        };

        DeviceOverlaySectionEntry entry = Assert.Single(DeviceOverlaySectionPages.Build(snapshot));

        // It is WSGM's own control, not a plugin capability, so it never reaches the capability
        // list and has to be counted into its section explicitly.
        Assert.Equal(DeviceOverlaySection.Glyphs, entry.Section);
        Assert.Equal(1, entry.Count);
    }

    [Fact]
    public void ASectionPageShowsOnlyItsOwnRows()
    {
        DeviceOverlaySnapshot snapshot = Snapshot(
            Capability("power", DeviceOverlaySection.PowerAndThermals),
            Capability("oem", DeviceOverlaySection.Oem));

        Assert.Equal(
            "power",
            Assert.Single(DeviceOverlaySectionPages.CapabilitiesIn(
                snapshot,
                DeviceOverlaySection.PowerAndThermals)).CapabilityId);
    }

    [Fact]
    public void EverySectionRoundTripsThroughItsPage()
    {
        foreach (DeviceOverlaySection section in Enum.GetValues<DeviceOverlaySection>())
        {
            OverlayPage page = DeviceOverlaySectionPages.PageFor(section);

            Assert.Equal(section, DeviceOverlaySectionPages.SectionFor(page));
        }
    }

    [Fact]
    public void EverySectionPageBelongsToTheDeviceDestination()
    {
        OverlayNavigation navigation = new();
        navigation.SetDeviceVisible(true);

        foreach (DeviceOverlaySection section in Enum.GetValues<DeviceOverlaySection>())
        {
            Assert.True(navigation.Select(OverlayDestination.Device));
            Assert.True(navigation.Push(DeviceOverlaySectionPages.PageFor(section), "key"));
            Assert.Equal(OverlayDestination.Device, navigation.Destination);
            Assert.Equal(OverlayBackAction.LeaveNestedPage, navigation.BackAction(false, false));
            Assert.Equal("key", navigation.Pop());
        }
    }

    [Fact]
    public void APageThatIsNotADeviceSectionHasNoSection() =>
        Assert.Null(DeviceOverlaySectionPages.SectionFor(OverlayPage.SteamLibraryTabs));

    [Fact]
    public void EverySectionHasADistinctFocusKey()
    {
        string[] keys = [.. Enum.GetValues<DeviceOverlaySection>()
            .Select(DeviceOverlaySectionPages.FocusKey)];

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    private static DeviceOverlaySnapshot Snapshot(params DeviceOverlayCapability[] capabilities) =>
        new(true, "Device", "Ready", null, capabilities);

    private static DeviceOverlayCapability Capability(
        string id,
        DeviceOverlaySection section,
        DescriptorStatus status = DescriptorStatus.Available) =>
        new(id, null, section, status, id, id, string.Empty, CanInvoke: true, NextValue: null);
}
