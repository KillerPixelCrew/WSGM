using WSGM.Core;
using WSGM.LogonService;

namespace WSGM.Tests.LogonService;

public sealed class LogonDecisionTests
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    private static BootManifest Manifest(bool enabled = true, bool elevate = false)
    {
        return new BootManifest { GameModeBoot = enabled, Elevate = elevate, ExePath = @"C:\x\WSGM.exe" };
    }

    [Fact]
    public void DesktopAutomationLaunchesWithoutGameModeTakeover()
    {
        var manifest = Manifest(false);
        manifest.DesktopResident = true;
        Assert.Equal(LogonAction.Launch, LogonDecision.Decide(
            manifest, true, false, null, StaleAfter));
        Assert.Equal("--shell --desktop-resident", LogonDecision.ArgumentsFor(manifest));
        manifest.GameModeBoot = true;
        Assert.Equal("--boot", LogonDecision.ArgumentsFor(manifest));
    }

    [Fact]
    public void FreshLogonWithEnabledManifestLaunches()
    {
        Assert.Equal(LogonAction.Launch, LogonDecision.Decide(
            Manifest(), true, false, null, StaleAfter));
    }

    [Fact]
    public void ElevateFlagRoutesToTheLinkedTokenLaunch()
    {
        Assert.Equal(LogonAction.LaunchElevated, LogonDecision.Decide(
            Manifest(elevate: true), true, false, null, StaleAfter));
    }

    [Fact]
    public void DisabledManifestSkips()
    {
        Assert.Equal(LogonAction.SkipDisabled, LogonDecision.Decide(
            Manifest(false), true, false, null, StaleAfter));
    }

    [Fact]
    public void MissingManifestSkips()
    {
        Assert.Equal(LogonAction.SkipNoManifest, LogonDecision.Decide(
            null, true, false, null, StaleAfter));
    }

    [Fact]
    public void OneLaunchPerSessionEvenWhenEverythingElseSaysGo()
    {
        Assert.Equal(LogonAction.SkipAlreadyLaunched, LogonDecision.Decide(
            Manifest(elevate: true), true, true, null, StaleAfter));
    }

    [Fact]
    public void CatchUpInsideTheWindowLaunches()
    {
        Assert.Equal(LogonAction.Launch, LogonDecision.Decide(
            Manifest(), true, false,
            TimeSpan.FromSeconds(12), StaleAfter));
    }

    [Fact]
    public void CatchUpBeyondTheWindowIsStale()
    {
        Assert.Equal(LogonAction.SkipStale, LogonDecision.Decide(
            Manifest(), true, false,
            TimeSpan.FromMinutes(5), StaleAfter));
    }

    [Fact]
    public void InactiveSessionIsStaleRegardlessOfAge()
    {
        Assert.Equal(LogonAction.SkipStale, LogonDecision.Decide(
            Manifest(), false, false, null, StaleAfter));
    }

    [Fact]
    public void StaleOutranksManifestProblemsSoTheLogIsHonest()
    {
        Assert.Equal(LogonAction.SkipStale, LogonDecision.Decide(
            null, false, false, null, StaleAfter));
    }
}
