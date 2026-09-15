using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Overlay;

namespace WSGM.Tests;

public sealed class HybridCoreTests
{
    [Fact]
    public void ANonHybridMachineOffersNothing()
    {
        FakeApi api = new() { Classes = [new(0, 8, 16)] };

        HybridCoreStatus status = new HybridCores(api).Read();

        Assert.False(status.Supported);
        Assert.Empty(status.Options);
    }

    [Fact]
    public void ASchemeThatDoesNotExposeThePolicyOffersNothing()
    {
        FakeApi api = new() { Configurable = false };

        Assert.False(new HybridCores(api).Read().Supported);
    }

    [Fact]
    public void CoresAreCountedWithTheHighestEfficiencyClassAsThePerformanceOne()
    {
        // Lunar Lake reports two classes: four performance cores and four low-power efficiency ones.
        FakeApi api = new() { Classes = [new(0, 4, 4), new(1, 4, 4)] };

        HybridCoreStatus status = new HybridCores(api).Read();

        Assert.Equal(4, status.PerformanceCores);
        Assert.Equal(4, status.EfficiencyCores);
    }

    [Fact]
    public void EveryClassBelowTheTopCountsAsEfficiency()
    {
        FakeApi api = new() { Classes = [new(0, 4, 4), new(1, 8, 8), new(2, 2, 4)] };

        HybridCoreStatus status = new HybridCores(api).Read();

        Assert.Equal(2, status.PerformanceCores);
        Assert.Equal(12, status.EfficiencyCores);
    }

    [Fact]
    public void OnlyModesTheMachinePublishesAValueForAreOffered()
    {
        // A mode offered without the machine accepting its value is a control that does nothing.
        FakeApi api = new()
        {
            Policies =
            [
                HybridSchedulingPolicy.Automatic,
                HybridSchedulingPolicy.PreferPerformantProcessors,
            ],
        };

        var modes = new HybridCores(api).Read().Options.Select(option => option.Mode).ToArray();

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
            (HybridSchedulingPolicy.EfficientProcessors, HybridCoreMode.EfficiencyOnly),
        ];

