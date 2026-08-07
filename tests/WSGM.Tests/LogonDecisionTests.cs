using WSGM.Core;
using WSGM.LogonService;

namespace WSGM.Tests;

public sealed class LogonDecisionTests
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    private static BootManifest Manifest(bool enabled = true, bool elevate = false)
        => new() { GameModeBoot = enabled, Elevate = elevate, ExePath = @"C:\x\WSGM.exe" };

    [Fact]
    public void FreshLogonWithEnabledManifestLaunches()
    {
        Assert.Equal(LogonAction.Launch, LogonDecision.Decide(
            Manifest(), sessionActive: true, alreadyLaunched: false, logonAge: null, StaleAfter));
    }

    [Fact]
    public void ElevateFlagRoutesToTheLinkedTokenLaunch()
    {
        Assert.Equal(LogonAction.LaunchElevated, LogonDecision.Decide(
            Manifest(elevate: true), sessionActive: true, alreadyLaunched: false, logonAge: null, StaleAfter));
    }

    [Fact]
    public void DisabledManifestSkips()
    {
        Assert.Equal(LogonAction.SkipDisabled, LogonDecision.Decide(
            Manifest(enabled: false), sessionActive: true, alreadyLaunched: false, logonAge: null, StaleAfter));
    }

    [Fact]
    public void MissingManifestSkips()
    {
        Assert.Equal(LogonAction.SkipNoManifest, LogonDecision.Decide(
            null, sessionActive: true, alreadyLaunched: false, logonAge: null, StaleAfter));
    }

    [Fact]
    public void OneLaunchPerSessionEvenWhenEverythingElseSaysGo()
    {
        Assert.Equal(LogonAction.SkipAlreadyLaunched, LogonDecision.Decide(
            Manifest(elevate: true), sessionActive: true, alreadyLaunched: true, logonAge: null, StaleAfter));
    }

    [Fact]
    public void CatchUpInsideTheWindowLaunches()
    {
        Assert.Equal(LogonAction.Launch, LogonDecision.Decide(
            Manifest(), sessionActive: true, alreadyLaunched: false,
            logonAge: TimeSpan.FromSeconds(12), StaleAfter));
    }

    [Fact]
    public void CatchUpBeyondTheWindowIsStale()
    {
        Assert.Equal(LogonAction.SkipStale, LogonDecision.Decide(
            Manifest(), sessionActive: true, alreadyLaunched: false,
            logonAge: TimeSpan.FromMinutes(5), StaleAfter));
    }

    [Fact]
    public void InactiveSessionIsStaleRegardlessOfAge()
    {
        Assert.Equal(LogonAction.SkipStale, LogonDecision.Decide(
            Manifest(), sessionActive: false, alreadyLaunched: false, logonAge: null, StaleAfter));
    }

    [Fact]
    public void StaleOutranksManifestProblemsSoTheLogIsHonest()
    {
        Assert.Equal(LogonAction.SkipStale, LogonDecision.Decide(
            null, sessionActive: false, alreadyLaunched: false, logonAge: null, StaleAfter));
    }
}
