using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Tests.Builders;

namespace WSGM.Device.Sdk.Tests.Capabilities;

public sealed class CapabilityLayoutTests
{
    [Fact]
    public void DefaultHintsPreserveUnpairedNormalPresentation()
    {
        var descriptor = PowerLimitDescriptors.Limit(CapabilityRole.PowerSustainedLimit);
        Assert.Equal(CapabilityProminence.Normal, descriptor.Prominence);
        Assert.Null(descriptor.LayoutPair);
        Assert.True(CapabilityLayout.TryValidate([descriptor], out _));
    }

    [Fact]
    public void PairingSelectsAnExactCompanionInstanceWithoutChangingPowerSemantics()
    {
        var primary = PowerLimitDescriptors.Limit(CapabilityRole.PowerSustainedLimit, "power.primary") with
        {
            Prominence = CapabilityProminence.Primary,
            LayoutPair = new CapabilityLayoutPair("reading", "left")
        };
        var reading = PowerLimitDescriptors.Limit(CapabilityRole.Telemetry, "reading") with
        {
            InstanceId = "left", Prominence = CapabilityProminence.Compact
        };
        Assert.True(CapabilityLayout.TryValidate([primary, reading, reading with { InstanceId = "right" }], out _));
        Assert.Null(primary.PairedPowerLimitId);
    }

    [Fact]
    public void InvalidProminenceSelfPairMissingInstanceAndCrossGroupPairAreRejected()
    {
        var descriptor = PowerLimitDescriptors.Limit(CapabilityRole.PowerSustainedLimit, "power");
        Assert.False(CapabilityLayout.TryValidate([descriptor with { Prominence = (CapabilityProminence)999 }], out _));
        Assert.False(CapabilityLayout.TryValidate([descriptor with { LayoutPair = new CapabilityLayoutPair("power") }],
            out _));
        Assert.False(
            CapabilityLayout.TryValidate([descriptor with { LayoutPair = new CapabilityLayoutPair("missing") }],
                out _));
        var companion = descriptor with { CapabilityId = "boost", CategoryId = "other" };
        Assert.False(
            CapabilityLayout.TryValidate(
                [descriptor with { LayoutPair = new CapabilityLayoutPair("boost") }, companion], out _));
    }
}
