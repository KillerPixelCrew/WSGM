using WSGM.Core;
using WSGM.Shell;
using WSGM.Tests.Fakes;
using static WSGM.Tests.Builders.PerformanceBuilders;

namespace WSGM.Tests.Shell;

/// <summary>The processor boost mode as a per-game value, carried without any device plugin.</summary>
public sealed class ApplicationCpuBoostTests
{
    [Fact]
    public async Task AGameThatDisablesBoostGetsItAndTheDesktopGetsTheOriginalBack()
    {
        FakeCpuBoostApi api = new();
        var profiles = Profiles();
        CpuBoostService cpuBoost = new(profiles, api.Owner());
        ApplicationPerformanceReconciler reconciler = new(profiles, () => null, cpuBoost);
        profiles.SetRunningApplication(new PerformanceApplicationTarget("steam:42", 42, "doom.exe"));
        Assert.True(await profiles.SetGameEnabledAsync(true));
        await profiles.SetAsync(values => values.CpuBoost = CpuBoostMode.Disabled, "CpuBoost=Disabled");

        await reconciler.ReconcileApplicationProfileAsync(profiles.Current, CancellationToken.None);

        Assert.Equal(0u, api.Values[false]);
        Assert.Equal(0u, api.Values[true]);
        Assert.Equal(CpuBoostMode.Disabled, cpuBoost.Status?.OnAc);

        // The game closes and no layer prefers a mode: what Windows held before comes back.
        profiles.SetRunningApplication(null);
        await reconciler.ReconcileApplicationProfileAsync(profiles.Current, CancellationToken.None);

        Assert.Equal(1u, api.Values[false]);
        Assert.Equal(1u, api.Values[true]);
    }

    [Fact]
    public async Task NothingPreferredAndNothingImposedNeverWritesWindows()
    {
        FakeCpuBoostApi api = new();
        var profiles = Profiles();
        CpuBoostService cpuBoost = new(profiles, api.Owner());
        ApplicationPerformanceReconciler reconciler = new(profiles, () => null, cpuBoost);

        await reconciler.ReconcileApplicationProfileAsync(profiles.Current, CancellationToken.None);

        Assert.DoesNotContain(api.Calls, call => call.StartsWith("write", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AModeChosenByHandIsWrittenAndSavedToTheLayerInForce()
    {
        FakeCpuBoostApi api = new();
        var profiles = Profiles();
        CpuBoostService cpuBoost = new(profiles, api.Owner());
        ApplicationPerformanceReconciler reconciler = new(profiles, () => null, cpuBoost);
        profiles.SetRunningApplication(new PerformanceApplicationTarget("steam:7", 7, "sonic.exe"));
        Assert.True(await profiles.SetGameEnabledAsync(true));

        Assert.True(await cpuBoost.SetFromUserAsync(CpuBoostMode.Aggressive, CancellationToken.None));

        Assert.Equal(2u, api.Values[false]);
        Assert.Equal(CpuBoostMode.Aggressive, profiles.Current.Game!.Values.CpuBoost);
        Assert.Null(profiles.Current.Config.Global.CpuBoost);
        Assert.Equal(ProfileSource.Game, profiles.Current.Layers.Value(values => values.CpuBoost).Source);
    }

    [Fact]
    public async Task AnUnsupportedSchemeRefusesTheUserWithoutSaving()
    {
        var profiles = Profiles();
        CpuBoostService cpuBoost = new(profiles, new FakeCpuBoostApi { Readable = false }.Owner());

        Assert.False(await cpuBoost.SetFromUserAsync(CpuBoostMode.Disabled, CancellationToken.None));

        Assert.Null(profiles.Current.Config.Global.CpuBoost);
        Assert.False(cpuBoost.Status?.Supported);
    }
}
