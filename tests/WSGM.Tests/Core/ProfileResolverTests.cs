using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Tests.Core;

public sealed class ProfileResolverTests
{
    private const string Device = "claw";
    private const string Zone = "lighting.zone-color";

    private static CapabilityValue Color(int color)
    {
        return new CapabilityValue { Kind = CapabilityValueKind.Color, ColorValue = color };
    }

    private static ProfileConfig Store(ProfileValues global, ProfileValues? game = null, bool enabled = true)
    {
        ProfileConfig config = new() { Global = global };
        if (game is not null)
        {
            config.Games.Add(new GameProfile
                { Id = "steam:42", ProcessNames = ["game.exe"], Enabled = enabled, Values = game });
        }

        return config;
    }

    private static ProfileLayers Running(ProfileConfig config)
    {
        return ProfileResolver.Layers(config, ProfileResolver.Activate(config, "steam:42", "game.exe", 42));
    }

    [Fact]
    public void AGameValueWinsOverGlobal()
    {
        var layers = Running(Store(new ProfileValues { FrameLimit = 60 }, new ProfileValues { FrameLimit = 40 }));

        Assert.Equal(new Resolved<int?>(40, ProfileSource.Game), layers.Value(values => values.FrameLimit));
    }

    [Fact]
    public void AValueTheGameDoesNotSetComesFromGlobal()
    {
        var layers = Running(Store(new ProfileValues { FrameLimit = 60, OverlayLevel = 2 },
            new ProfileValues { FrameLimit = 40 }));

        Assert.Equal(new Resolved<int?>(2, ProfileSource.Global), layers.Value(values => values.OverlayLevel));
    }

    [Fact]
    public void AValueNeitherLayerSetsIsLeftToTheDevice()
    {
        var layers = Running(Store(new ProfileValues(), new ProfileValues()));

        Assert.Equal(new Resolved<bool?>(null, ProfileSource.None), layers.Value(values => values.VariableRefreshRate));
    }

    [Fact]
    public void ADisabledGameProfileResolvesEverythingFromGlobal()
    {
        var layers = Running(Store(new ProfileValues { FrameLimit = 60 }, new ProfileValues { FrameLimit = 40 },
            false));

        Assert.Equal(new Resolved<int?>(60, ProfileSource.Global), layers.Value(values => values.FrameLimit));
        Assert.Null(layers.Game);
    }

    [Fact]
    public void NothingRunningResolvesFromGlobal()
    {
        var config = Store(new ProfileValues { FrameLimit = 60 }, new ProfileValues { FrameLimit = 40 });

        var layers = ProfileResolver.Layers(config, ProfileResolver.Activate(config, null, null, null));

        Assert.Equal(60, layers.Value(values => values.FrameLimit).Value);
    }

    [Fact]
    public void EachLightingZoneFallsBackOnItsOwn()
    {
        // The reported bug: a game that set only its buttons must leave the rings on the Global colour,
        // not on whatever the firmware restored after sleep.
        ProfileValues global = new();
        global.SetDevice(Device, Zone, "buttons", Color(0x0000FF));
        global.SetDevice(Device, Zone, "left-ring", Color(0x0000FF));
        ProfileValues game = new();
        game.SetDevice(Device, Zone, "buttons", Color(0xFF0000));
        var layers = Running(Store(global, game));

        Assert.Equal(new Resolved<CapabilityValue?>(Color(0xFF0000), ProfileSource.Game),
            layers.Device(Device, Zone, "buttons"));
        Assert.Equal(new Resolved<CapabilityValue?>(Color(0x0000FF), ProfileSource.Global),
            layers.Device(Device, Zone, "left-ring"));
        Assert.Equal(ProfileSource.None, layers.Device(Device, Zone, "right-ring").Source);
    }

    [Fact]
    public void ADeviceValueForAnotherMachineIsNotUsed()
    {
        ProfileValues global = new();
        global.SetDevice("other", Zone, "buttons", Color(0x0000FF));

        Assert.Equal(ProfileSource.None, Running(Store(global)).Device(Device, Zone, "buttons").Source);
    }

    [Fact]
    public void SourceNamesTheLayerForEverySetting()
    {
        ProfileValues game = new()
            { AcPowerPreset = new DevicePowerPresetReference { PluginId = "p", PresetId = "a" } };
        game.SetDevice(Device, Zone, "buttons", Color(1));
        var layers = Running(Store(new ProfileValues { ControllerTarget = ManagedControllerTarget.Xbox360 }, game));

        Assert.Equal(ProfileSource.Game, layers.Source(new ProfileSettingKey(ProfileField.AcPowerPreset)));
        Assert.Equal(ProfileSource.Global, layers.Source(new ProfileSettingKey(ProfileField.ControllerTarget)));
        Assert.Equal(ProfileSource.None, layers.Source(new ProfileSettingKey(ProfileField.BatteryPowerPreset)));
        Assert.Equal(ProfileSource.Game, layers.Source(ProfileSettingKey.ForDevice(Zone, "buttons"), Device));
        Assert.Equal(2, layers.GameOverrideCount);
    }

    [Theory]
    [InlineData("FrameLimit")]
    [InlineData("device:lighting.zone-color#left-ring")]
    [InlineData("device:fan.mode#")]
    public void SettingKeysRoundTripThroughTheirStringForm(string id)
    {
        Assert.True(ProfileSettingKey.TryParse(id, out var key));
        Assert.Equal(id, key.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Device")]
    [InlineData("NotAField")]
    [InlineData("device:#zone")]
    [InlineData("999")]
    public void UnknownSettingKeysAreRefused(string? id)
    {
        Assert.False(ProfileSettingKey.TryParse(id, out _));
    }
}
