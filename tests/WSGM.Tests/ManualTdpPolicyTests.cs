using WSGM.Core;

namespace WSGM.Tests;

public sealed class ManualTdpPolicyTests
{
    [Fact]
    public void ManualEditChangesOnlyTheSelectedModesTarget()
    {
        ManualTdpProfile profile = new(true, 20, 18, 28);
        profile = ManualTdpPolicy.WithTarget(profile, 25);
        Assert.Equal(new(true, 25, 18, 28), profile);
        profile = ManualTdpPolicy.WithTarget(profile with { Unified = false }, 16);
        Assert.Equal(new(false, 25, 16, 28), profile);
    }

    [Fact]
    public void PerGameSwitchPreservesIndependentUnifiedAndAdvancedPreferences()
    {
        PerformanceConfig global = new() { TdpWatts = 30, ManualTdp = new(true, 20, 25, 35) };
        PerformanceApplicationConfig app = new() { ManualTdp = new(false, 15, 18, 28) };
        Assert.Equal((20, true), ManualTdpPolicy.ResolveTarget(global, app, false));
        Assert.Equal((18, false), ManualTdpPolicy.ResolveTarget(global, app, true));
        app.ManualTdp = app.ManualTdp with { Unified = true };
        Assert.Equal((15, true), ManualTdpPolicy.ResolveTarget(global, app, true));
        app.ManualTdp = app.ManualTdp with { Unified = false };
        Assert.Equal((18, false), ManualTdpPolicy.ResolveTarget(global, app, true));
        Assert.Equal(28, app.ManualTdp.BoostWatts);
    }

    [Fact]
    public void MissingTargetDoesNotInventAWattageAndExistingProfilesKeepTheirMeaning()
    {
        PerformanceConfig global = new() { TdpWatts = 30 };
        Assert.Equal((30, false), ManualTdpPolicy.ResolveTarget(global, null, false));
        global.ManualTdp = new(true, null, 18, 25);
        Assert.Null(ManualTdpPolicy.ResolveTarget(global, null, false).Watts);
    }
}
