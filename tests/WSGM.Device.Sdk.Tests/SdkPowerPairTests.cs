using System.Text.Json;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Device.Tests;

public sealed class SdkPowerPairTests
{
    [Fact]
    public void PairMetadataIsOptionalAndSurvivesSerialization()
    {
        var primary = Limit("primary", CapabilityRole.PowerSustainedLimit);
        var boost = Limit("boost", CapabilityRole.PowerSlowLimit);
        Assert.True(DevicePowerPair.TryValidate([primary], out _));
        primary = primary with { PairedPowerLimitId = "boost" };
        var decoded = JsonSerializer.Deserialize<CapabilityDescriptor>(JsonSerializer.Serialize(primary))!;
        Assert.Equal("boost", decoded.PairedPowerLimitId);
        Assert.True(DevicePowerPair.TryValidate([decoded, boost], out _));
    }

    [Fact]
    public void MissingAmbiguousOrIncompatibleCompanionsAreRejected()
    {
        var primary = Limit("primary", CapabilityRole.PowerSustainedLimit) with { PairedPowerLimitId = "boost" };
        var boost = Limit("boost", CapabilityRole.PowerSlowLimit);
        Assert.False(DevicePowerPair.TryValidate([primary], out _));
        Assert.False(DevicePowerPair.TryValidate([primary, boost, boost], out _));
        Assert.False(DevicePowerPair.TryValidate([primary, boost with { Minimum = 40 }], out _));
        Assert.False(DevicePowerPair.TryValidate([primary, boost with { SupportsWrite = false }], out _));
        Assert.False(DevicePowerPair.TryValidate([primary, boost with { PairedPowerLimitId = "primary" }], out _));
    }

    [Fact]
    public void PluginDefinesCompanionRangeAndStepIndependently()
    {
        var primary = Limit("primary", CapabilityRole.PowerSustainedLimit) with { PairedPowerLimitId = "boost" };
        var boost = Limit("boost", CapabilityRole.PowerSlowLimit) with { Minimum = 10, Maximum = 50, Step = 2 };
        Assert.True(DevicePowerPair.TryValidate([primary, boost], out _));
        Assert.False(DevicePowerPair.TryValidate([primary, boost with { Step = 0 }], out _));
    }

    private static CapabilityDescriptor Limit(string id, CapabilityRole role) => new()
    {
        CapabilityId = id,
        Role = role,
        ValueKind = CapabilityValueKind.Integer,
        Display = new() { Key = DisplayKey.SustainedPowerLimit },
        Persistence = CapabilityPersistence.Volatile,
        SupportsRead = true,
        SupportsWrite = true,
        Unit = CapabilityUnit.Watt,
        Minimum = 8,
        Maximum = 37,
        Step = 1,
    };
}
