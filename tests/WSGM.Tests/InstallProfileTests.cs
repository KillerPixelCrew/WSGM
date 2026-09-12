using WSGM.Core;

namespace WSGM.Tests;

public sealed class InstallProfileTests
{
    [Theory]
    [InlineData("minimal", InstallProfileKind.Minimal)]
    [InlineData("claw8a2vm", InstallProfileKind.Claw8A2Vm)]
    [InlineData("desktop", InstallProfileKind.DesktopFirst)]
    [InlineData("Claw8A2Vm", InstallProfileKind.Claw8A2Vm)]
    [InlineData(" desktop ", InstallProfileKind.DesktopFirst)]
    public void EachModeIsRecognised(string value, InstallProfileKind expected)
    {
        Assert.True(InstallProfile.TryParse(value, out InstallProfileKind kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    // Custom names no mode on purpose, and neither does an absent or unknown argument. All three
    // must leave the configuration's own defaults alone rather than fall back to a mode.
    [InlineData("custom")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("full")]
    public void AValueThatNamesNoModeIsRefused(string? value)
        => Assert.False(InstallProfile.TryParse(value, out _));

    [Fact]
    public void TheModeIsReadFromSetupsArguments()
    {
        Assert.Equal("desktop", InstallProfile.Read(["--setup", "--profile=desktop"]));
        Assert.Equal("claw8a2vm", InstallProfile.Read(["--PROFILE=claw8a2vm"]));
        Assert.Null(InstallProfile.Read(["--setup"]));
        Assert.Equal("", InstallProfile.Read(["--profile="]));
    }

    [Fact]
    public void MinimalStartsGameModeAtSignInWithNoDeviceIntegration()
    {
        AppConfig config = new();

        Assert.True(InstallProfile.Apply(config, InstallProfileKind.Minimal, freshInstall: true));

        Assert.True(config.StartAtSignIn);
        Assert.Equal(SessionStartMode.Game, config.StartMode);
        Assert.False(config.DeviceIntegration.Enabled);
    }

    [Fact]
    public void TheClawModeAlsoSwitchesTheIntegrationOn()
    {
        // Naming a device in setup is the explicit choice the integration waits for; installing the
        // package and leaving it off would read as a broken install.
        AppConfig config = new();

        Assert.True(InstallProfile.Apply(config, InstallProfileKind.Claw8A2Vm, freshInstall: true));

        Assert.Equal(SessionStartMode.Game, config.StartMode);
        Assert.True(config.DeviceIntegration.Enabled);
    }

    [Fact]
    public void DesktopFirstStartsAtSignInIntoTheResidentDesktopSession()
    {
        AppConfig config = new();

        Assert.True(InstallProfile.Apply(config, InstallProfileKind.DesktopFirst, freshInstall: true));

        Assert.True(config.StartAtSignIn);
        Assert.Equal(SessionStartMode.Desktop, config.StartMode);
        Assert.False(config.DeviceIntegration.Enabled);
    }

    [Theory]
    [InlineData(InstallProfileKind.Minimal)]
    [InlineData(InstallProfileKind.Claw8A2Vm)]
    [InlineData(InstallProfileKind.DesktopFirst)]
    public void ARepairOrUpgradeLeavesEverythingTheUserChose(InstallProfileKind kind)
    {
        // Re-running setup is how people repair and upgrade. A mode that rewrote these each time
        // would silently undo Settings, so changing an installed machine's mode means Settings.
        AppConfig config = new() { StartAtSignIn = false, StartMode = SessionStartMode.Desktop };
        config.DeviceIntegration.Enabled = true;

        Assert.False(InstallProfile.Apply(config, kind, freshInstall: false));

        Assert.False(config.StartAtSignIn);
        Assert.Equal(SessionStartMode.Desktop, config.StartMode);
        Assert.True(config.DeviceIntegration.Enabled);
    }

    [Fact]
    public void ASeededConfigurationStillAsksQuickSetup()
    {
        // The mode preselects the answers; the panel still appears so a person confirms them and
        // the Steam autostart takeover is still consented to rather than assumed.
        AppConfig config = new();

        InstallProfile.Apply(config, InstallProfileKind.DesktopFirst, freshInstall: true);

        Assert.True(QuickSetup.ShouldShow(config));
    }
}
