using LibHandheld.Families.Acer;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;
using P = LibHandheld.Contracts;

namespace WSGM.Tests.Shell.Device;

public sealed class DeviceCoordinatorLightingZonesServiceTests
{
    private static readonly string[] AtlasZoneOrder =
    [
        "main", "left.top-left", "left.top-right", "left.bottom-left", "left.bottom-right",
        "right.top-left", "right.top-right", "right.bottom-left", "right.bottom-right"
    ];

    /// The capability snapshot is alphabetical, so "left.*" came before "main" in Steam's zone list. Both
    /// surfaces list the zones as the Atlas publishes them: all segments, then the left ring, then the right.
    [Fact]
    public void AtlasZonesKeepThePublishedOrderOnTheOverlayAndInSteam()
    {
        var views = AtlasLightingViews();

        Assert.Equal(AtlasZoneOrder, DeviceCoordinatorLightingZonesService
            .Project(views, FanCurvePresetTable.HandheldCompanion).Select(zone => zone.Id));
        Assert.Equal("All ring segments",
            DeviceCoordinatorLightingZonesService.Project(views, FanCurvePresetTable.HandheldCompanion)[0].Label);

        var sections = DeviceSectionLayout.IncludePredefined(HandheldUiProjection.Descriptors(
            new P.CapabilityDescriptorSet { Descriptors = [] }).Sections);
        var ids = sections.Select(section => section.SectionId).ToHashSet(StringComparer.Ordinal);
        DeviceOverlaySnapshot snapshot = new(true, "Atlas", "Ready", null,
        [
            .. views.Select(view =>
                CapabilityRowProjection.ToDeviceRow(view, ids, FanCurvePresetTable.HandheldCompanion))
        ]) { PluginSections = CapabilityRowProjection.ProjectSections(sections) };
        var zones = Assert.Single(DeviceOverlayGroups.Build(snapshot),
            group => group.Id == "section.device.plugin.rgb.category.zones");
        Assert.Equal(AtlasZoneOrder, zones.Capabilities.Select(capability => capability.InstanceId));
    }

    /// The order is the descriptors', so a refresh that hands the views over in another order changes nothing.
    [Fact]
    public void ZoneOrderIsStableWhateverOrderTheSnapshotHoldsTheViewsIn()
    {
        var views = AtlasLightingViews();

        Assert.Equal(AtlasZoneOrder, DeviceCoordinatorLightingZonesService
            .Project([.. views.AsEnumerable().Reverse()], FanCurvePresetTable.HandheldCompanion)
            .Select(zone => zone.Id));
    }

    /// The Atlas lighting publication through the host projection, in the router's alphabetical snapshot order.
    private static DeviceCapabilityView[] AtlasLightingViews()
    {
        var descriptors = HandheldUiProjection.Descriptors(new P.CapabilityDescriptorSet
        {
            Descriptors = [.. new AcerLightingService(null!, null!).Descriptors]
        }).Descriptors;
        return
        [
            .. descriptors
                .OrderBy(descriptor => descriptor.CapabilityId, StringComparer.Ordinal)
                .ThenBy(descriptor => descriptor.InstanceId, StringComparer.Ordinal)
                .Select(descriptor => new DeviceCapabilityView(descriptor, new CapabilityProjection
                {
                    State = new CapabilityState
                    {
                        CapabilityId = descriptor.CapabilityId, InstanceId = descriptor.InstanceId, Available = true,
                        ObservedValue = descriptor.ValueKind is CapabilityValueKind.Color
                            ? CapabilityValue.Color(0xFF0000)
                            : null,
                        Quality = HardwareStateQuality.Verified, ObservedAt = DateTimeOffset.UtcNow
                    }
                }, null))
        ];
    }
}
