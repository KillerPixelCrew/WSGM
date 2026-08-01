namespace WSGM.Tests;

public sealed class ModeSelectionTests
{
    [Fact]
    public void ExplicitShellModeHasHighestPrecedence()
    {
        var mode = Program.DecideMode(["--settings", "--overlay-test", "--SHELL"], false, true);

        Assert.Equal(RunMode.Shell, mode);
    }

    [Fact]
    public void ExplicitSettingsModeWinsOverOverlayTest()
    {
        var mode = Program.DecideMode(["--overlay-test", "--settings"], true, false);

        Assert.Equal(RunMode.Settings, mode);
    }

    [Theory]
    [InlineData(true, false, RunMode.Shell)]
    [InlineData(true, true, RunMode.Settings)]
    [InlineData(false, false, RunMode.Settings)]
    [InlineData(false, true, RunMode.Settings)]
    public void AutoModeRequiresTheRegisteredShellAndNoDesktop(
        bool registeredAsShell,
        bool desktopAlive,
        RunMode expected)
    {
        var mode = Program.DecideMode([], registeredAsShell, desktopAlive);

        Assert.Equal(expected, mode);
    }
}