        foreach ((HybridSchedulingPolicy policy, HybridCoreMode mode) in expected)
        {
            Assert.Equal(mode, HybridCores.ModeFor(new(0, policy, policy)));
        }
    }

    [Fact]
    public void AStoredPairThatDisagreesWithItselfIsNoOfferedMode()
    {
        // Ordinary threads steered one way and short-lived threads another is a placement WSGM
        // never writes, so reporting it as one of WSGM's modes would claim WSGM put it there.
        Assert.Null(HybridCores.ModeFor(new(
            0,
            HybridSchedulingPolicy.PreferPerformantProcessors,
            HybridSchedulingPolicy.PreferEfficientProcessors)));
    }

    [Fact]
    public void APolicyValueWindowsDoesNotNameIsNoOfferedMode()
        => Assert.Null(HybridCores.ModeFor(new(0, (HybridSchedulingPolicy)9, (HybridSchedulingPolicy)9)));

    [Fact]
    public void AllProcessorsIsReportedAsUnknownRatherThanAsAutomatic()
    {
        // Windows names 0 "all processors" and 5 "automatic"; they are different placements and
        // only the second is what WSGM's Automatic writes.
        Assert.Null(HybridCores.ModeFor(new(
            0, HybridSchedulingPolicy.AllProcessors, HybridSchedulingPolicy.AllProcessors)));
    }

    [Fact]
    public void TheEffectiveModeIsReadSeparatelyForEachPowerSource()
    {
        FakeApi api = new();
        api.States[false] = new(0, HybridSchedulingPolicy.PreferPerformantProcessors,
            HybridSchedulingPolicy.PreferPerformantProcessors);
        api.States[true] = new(4, HybridSchedulingPolicy.PreferEfficientProcessors,
            HybridSchedulingPolicy.PreferEfficientProcessors);

        HybridCoreStatus status = new HybridCores(api).Read();

        Assert.Equal(HybridCoreMode.PreferPerformance, status.OnAc);
        Assert.Equal(HybridCoreMode.PreferEfficiency, status.OnBattery);
    }

    [Fact]
    public void ApplyingAModeWritesBothPowerSourcesAndActivatesTheScheme()
    {
        FakeApi api = new();

        new HybridCores(api).Apply(HybridCoreMode.PerformanceOnly);

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
        FakeApi api = new();
        api.States[false] = api.States[false] with { HeterogeneousPolicy = 2 };
        api.States[true] = api.States[true] with { HeterogeneousPolicy = 4 };

        new HybridCores(api).Apply(HybridCoreMode.PreferEfficiency);

        Assert.Equal(2u, api.States[false].HeterogeneousPolicy);
        Assert.Equal(4u, api.States[true].HeterogeneousPolicy);
    }

    [Fact]
    public void AWriteThatDoesNotReadBackAsTheRequestedModeIsAFailure()
    {
        FakeApi api = new() { IgnoreWrites = true };

        var error = Assert.Throws<InvalidOperationException>(
            () => new HybridCores(api).Apply(HybridCoreMode.EfficiencyOnly));
        Assert.Contains("did not confirm", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSchemeIsActivatedBeforeTheConfirmingReadRatherThanAfter()
    {
        // Processor policy takes effect on activation, so a readback taken first would confirm a
        // value that is stored but not applied.
        FakeApi api = new();

        new HybridCores(api).Apply(HybridCoreMode.PreferPerformance);

        int refresh = api.Calls.IndexOf("refresh");
        Assert.True(refresh >= 0);
        Assert.True(api.Calls.LastIndexOf("write") < refresh);
        Assert.True(api.Calls.LastIndexOf("read") > refresh);
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
        => Assert.Contains(
            "nothing to choose",
            HybridCoreSelection.Describe(new(false, 0, 0, [], null, null)),
            StringComparison.Ordinal);

    [Fact]
    public void TheCoreCountsAreReportedInTheDescription()
        => Assert.Contains(
            "4 performance and 4 efficiency cores",
            HybridCoreSelection.Describe(new(true, 4, 4, [], HybridCoreMode.Automatic, HybridCoreMode.Automatic)),
            StringComparison.Ordinal);

    [Fact]
    public async Task TheSteamDropdownAndTheOverlayShareOneIdVocabularyAndOnePolicy()
    {
        FakeApi api = new();
        var qam = new WSGM.Shell.NativeQamHybridCoreService(new HybridCores(api));

        var state = await qam.ReadAsync();

        Assert.True(state!.Available);
        Assert.Equal("automatic", state.Current);
        Assert.Equal(
            ["automatic", "prefer-performance", "prefer-efficiency", "performance-only", "efficiency-only"],
            state.Options.Select(option => option.Id));

        Assert.True((await qam.SetHybridCoresAsync("prefer-performance", default)).Succeeded);
        Assert.Equal("prefer-performance", (await qam.ReadAsync())!.Current);
        Assert.Equal(HybridCoreMode.PreferPerformance, new HybridCores(api).Read().OnAc);
    }

    [Fact]
    public async Task TheSteamDropdownRefusesAnIdItNeverPublished()
    {
        FakeApi api = new();
        var qam = new WSGM.Shell.NativeQamHybridCoreService(new HybridCores(api));
        await qam.ReadAsync();

        Assert.False((await qam.SetHybridCoresAsync("turbo", default)).Succeeded);
        Assert.Equal(0, api.Refreshes);
    }

    [Fact]
    public async Task AnUnconfirmedSteamWriteBlocksTheNextOneUntilTheStateIsReRead()
    {
        // The same rule the power-profile row follows: after a write Windows did not confirm, WSGM
        // does not know what is applied, and a second write on top of that is a guess.
        FakeApi api = new() { IgnoreWrites = true };
        var qam = new WSGM.Shell.NativeQamHybridCoreService(new HybridCores(api));
        await qam.ReadAsync();

        Assert.False((await qam.SetHybridCoresAsync("efficiency-only", default)).Succeeded);
        SteamUiCommandResult second = await qam.SetHybridCoresAsync("efficiency-only", default);

        Assert.False(second.Succeeded);
        Assert.Contains("must be refreshed", second.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonHybridMachinePublishesTheRowAsUnavailableRatherThanWithholdingIt()
    {
        // A silently absent control cannot be told apart from a broken one.
        FakeApi api = new() { Classes = [new(0, 8, 16)] };
        var qam = new WSGM.Shell.NativeQamHybridCoreService(new HybridCores(api));

        var state = await qam.ReadAsync();

        Assert.False(state!.Available);
        Assert.Empty(state.Options);
        Assert.Contains("nothing to choose", state.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOfferedModeHasAnIdThatRoundTripsAndFitsTheBridgeRules()
    {
        foreach (HybridCoreOption option in new HybridCores(new FakeApi()).Read().Options)
        {
            string id = HybridCores.IdFor(option.Mode);
            Assert.Matches("^[A-Za-z0-9._-]{1,64}$", id);
            Assert.Equal(option.Mode, HybridCores.ModeForId(id));
        }
    }

    private sealed class FakeApi : IHybridCoreApi
    {
        private static readonly Guid Scheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");

        internal IReadOnlyList<HybridCoreClass> Classes { get; set; } = [new(0, 4, 4), new(1, 4, 4)];

        internal bool Configurable { get; set; } = true;

        internal bool IgnoreWrites { get; set; }

        internal IReadOnlyList<HybridSchedulingPolicy> Policies { get; set; } =
        [
            HybridSchedulingPolicy.AllProcessors,
            HybridSchedulingPolicy.PerformantProcessors,
            HybridSchedulingPolicy.PreferPerformantProcessors,
            HybridSchedulingPolicy.EfficientProcessors,
            HybridSchedulingPolicy.PreferEfficientProcessors,
            HybridSchedulingPolicy.Automatic,
        ];

        internal Dictionary<bool, HybridCoreState> States { get; } = new()
        {
            [false] = new(0, HybridSchedulingPolicy.Automatic, HybridSchedulingPolicy.Automatic),
            [true] = new(0, HybridSchedulingPolicy.Automatic, HybridSchedulingPolicy.Automatic),
        };

        internal List<string> Calls { get; } = [];

        internal int Refreshes { get; private set; }

        public Guid ReadActiveScheme() => Scheme;

        public HybridCoreSupport Query(Guid scheme)
            => new(Classes, Configurable, [0, 1, 2, 3, 4], Policies, Policies);

        public HybridCoreState Read(Guid scheme, bool onBattery)
        {
            Calls.Add("read");
            return States[onBattery];
        }

        public void Write(Guid scheme, bool onBattery, HybridCoreState state)
        {
            Calls.Add("write");
            if (!IgnoreWrites) { States[onBattery] = state; }
        }

        public void RefreshActiveScheme()
        {
            Calls.Add("refresh");
            Refreshes++;
        }
    }
}
