using System.Text.Json;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
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

    [Fact]
    public void EitherLimitFindsTheOtherAndAnUnpairedLimitFindsNone()
    {
        var primary = Limit(CapabilityRole.PowerSustainedLimit, "primary") with { PairedPowerLimitId = "boost" };
        var boost = Limit(CapabilityRole.PowerSlowLimit, "boost");
        var other = Limit(CapabilityRole.PowerSustainedLimit, "other");

        Assert.Same(boost, DevicePowerPair.Peer([primary, boost, other], "primary"));
        Assert.Same(primary, DevicePowerPair.Peer([primary, boost, other], "boost"));
        Assert.Null(DevicePowerPair.Peer([primary, boost, other], "other"));
    }

    [Theory]
    [InlineData("primary", 20, 25, 20, 25)]
    [InlineData("boost", 25, 20, 20, 25)]
    [InlineData("primary", 20, 20, 20, 20)]
    public void ACommandOnEitherLimitResolvesToTheSustainedAndBoostItCarries(
        string capabilityId, int requested, int paired, int sustained, int boost)
    {
        var resolved = DevicePowerPair.TryResolve(Command(capabilityId, requested, paired), Pair(), out var s,
            out var b, out var error);

        Assert.True(resolved, error);
        Assert.Equal((sustained, boost), (s, b));
    }

    [Theory]
    [InlineData("primary", 30, 20)]
    [InlineData("boost", 20, 30)]
    [InlineData("primary", 20, 40)]
    [InlineData("primary", 20, null)]
    public void APairThatIsMissingOutOfRangeOrInvertedIsRefused(string capabilityId, int requested, int? paired)
    {
        Assert.False(DevicePowerPair.TryResolve(Command(capabilityId, requested, paired), Pair(), out _, out _,
            out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ALimitInNoPairIsRefused()
    {
        var unpaired = Limit(CapabilityRole.PowerSustainedLimit, "primary");

        Assert.False(DevicePowerPair.TryResolve(Command("primary", 20, 20), [unpaired], out _, out _, out _));
    }

    private static CapabilityDescriptor[] Pair()
    {
        return
        [
            Limit(CapabilityRole.PowerSustainedLimit, "primary") with { PairedPowerLimitId = "boost" },
            Limit(CapabilityRole.PowerSlowLimit, "boost")
        ];
    }

    private static CapabilityCommand Command(string capabilityId, int requested, int? paired)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = capabilityId,
            RequestedValue = CapabilityValue.Integer(requested),
            PairedPowerLimitWatts = paired,
            ExpectedDescriptorGeneration = 1,
            ExpectedCycleGeneration = 1,
            Deadline = Deadline.After(TimeSpan.FromSeconds(5))
        };
    }
}
