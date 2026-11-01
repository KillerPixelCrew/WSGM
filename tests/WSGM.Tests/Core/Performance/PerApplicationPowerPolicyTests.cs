using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class PerApplicationPowerPolicyTests
{
    [Fact]
    public void AResolvedLimitIsAlwaysApplied()
    {
        var decision = PerApplicationPowerPolicy.DecideOnTargetChange(
            21,
            false,
            true,
            37);

        Assert.Equal(PerAppPowerAction.Apply, decision.Action);
        Assert.Equal(21, decision.Watts);
    }

    [Fact]
    public void NoLimitResumesAutomaticControlWhenItIsOnAndAProfileHadImposedOne()
    {
        // The reported bug's AutoTDP case: a limit set in a game paused control; leaving the game
        // with no limit preferred must resume it rather than leave the game's limit latched.
        var decision = PerApplicationPowerPolicy.DecideOnTargetChange(
            null,
            true,
            true,
            37);

        Assert.Equal(PerAppPowerAction.ResumeAutomatic, decision.Action);
    }

    [Fact]
    public void NoLimitReleasesToTheCeilingWhenAutomaticControlIsOffAndAProfileHadImposedOne()
    {
        // The reported bug's non-AutoTDP case: without automatic control there is nothing to resume,
        // so the game's limit is released to the ceiling instead of leaking onto the desktop.
        var decision = PerApplicationPowerPolicy.DecideOnTargetChange(
            null,
            true,
            false,
            37);

        Assert.Equal(PerAppPowerAction.ReleaseToCeiling, decision.Action);
        Assert.Equal(37, decision.Watts);
    }

    [Fact]
    public void NoLimitAndNothingImposedLeavesTheDeviceUntouched()
    {
        // A session that never used the feature must never have its power limit written: WSGM has no
        // limit of its own to take back, and forcing the ceiling would raise power the user did not.
        var autoOn = PerApplicationPowerPolicy.DecideOnTargetChange(
            null,
            false,
            true,
            37);
        var autoOff = PerApplicationPowerPolicy.DecideOnTargetChange(
            null,
            false,
            false,
            37);

        Assert.Equal(PerAppPowerAction.Leave, autoOn.Action);
        Assert.Equal(PerAppPowerAction.Leave, autoOff.Action);
    }

    [Fact]
    public void VrrAResolvedStateIsAlwaysApplied()
    {
        var decision = PerApplicationVrrPolicy.DecideOnTargetChange(
            true,
            false);

        Assert.Equal(PerAppVrrAction.Apply, decision.Action);
        Assert.True(decision.Enabled);
    }

    [Fact]
    public void VrrNoStateReturnsToOffWhenAProfileHadImposedOne()
    {
        // The leak this prevents: a game turned VRR on; leaving it with no state preferred returns
        // to off — Steam's own default and a fixed-refresh desktop's expectation — not on.
        var decision = PerApplicationVrrPolicy.DecideOnTargetChange(
            null,
            true);

        Assert.Equal(PerAppVrrAction.Apply, decision.Action);
        Assert.False(decision.Enabled);
    }

    [Fact]
    public void VrrNoStateAndNothingImposedLeavesTheDisplayUntouched()
    {
        var decision = PerApplicationVrrPolicy.DecideOnTargetChange(
            null,
            false);

        Assert.Equal(PerAppVrrAction.Leave, decision.Action);
    }

    [Fact]
    public void CpuBoostPreferredModeIsApplied()
    {
        var decision = PerApplicationCpuBoostPolicy.DecideOnTargetChange(
            CpuBoostMode.Disabled,
            false,
            CpuBoostMode.Enabled);

        Assert.Equal(PerAppCpuBoostAction.Apply, decision.Action);
        Assert.Equal(CpuBoostMode.Disabled, decision.Mode);
    }

    [Fact]
    public void CpuBoostNoModeReturnsToTheBaselineWhenAProfileHadImposedOne()
    {
        var decision = PerApplicationCpuBoostPolicy.DecideOnTargetChange(
            null,
            true,
            CpuBoostMode.Aggressive);

        Assert.Equal(PerAppCpuBoostAction.Apply, decision.Action);
        Assert.Equal(CpuBoostMode.Aggressive, decision.Mode);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CpuBoostNoModeLeavesWindowsAloneWithoutSomethingToRestore(bool imposed, bool knownBaseline)
    {
        // Nothing imposed: nothing is WSGM's to take back. Imposed over an unknown baseline (a mode
        // WSGM does not offer): restoring would mean guessing.
        var decision = PerApplicationCpuBoostPolicy.DecideOnTargetChange(
            null,
            imposed,
            knownBaseline ? CpuBoostMode.Enabled : null);

        Assert.Equal(PerAppCpuBoostAction.Leave, decision.Action);
    }
}
