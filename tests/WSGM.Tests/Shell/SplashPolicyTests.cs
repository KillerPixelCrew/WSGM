using WSGM.Shell;

namespace WSGM.Tests;

public sealed class SplashPolicyTests
{
    [Fact]
    public void AnUnarmedSplashNeverTimesOut()
    {
        // The wait for a TV behind an HDMI switch is open-ended by design. A timeout measured from
        // the cover would fire in the middle of exactly the wait the cover exists for.
        Assert.False(SplashPolicy.ShouldTimeout(armed: false, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void AnArmedSplashTimesOutOnlyAfterItsWindow()
    {
        Assert.False(SplashPolicy.ShouldTimeout(armed: true, SplashPolicy.SteamTimeout));
        Assert.True(SplashPolicy.ShouldTimeout(
            armed: true, SplashPolicy.SteamTimeout + TimeSpan.FromSeconds(1)));
    }
}
