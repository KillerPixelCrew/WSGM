using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;
using static WSGM.Tests.Builders.PerformanceBuilders;

namespace WSGM.Tests.Shell;

public sealed class NativeQamProfileOverrideServiceTests
{
    private static async Task<ProfileService> GameWithOverridesAsync()
    {
        var profiles = Profiles(Config(60));
        profiles.SetRunningApplication(new PerformanceApplicationTarget("steam:42", 42, "game.exe"));
        await profiles.SetGameEnabledAsync(true);
        await profiles.SetAsync(ProfileField.FrameLimit, 40);
        await profiles.SetDeviceAsync("claw", "lighting.zone-color", "buttons",
            new CapabilityValue { Kind = CapabilityValueKind.Color, ColorValue = 0xFF0000 });
        return profiles;
    }

    [Fact]
    public async Task UseGlobalRemovesTheNamedGameOverride()
    {
        var profiles = await GameWithOverridesAsync();
        NativeQamProfileOverrideService service = new(profiles, () => "claw");

        var frame = await service.UseGlobalAsync("FrameLimit", CancellationToken.None);
        var zone = await service.UseGlobalAsync("device:lighting.zone-color#buttons", CancellationToken.None);

        Assert.True(frame.Succeeded);
        Assert.True(zone.Succeeded);
        Assert.Equal(0, profiles.Current.Game!.Values.Count());
        Assert.Equal(60, profiles.Current.Layers.Value(values => values.FrameLimit).Value);
    }

    [Theory]
    [InlineData("NotAField")]
    [InlineData("VariableRefreshRate")]
    public async Task AnUnknownOrNotOverriddenSettingIsRefused(string id)
    {
        var profiles = await GameWithOverridesAsync();
        NativeQamProfileOverrideService service = new(profiles, () => "claw");

        var result = await service.UseGlobalAsync(id, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(2, profiles.Current.Game!.Values.Count());
    }

    [Fact]
    public async Task AnOverrideIdNamesOnlyAValueTheGameSets()
    {
        var profiles = await GameWithOverridesAsync();
        var layers = profiles.Current.Layers;

        Assert.Equal("FrameLimit", NativeQamUi.OverrideId(layers, new ProfileSettingKey(ProfileField.FrameLimit)));
        Assert.Null(NativeQamUi.OverrideId(layers, new ProfileSettingKey(ProfileField.OverlayLevel)));
        Assert.Null(NativeQamUi.OverrideId(null, new ProfileSettingKey(ProfileField.FrameLimit)));
    }
}
