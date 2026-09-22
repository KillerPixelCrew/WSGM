using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class ManualTdpPolicyTests
{
    private static ProfileLayers Layers(ProfileValues global, ProfileValues? game = null)
    {
        return new ProfileLayers(global, game);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(50, true)]
    [InlineData(9, false)]
    [InlineData(51, false)]
    [InlineData(11, false)]
    public void SplitRestoreUsesTheCompanionsOwnRangeAndStep(int watts, bool accepted)
    {
        Assert.Equal(accepted, ManualTdpPolicy.Accepts(10, 50, 2, watts));
        Assert.False(ManualTdpPolicy.Accepts(10, 50, null, watts));
    }

    [Fact]
    public void AGameThatSetsOnlyItsModeKeepsEveryGlobalWattage()
    {
        // The bug the per-field merge exists for: the game's record used to be taken whole, so a
        // game that chose advanced mode ran with no sustained limit although Global had one.
        var layers = Layers(
            new ProfileValues { TdpUnified = true, UnifiedWatts = 30, SustainedWatts = 20, BoostWatts = 35 },
            new ProfileValues { TdpUnified = false });

        Assert.Equal(new ManualTdpProfile(false, 30, 20, 35), layers.ManualTdp());
        Assert.Equal((20, false), ManualTdpPolicy.ResolveTarget(layers));
    }

    [Fact]
    public void AGameWattageOverridesOnlyThatWattage()
    {
        var layers = Layers(
            new ProfileValues { TdpUnified = false, SustainedWatts = 20, BoostWatts = 35 },
            new ProfileValues { SustainedWatts = 15 });

        Assert.Equal(new ManualTdpProfile(false, null, 15, 35), layers.ManualTdp());
        Assert.Equal(ProfileSource.Game, layers.Source(new ProfileSettingKey(ProfileField.SustainedWatts)));
        Assert.Equal(ProfileSource.Global, layers.Source(new ProfileSettingKey(ProfileField.BoostWatts)));
    }

    [Fact]
    public void TheTargetFollowsTheResolvedMode()
    {
        ProfileValues global = new() { UnifiedWatts = 20, SustainedWatts = 25 };
        ProfileValues game = new() { TdpUnified = true, UnifiedWatts = 15 };

        Assert.Equal((25, false), ManualTdpPolicy.ResolveTarget(Layers(global)));
        Assert.Equal((15, true), ManualTdpPolicy.ResolveTarget(Layers(global, game)));
        Assert.Equal(ProfileField.UnifiedWatts, Layers(global, game).PowerTargetKey.Field);
        Assert.Equal(ProfileField.SustainedWatts, Layers(global).PowerTargetKey.Field);
    }

    [Fact]
    public void NoLayerSettingAnythingResolvesToNoProfileAndNoTarget()
    {
        Assert.Null(Layers(new ProfileValues(), new ProfileValues()).ManualTdp());
        Assert.Equal((null, false), ManualTdpPolicy.ResolveTarget(Layers(new ProfileValues())));
    }

    [Fact]
    public void AMissingTargetInTheResolvedModeDoesNotInventAWattage()
    {
        var layers = Layers(new ProfileValues { TdpUnified = true, SustainedWatts = 18 });

        Assert.Null(ManualTdpPolicy.ResolveTarget(layers).Watts);
    }
}
