using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class PerApplicationPowerPolicyTests
{
    [Fact]
    public void AnEnabledProfilePrefersItsOwnLimit()
    {
        Assert.Equal(
            21,
            PerApplicationPowerPolicy.ResolveEffective(
                37,
                21,
                true));
    }

    [Fact]
    public void ADisabledProfileInheritsTheGlobalLimit()
    {
        // The per-game switch governs every performance value: a stored application limit is dormant
        // while its profile is off, exactly like the frame limit and overlay level.
        Assert.Equal(
            37,
            PerApplicationPowerPolicy.ResolveEffective(
                37,
                21,
                false));
    }

    [Fact]
    public void AnEnabledProfileWithNoLimitOfItsOwnInheritsTheGlobalLimit()
    {
        Assert.Equal(
            37,
            PerApplicationPowerPolicy.ResolveEffective(
                37,
                null,
                true));
    }

    [Fact]
    public void NoLimitAnywhereResolvesToNone()
    {
        Assert.Null(PerApplicationPowerPolicy.ResolveEffective(null, null, true));
    }

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
    public void VrrAnEnabledProfilePrefersItsOwnState()
    {
        Assert.True(PerApplicationVrrPolicy.ResolveEffective(
            false,
            true,
            true));
    }

    [Fact]
    public void VrrADisabledProfileInheritsTheGlobalState()
    {
        Assert.False(PerApplicationVrrPolicy.ResolveEffective(
            false,
            true,
            false));
    }

    [Fact]
    public void VrrNoStateAnywhereResolvesToNone()
    {
        Assert.Null(PerApplicationVrrPolicy.ResolveEffective(null, null, true));
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
}
