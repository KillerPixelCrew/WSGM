using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class ShellStartupTests
{
    [Theory]
    [InlineData("--boot")]
    [InlineData("--shell")]
    [InlineData("--shell", "--desktop-resident")]
    public void DesktopPreferenceWinsForBothServiceBootAndOrdinaryShellLaunch(params string[] arguments)
    {
        var options = StartupOptions.Parse(arguments);

        Assert.Equal(RunMode.Shell, options.Mode);
        Assert.True(SessionBoot.StartsOnDesktop(SessionStartMode.Desktop, options.DesktopResident));
    }

    [Fact]
    public void GameServiceBootStillRequestsTakeover()
    {
        var options = StartupOptions.Parse(["--boot"]);

        Assert.True(options.ServiceBoot);
        Assert.False(SessionBoot.StartsOnDesktop(SessionStartMode.Game, options.DesktopResident));
    }

    [Theory]
    [InlineData(SessionStartMode.Desktop, false, true)]
    [InlineData(SessionStartMode.Desktop, true, true)]
    [InlineData(SessionStartMode.Game, false, false)]
    [InlineData(SessionStartMode.Game, true, true)]
    public void CurrentDesktopPreferenceCannotBeBypassedByMissingOrStaleLaunchModifiers(
        SessionStartMode configuredMode, bool desktopResident, bool expected)
    {
        Assert.Equal(expected, SessionBoot.StartsOnDesktop(configuredMode, desktopResident));
    }
}
