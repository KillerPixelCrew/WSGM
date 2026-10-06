extern alias LogonService;
using BootManifest = LogonService::WSGM.Core.BootManifest;
using LogonAction = LogonService::WSGM.LogonService.LogonAction;
using LogonDecision = LogonService::WSGM.LogonService.LogonDecision;

namespace WSGM.Tests.LogonService;

public sealed class LogonDecisionTests
{
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
            manifest, false, false));
        Assert.Equal("--shell --desktop-resident", LogonDecision.ArgumentsFor(manifest));
        manifest.GameModeBoot = true;
        Assert.Equal("--boot", LogonDecision.ArgumentsFor(manifest));
    }

    [Fact]
    public void FreshLogonWithEnabledManifestLaunches()
    {
        Assert.Equal(LogonAction.Launch, LogonDecision.Decide(
            Manifest(), false, false));
    }

    [Fact]
    public void ElevateFlagRoutesToTheLinkedTokenLaunch()
    {
        Assert.Equal(LogonAction.LaunchElevated, LogonDecision.Decide(
            Manifest(elevate: true), false, false));
    }

    [Fact]
    public void DisabledManifestSkips()
    {
        Assert.Equal(LogonAction.SkipDisabled, LogonDecision.Decide(
            Manifest(false), false, false));
    }

    [Fact]
    public void MissingManifestSkips()
    {
        Assert.Equal(LogonAction.SkipNoManifest, LogonDecision.Decide(
            null, false, false));
    }

    [Fact]
    public void OneLaunchPerSessionEvenWhenEverythingElseSaysGo()
    {
        Assert.Equal(LogonAction.SkipAlreadyLaunched, LogonDecision.Decide(
            Manifest(elevate: true), true, false));
    }

    [Fact]
    public void StaleCatchUpIsSkipped()
    {
        Assert.Equal(LogonAction.SkipStale, LogonDecision.Decide(
            Manifest(), false, true));
    }

    [Fact]
    public void StaleOutranksManifestProblemsSoTheLogIsHonest()
    {
        Assert.Equal(LogonAction.SkipStale, LogonDecision.Decide(
            null, false, true));
    }
}
