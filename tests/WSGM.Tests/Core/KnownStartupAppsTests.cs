using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class KnownStartupAppsTests
{
    [Fact]
    public void AmdSoftwareHostIsSuggestedWithWindowsOwnLauncherArguments()
    {
        var detected = KnownStartupApps.Resolve([@"C:\Program Files", @"C:\Program Files (x86)", ""],
            path => path == @"C:\Program Files\AMD\CNext\CNext\cncmd.exe");

        var suggestion = Assert.Single(detected);
        Assert.Equal("AMD Software: Adrenalin Edition (background host)", suggestion.Label);
        Assert.Equal(@"C:\Program Files\AMD\CNext\CNext\cncmd.exe", suggestion.Path);
        Assert.Equal("restart", suggestion.Args);
        Assert.False(suggestion.Elevated);
    }

    [Fact]
    public void TheSameExecutableUnderSeveralRootsIsSuggestedOnceWithoutArguments()
    {
        var detected = KnownStartupApps.Resolve([@"C:\Program Files", @"C:\Program Files (x86)"],
            path => path.EndsWith(@"\RTSS.exe", StringComparison.Ordinal));

        var suggestion = Assert.Single(detected);
        Assert.Equal(@"C:\Program Files\RivaTuner Statistics Server\RTSS.exe", suggestion.Path);
        Assert.Equal("", suggestion.Args);
        Assert.True(suggestion.Elevated);
    }
}
