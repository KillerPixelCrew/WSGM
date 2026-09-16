using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SplashPolicyTests
{
    [Fact]
    public void AnUnarmedSplashNeverTimesOut()
    {
        // The wait for a TV behind an HDMI switch is open-ended by design. A timeout measured from
        // the cover would fire in the middle of exactly the wait the cover exists for.
        Assert.False(SplashPolicy.ShouldTimeout(false, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void AnArmedSplashTimesOutOnlyAfterItsWindow()
    {
        Assert.False(SplashPolicy.ShouldTimeout(true, SplashPolicy.SteamTimeout));
        Assert.True(SplashPolicy.ShouldTimeout(
            true, SplashPolicy.SteamTimeout + TimeSpan.FromSeconds(1)));
    }
}
