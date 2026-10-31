using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SteamCefActivationPolicyTests
{
    private static readonly SteamProcessSnapshot NoBrowserYet = new(true, 0, null, false);

    [Fact]
    public void ASteamStillStartingOrUpdatingNeverRestarts()
    {
        // 2026-10-13: the client update ran past the old 30 s grace and WSGM restarted a healthy Steam.
        var policy = new SteamCefActivationPolicy();

        for (var tick = 0; tick < 600; tick++)
        {
            Assert.Equal(SteamCefActivation.None, policy.Decide(true, false, NoBrowserYet));
        }
    }

    [Fact]
    public void ABrowserStartedWithTheFlagNeverRestartsWhateverThePortDoes()
    {
        var policy = new SteamCefActivationPolicy();

        Assert.Equal(SteamCefActivation.None, policy.Decide(true, false, Browser(100, true)));
    }

    [Fact]
    public void ABrowserStartedWithoutTheFlagRestartsOnce()
    {
        var policy = new SteamCefActivationPolicy();

        Assert.Equal(SteamCefActivation.Restart, policy.Decide(true, false, Browser(100, false)));
        Assert.Equal(SteamCefActivation.None, policy.Decide(true, false, Browser(100, false)));
    }

    [Fact]
    public void AReplacementWithoutTheFlagIsReportedOnceAndNeverRestarted()
    {
        var policy = new SteamCefActivationPolicy();

        Assert.Equal(SteamCefActivation.Restart, policy.Decide(true, false, Browser(100, false)));
        Assert.Equal(SteamCefActivation.None, policy.Decide(true, false, NoBrowserYet));
        Assert.Equal(SteamCefActivation.GaveUp, policy.Decide(true, false, Browser(200, false)));
        Assert.Equal(SteamCefActivation.None, policy.Decide(true, false, Browser(200, false)));
    }

    [Fact]
    public void ABrowserWithTheFlagRearmsTheRestartForALaterSteam()
    {
        var policy = new SteamCefActivationPolicy();

        Assert.Equal(SteamCefActivation.Restart, policy.Decide(true, false, Browser(100, false)));
        Assert.Equal(SteamCefActivation.None, policy.Decide(true, false, Browser(200, true)));
        Assert.Equal(SteamCefActivation.Restart, policy.Decide(true, false, Browser(300, false)));
    }

    [Fact]
    public void AnUnreadableCommandLineIsNeverAReasonToRestart()
    {
        var policy = new SteamCefActivationPolicy();

        Assert.Equal(SteamCefActivation.None, policy.Decide(true, false, new SteamProcessSnapshot(true, 100, null, true)));
    }

    [Fact]
    public void ATransitionDefersTheRestartWithoutSpendingIt()
    {
        var policy = new SteamCefActivationPolicy();

        Assert.Equal(SteamCefActivation.None, policy.Decide(true, true, Browser(100, false)));
        Assert.Equal(SteamCefActivation.Restart, policy.Decide(true, false, Browser(100, false)));
    }

    [Fact]
    public void TurningTheIntegrationOffRearmsTheRestart()
    {
        var policy = new SteamCefActivationPolicy();

        Assert.Equal(SteamCefActivation.Restart, policy.Decide(true, false, Browser(100, false)));
        Assert.Equal(SteamCefActivation.None, policy.Decide(false, false, Browser(100, false)));
        Assert.Equal(SteamCefActivation.Restart, policy.Decide(true, false, Browser(100, false)));
    }

    private static SteamProcessSnapshot Browser(uint processId, bool debugging)
    {
        return new SteamProcessSnapshot(true, processId, debugging, false);
    }
}
