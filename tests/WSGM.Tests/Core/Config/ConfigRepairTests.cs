using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Tests.Core;

/// <summary>Schema migration; schema 2 moves a saved Atlas power-limit path to IPF once.</summary>
public sealed class ConfigRepairTests
{
    [Fact]
    public void AnOlderConfigMovesEveryAcerPowerPathToIpfAndLeavesOtherDevicesAlone()
    {
        var config = new AppConfig { SchemaVersion = 1 };
        config.Profiles.Global.Device.AddRange([
            Path("acer:A57E1E9911F718F9BB1261EE", "firmware"),
            Path("acer:F7CAD1E66B63ED8DD4D859FB", "pawnio"),
            Path("msi-claw:0123456789ABCDEF", "firmware")
        ]);
        config.Profiles.Games.Add(new GameProfile { Values = { Device = [Path("acer:F7CAD1E66B63ED8DD4D859FB", "firmware")] } });

        Assert.Equal(1, ConfigRepair.Migrate(config));

        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
        Assert.Equal(["ipf", "ipf", "firmware"], config.Profiles.Global.Device.Select(entry => entry.Value!.ChoiceValue));
        Assert.Equal("ipf", config.Profiles.Games[0].Values.Device[0].Value!.ChoiceValue);
    }

    [Fact]
    public void AChoiceSavedAfterTheMigrationIsKept()
    {
        var config = new AppConfig { SchemaVersion = AppConfig.CurrentSchemaVersion };
        config.Profiles.Global.Device.Add(Path("acer:F7CAD1E66B63ED8DD4D859FB", "firmware"));

        ConfigRepair.Migrate(config);

        Assert.Equal("firmware", config.Profiles.Global.Device[0].Value!.ChoiceValue);
    }

    private static ProfileDeviceValue Path(string device, string choice)
    {
        return new ProfileDeviceValue
        {
            DeviceIdentityKey = device, CapabilityId = "power.backend", Value = CapabilityValue.Choice(choice)
        };
    }
}
