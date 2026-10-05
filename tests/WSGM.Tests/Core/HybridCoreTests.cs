using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Core;

public sealed class HybridCoreTests
{
    [Fact]
    public async Task AKnownModeNotInThePublishedOptionsIsRefusedWithoutNativeAccess()
    {
        FakeHybridCoreApi api = new() { Policies = [HybridSchedulingPolicy.Automatic] };
        NativeQamHybridCoreService qam = new(api.Owner());
        await qam.ReadAsync();
        api.Calls.Clear();

        Assert.False((await qam.SetHybridCoresAsync("efficiency-only", CancellationToken.None)).Succeeded);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task AnAcceptedSteamSelectionPublishesTheWrittenModeWithoutQueryingAgain()
    {
        FakeHybridCoreApi api = new() { IgnoreWrites = true };
        NativeQamHybridCoreService qam = new(api.Owner());
        await qam.ReadAsync();
        api.Calls.Clear();

        Assert.True((await qam.SetHybridCoresAsync("efficiency-only", CancellationToken.None)).Succeeded);
        var published = await qam.ReadAsync();

        Assert.Equal("efficiency-only", published!.Current);
        Assert.Equal(["read", "read", "write", "write", "refresh"], api.Calls);
    }

    [Fact]
    public async Task OverlayApplyPublishesTheWrittenModeWithoutQueryingAgain()
    {
        FakeHybridCoreApi api = new() { IgnoreWrites = true };
        using HybridCoreSelection selection = new(api.Owner());
        await selection.RefreshAsync();
        api.Calls.Clear();

        await selection.ApplyAsync(HybridCoreMode.EfficiencyOnly);

        Assert.Equal(HybridCoreMode.EfficiencyOnly, selection.Status.OnAc);
        Assert.Equal(HybridCoreMode.EfficiencyOnly, selection.Status.OnBattery);
        Assert.Equal(["read", "read", "write", "write", "refresh"], api.Calls);
    }

    [Fact]
    public void ANonHybridMachineOffersNothing()
    {
        FakeHybridCoreApi api = new() { Classes = [new HybridCoreClass(0, 8, 16)] };

        var status = api.Owner().Read();

        Assert.False(status.Supported);
        Assert.Empty(status.Options);
    }

    [Fact]
    public void ASchemeThatDoesNotExposeThePolicyOffersNothing()
    {
        FakeHybridCoreApi api = new() { Configurable = false };

        Assert.False(api.Owner().Read().Supported);
    }

    [Fact]
    public void CoresAreCountedWithTheHighestEfficiencyClassAsThePerformanceOne()
    {
        // Lunar Lake reports two classes: four performance cores and four low-power efficiency ones.
        FakeHybridCoreApi api = new() { Classes = [new HybridCoreClass(0, 4, 4), new HybridCoreClass(1, 4, 4)] };

        var status = api.Owner().Read();

        Assert.Equal(4, status.PerformanceCores);
        Assert.Equal(4, status.EfficiencyCores);
    }

    [Fact]
    public void EveryClassBelowTheTopCountsAsEfficiency()
    {
        FakeHybridCoreApi api = new()
            { Classes = [new HybridCoreClass(0, 4, 4), new HybridCoreClass(1, 8, 8), new HybridCoreClass(2, 2, 4)] };

        var status = api.Owner().Read();

        Assert.Equal(2, status.PerformanceCores);
        Assert.Equal(12, status.EfficiencyCores);
    }

    [Fact]
    public void OnlyModesTheMachinePublishesAValueForAreOffered()
    {
        // A mode offered without the machine accepting its value is a control that does nothing.
        FakeHybridCoreApi api = new()
        {
            Policies =
            [
                HybridSchedulingPolicy.Automatic,
                HybridSchedulingPolicy.PreferPerformantProcessors
            ]
        };

        var modes = api.Owner().Read().Options.Select(option => option.Mode).ToArray();

        Assert.Equal([HybridCoreMode.Automatic, HybridCoreMode.PreferPerformance], modes);
    }

    [Fact]
    public void EachOfferedModeReadsBackFromItsStoredPolicy()
    {
        (HybridSchedulingPolicy Policy, HybridCoreMode Mode)[] expected =
        [
            (HybridSchedulingPolicy.Automatic, HybridCoreMode.Automatic),
            (HybridSchedulingPolicy.PreferPerformantProcessors, HybridCoreMode.PreferPerformance),
            (HybridSchedulingPolicy.PreferEfficientProcessors, HybridCoreMode.PreferEfficiency),
            (HybridSchedulingPolicy.PerformantProcessors, HybridCoreMode.PerformanceOnly),
            (HybridSchedulingPolicy.EfficientProcessors, HybridCoreMode.EfficiencyOnly)
        ];

        foreach (var (policy, mode) in expected)
        {
            Assert.Equal(mode, HybridCores.ModeFor(new HybridCoreState(0, policy, policy)));
        }
    }

    [Fact]
    public void AStoredPairThatDisagreesWithItselfIsNoOfferedMode()
    {
        // Ordinary threads steered one way and short-lived threads another is a placement WSGM
        // never writes, so reporting it as one of WSGM's modes would claim WSGM put it there.
        Assert.Null(HybridCores.ModeFor(new HybridCoreState(
            0,
            HybridSchedulingPolicy.PreferPerformantProcessors,
            HybridSchedulingPolicy.PreferEfficientProcessors)));
    }

    [Fact]
    public void APolicyValueWindowsDoesNotNameIsNoOfferedMode()
    {
        Assert.Null(HybridCores.ModeFor(new HybridCoreState(0, (HybridSchedulingPolicy)9, (HybridSchedulingPolicy)9)));
    }

    [Fact]
    public void AllProcessorsIsReportedAsUnknownRatherThanAsAutomatic()
    {
        // Windows names 0 "all processors" and 5 "automatic"; they are different placements and
        // only the second is what WSGM's Automatic writes.
        Assert.Null(HybridCores.ModeFor(new HybridCoreState(
            0, HybridSchedulingPolicy.AllProcessors, HybridSchedulingPolicy.AllProcessors)));
    }

    [Fact]
    public void TheEffectiveModeIsReadSeparatelyForEachPowerSource()
    {
        FakeHybridCoreApi api = new()
        {
            States =
            {
                [false] = new HybridCoreState(0, HybridSchedulingPolicy.PreferPerformantProcessors,
                    HybridSchedulingPolicy.PreferPerformantProcessors),
                [true] = new HybridCoreState(4, HybridSchedulingPolicy.PreferEfficientProcessors,
                    HybridSchedulingPolicy.PreferEfficientProcessors)
            }
        };

        var status = api.Owner().Read();

        Assert.Equal(HybridCoreMode.PreferPerformance, status.OnAc);
        Assert.Equal(HybridCoreMode.PreferEfficiency, status.OnBattery);
    }

    [Fact]
    public void ApplyingAModeWritesBothPowerSourcesAndActivatesTheScheme()
    {
        FakeHybridCoreApi api = new();

        api.Owner().Apply(HybridCoreMode.PerformanceOnly);

        Assert.Equal(HybridSchedulingPolicy.PerformantProcessors, api.States[false].Threads);
        Assert.Equal(HybridSchedulingPolicy.PerformantProcessors, api.States[false].ShortThreads);
        Assert.Equal(HybridSchedulingPolicy.PerformantProcessors, api.States[true].Threads);
        Assert.Equal(HybridSchedulingPolicy.PerformantProcessors, api.States[true].ShortThreads);
        Assert.Equal(1, api.Refreshes);
    }

    [Fact]
    public void ApplyingAModeCarriesTheUnnamedHeterogeneousPolicyThrough()
    {
        // Windows enumerates that setting only as "use heterogeneous policy N". WSGM restores what
        // was there rather than choosing a value whose meaning nobody publishes.
        FakeHybridCoreApi api = new();
        api.States[false] = api.States[false] with { HeterogeneousPolicy = 2 };
        api.States[true] = api.States[true] with { HeterogeneousPolicy = 4 };

        api.Owner().Apply(HybridCoreMode.PreferEfficiency);

        Assert.Equal(2u, api.States[false].HeterogeneousPolicy);
        Assert.Equal(4u, api.States[true].HeterogeneousPolicy);
    }

    [Fact]
    public void AnAcceptedWriteDoesNotDependOnReadback()
    {
        FakeHybridCoreApi api = new() { IgnoreWrites = true };

        api.Owner().Apply(HybridCoreMode.EfficiencyOnly);
        Assert.Equal(["read", "read", "write", "write", "refresh"], api.Calls);
    }

    [Fact]
    public void TheSchemeIsActivatedAfterBothWritesWithNoFurtherRead()
    {
        // Capture both unrelated settings before writing, then activate the new policy.
        FakeHybridCoreApi api = new();

        api.Owner().Apply(HybridCoreMode.PreferPerformance);

        var refresh = api.Calls.IndexOf("refresh");
        Assert.True(refresh >= 0);
        Assert.True(api.Calls.LastIndexOf("write") < refresh);
        Assert.True(api.Calls.LastIndexOf("read") < api.Calls.IndexOf("write"));
    }

    [Fact]
    public void TheDescriptionNamesThePowerSourcesOnlyWhenTheyDisagree()
    {
        HybridCoreStatus same = new(true, 4, 4, [], HybridCoreMode.Automatic, HybridCoreMode.Automatic);
        Assert.Contains("both battery and plugged in", HybridCoreSelection.Describe(same), StringComparison.Ordinal);

        HybridCoreStatus split = new(
            true, 4, 4, [], HybridCoreMode.PreferPerformance, HybridCoreMode.PreferEfficiency);
        Assert.Contains("currently differ", HybridCoreSelection.Describe(split), StringComparison.Ordinal);
    }

    [Fact]
    public void ADescriptionNeverClaimsWsgmSetAPlacementItDoesNotOffer()
    {
        HybridCoreStatus foreign = new(true, 4, 4, [], null, HybridCoreMode.Automatic);

        Assert.Contains("was not set by WSGM", HybridCoreSelection.Describe(foreign), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsupportedMachineSaysThereIsNothingToChoose()
    {
        Assert.Contains(
            "nothing to choose",
            HybridCoreSelection.Describe(new HybridCoreStatus(false, 0, 0, [], null, null)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheCoreCountsAreReportedInTheDescription()
    {
        Assert.Contains(
            "4 performance and 4 efficiency cores",
            HybridCoreSelection.Describe(new HybridCoreStatus(true, 4, 4, [], HybridCoreMode.Automatic,
                HybridCoreMode.Automatic)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSteamDropdownAndTheOverlayShareOneIdVocabularyAndOnePolicy()
    {
        FakeHybridCoreApi api = new();
        var qam = new NativeQamHybridCoreService(api.Owner());

        var state = await qam.ReadAsync();

        Assert.True(state!.Available);
        Assert.Equal("automatic", state.Current);
        Assert.Equal(
            ["automatic", "prefer-performance", "prefer-efficiency", "performance-only", "efficiency-only"],
            state.Options.Select(option => option.Id));

        Assert.True((await qam.SetHybridCoresAsync("prefer-performance", CancellationToken.None)).Succeeded);
        Assert.Equal("prefer-performance", (await qam.ReadAsync())!.Current);
        Assert.Equal(HybridCoreMode.PreferPerformance, api.Owner().Read().OnAc);
    }

    [Fact]
    public async Task TheSteamDropdownRefusesAnIdItNeverPublished()
    {
        FakeHybridCoreApi api = new();
        var qam = new NativeQamHybridCoreService(api.Owner());
        await qam.ReadAsync();

        Assert.False((await qam.SetHybridCoresAsync("turbo", CancellationToken.None)).Succeeded);
        Assert.Equal(0, api.Refreshes);
    }

    [Fact]
    public async Task AFailedSteamWriteDoesNotBlockTheNextExplicitSelection()
    {
        FakeHybridCoreApi api = new() { NextWriteFailure = new IOException("write failed") };
        var qam = new NativeQamHybridCoreService(api.Owner());
        await qam.ReadAsync();

        Assert.False((await qam.SetHybridCoresAsync("efficiency-only", CancellationToken.None)).Succeeded);
        var second = await qam.SetHybridCoresAsync("efficiency-only", CancellationToken.None);

        Assert.True(second.Succeeded);
        var reads = api.Calls.Count(call => call == "read");
        Assert.Equal("efficiency-only", (await qam.ReadAsync())!.Current);
        Assert.Equal(reads, api.Calls.Count(call => call == "read"));
    }

    [Fact]
    public async Task ANonHybridMachinePublishesTheRowAsUnavailableRatherThanWithholdingIt()
    {
        // A silently absent control cannot be told apart from a broken one.
        FakeHybridCoreApi api = new() { Classes = [new HybridCoreClass(0, 8, 16)] };
        var qam = new NativeQamHybridCoreService(api.Owner());

        var state = await qam.ReadAsync();

        Assert.False(state!.Available);
        Assert.Empty(state.Options);
        Assert.Contains("nothing to choose", state.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOfferedModeHasAnIdThatRoundTripsAndFitsTheBridgeRules()
    {
        foreach (var option in new FakeHybridCoreApi().Owner().Read().Options)
        {
            var id = HybridCores.IdFor(option.Mode);
            Assert.Matches("^[A-Za-z0-9._-]{1,64}$", id);
            Assert.Equal(option.Mode, HybridCores.ModeForId(id));
        }
    }
}
