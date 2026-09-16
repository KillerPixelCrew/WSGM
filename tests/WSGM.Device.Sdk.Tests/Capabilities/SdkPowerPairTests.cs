using System.Text.Json;
using WSGM.Device.Sdk.Capabilities;
using static WSGM.Device.Sdk.Tests.Builders.PowerLimitDescriptors;

namespace WSGM.Device.Sdk.Tests.Capabilities;

public sealed class SdkPowerPairTests
{
    [Fact]
    public void PairMetadataIsOptionalAndSurvivesSerialization()
    {
        var primary = Limit(CapabilityRole.PowerSustainedLimit, "primary");
        var boost = Limit(CapabilityRole.PowerSlowLimit, "boost");
        Assert.True(DevicePowerPair.TryValidate([primary], out _));
        primary = primary with { PairedPowerLimitId = "boost" };
        var decoded = JsonSerializer.Deserialize<CapabilityDescriptor>(JsonSerializer.Serialize(primary))!;
        Assert.Equal("boost", decoded.PairedPowerLimitId);
        Assert.True(DevicePowerPair.TryValidate([decoded, boost], out _));
    }

    [Fact]
    public void MissingAmbiguousOrIncompatibleCompanionsAreRejected()
    {
        var primary = Limit(CapabilityRole.PowerSustainedLimit, "primary") with { PairedPowerLimitId = "boost" };
        var boost = Limit(CapabilityRole.PowerSlowLimit, "boost");
        Assert.False(DevicePowerPair.TryValidate([primary], out _));
        Assert.False(DevicePowerPair.TryValidate([primary, boost, boost], out _));
        Assert.False(DevicePowerPair.TryValidate([primary, boost with { Minimum = 40 }], out _));
        Assert.False(DevicePowerPair.TryValidate([primary, boost with { SupportsWrite = false }], out _));
        Assert.False(DevicePowerPair.TryValidate([primary, boost with { PairedPowerLimitId = "primary" }], out _));
    }

    [Fact]
    public void PluginDefinesCompanionRangeAndStepIndependently()
    {
        var primary = Limit(CapabilityRole.PowerSustainedLimit, "primary") with { PairedPowerLimitId = "boost" };
        var boost = Limit(CapabilityRole.PowerSlowLimit, "boost") with { Minimum = 10, Maximum = 50, Step = 2 };
        Assert.True(DevicePowerPair.TryValidate([primary, boost], out _));
        Assert.False(DevicePowerPair.TryValidate([primary, boost with { Step = 0 }], out _));
    }
}
