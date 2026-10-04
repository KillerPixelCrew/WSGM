namespace WSGM.Tests.App;

public sealed class ModeSelectionTests
{
    [Fact]
    public void ExplicitShellModeHasHighestPrecedence()
    {
        var mode = StartupOptions.Parse(["--settings", "--overlay-test", "--SHELL"]).Mode;

        Assert.Equal(RunMode.Shell, mode);
    }

    [Fact]
    public void ExplicitSettingsModeWinsOverOverlayTest()
    {
        var mode = StartupOptions.Parse(["--overlay-test", "--settings"]).Mode;

        Assert.Equal(RunMode.Settings, mode);
    }

    [Fact]
    public void OverlayTestFlagSelectsTheSafeOverlaySmokeTestMode()
    {
        // The only local surface that exercises the overlay without a takeover; every
        // other test in this file passes --overlay-test as a LOSER of the precedence
        // rules, so deleting its branch would go unnoticed without this one.
        var mode = StartupOptions.Parse(["--OVERLAY-TEST"]).Mode;

        Assert.Equal(RunMode.OverlayTest, mode);
    }

    [Fact]
    public void NoFlagSelectsTheSafeSettingsMode()
    {
        var mode = StartupOptions.Parse([]).Mode;

        Assert.Equal(RunMode.Settings, mode);
    }

    [Fact]
    public void ServiceBootSelectsShellMode()
    {
        var mode = StartupOptions.Parse(["--BOOT"]).Mode;

        Assert.Equal(RunMode.Shell, mode);
    }

    [Fact]
    public void ServiceBootOutranksSettingsAndOverlayTest()
    {
        var mode = StartupOptions.Parse(["--settings", "--overlay-test", "--boot"]).Mode;

        Assert.Equal(RunMode.Shell, mode);
    }

    [Theory]
    [InlineData(new[] { "--boot" }, true)]
    [InlineData(new[] { "--BOOT", "--elevated-relaunch" }, true)]
    [InlineData(new[] { "--shell" }, false)]
    [InlineData(new string[0], false)]
    public void IsServiceBootDetectsOnlyTheBootFlag(string[] args, bool expected)
    {
        Assert.Equal(expected, StartupOptions.Parse(args).ServiceBoot);
    }
}
