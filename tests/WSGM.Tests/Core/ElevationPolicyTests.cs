using WSGM.Core;

namespace WSGM.Tests;

public sealed class ElevationPolicyTests
{
    [Fact]
    public void AnElevatedSteamAlwaysWins()
        => Assert.Equal("Steam requires matching elevation",
            ElevationPolicy.ElevationReason(new AppConfig(), steamAlreadyElevated: true));

    [Fact]
    public void AnElevatedStartupAppWantsElevation()
    {
        var config = new AppConfig
        {
            SteamLaunchUnelevated = true,
            StartupApps = [new StartupAppConfig { Enabled = true, Elevated = true, Path = @"C:\x.exe" }],
        };

        Assert.Equal("the configuration starts elevated apps",
            ElevationPolicy.ElevationReason(config, steamAlreadyElevated: false));
    }

    [Fact]
    public void ADisabledStartupAppDoesNot()
    {
        var config = new AppConfig
        {
            SteamLaunchUnelevated = true,
            StartupApps = [new StartupAppConfig { Enabled = false, Elevated = true, Path = @"C:\x.exe" }],
        };

        Assert.Null(ElevationPolicy.ElevationReason(config, steamAlreadyElevated: false));
    }

    [Fact]
    public void DeviceIntegrationWantsElevation()
    {
        var config = new AppConfig { SteamLaunchUnelevated = true };
        config.DeviceIntegration.Enabled = true;

        Assert.Equal("device integration is enabled",
            ElevationPolicy.ElevationReason(config, steamAlreadyElevated: false));
    }

    [Fact]
    public void StartingSteamAtWsgmIntegrityWantsElevation()
    {
        // WSGM owns the Steam start now, and children inherit the token: without this the
        // session would silently drop to medium integrity once the user's own elevated Steam
        // autostart is gone.
        Assert.Equal("WSGM starts Steam at its own integrity",
            ElevationPolicy.ElevationReason(new AppConfig(), steamAlreadyElevated: false));
    }

    [Fact]
    public void AskingForAnUnelevatedSteamNeedsNothing()
    {
        var config = new AppConfig { SteamLaunchUnelevated = true };

        Assert.Null(ElevationPolicy.ElevationReason(config, steamAlreadyElevated: false));
        Assert.False(ElevationPolicy.WantsElevation(config, steamAlreadyElevated: false));
    }
}
