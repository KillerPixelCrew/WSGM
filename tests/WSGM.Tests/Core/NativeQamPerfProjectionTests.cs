using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class NativeQamPerfProjectionTests
{
    private static NativeQamPerfSupport Support(
        bool vrr = false,
        bool refreshSelectable = false,
        int[]? options = null)
    {
        return new NativeQamPerfSupport(options ?? [30, 60, 120], vrr, refreshSelectable, 30, 120);
    }

    private static string Serialize(SteamPerformanceState state)
    {
        return SteamPerformanceSurface.Serialize(state).GetRawText();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void EveryAdvertisedControlAlsoCarriesAValue(bool refreshSelectable, bool vrr)
    {
        // THE rule this file exists to protect. Hiding a control by omitting its limits field is
        // safe; advertising it in limits and omitting its settings value is not — Valve's component
        // renders, finds no value and throws inside Steam's error boundary, which took the whole
        // Performance tab down on 2026-08-30.
        //
        // Values are deliberately all null here: an untouched profile is exactly the case that
        // shipped broken.
        var state = NativeQamProjection(
            PerformanceValues.Empty,
            Support(vrr, refreshSelectable));

        Assert.Equal(state.Limits?.FpsLimitOptions is not null, state.PerApp?.FpsLimit is not null);
        Assert.Equal(
            state.Limits?.FpsLimitOptions is not null,
            state.PerApp?.IsFpsLimitEnabled is not null);
        Assert.Equal(
            state.Limits?.IsManualDisplayRefreshRateAvailable is not null,
            state.PerApp?.DisplayRefreshManualHz is not null);
        Assert.Equal(
            state.Limits?.IsVrrSupported is not null,
            state.PerApp?.IsVrrEnabled is not null);
        // Always mounted, so it always needs a number.
        Assert.NotNull(state.Global?.PerfOverlayLevel);
    }

    private static SteamPerformanceState NativeQamProjection(
        PerformanceValues values,
        NativeQamPerfSupport support)
    {
        return NativeQamPerfProjection.Project(
            values,
            support,
            null,
            false,
            false,
            null,
            support.CurrentRefreshRateHz);
    }

    [Fact]
    public void UnsupportedControlsAreOmittedEntirelySoValvesWrapperRendersNothing()
    {
        // Hiding is the safety property: availability is read straight out of this state, so an
        // absent field is an absent control. A present-but-false field is a visible dead control.
        var json = Serialize(NativeQamPerfProjection.Project(
            new PerformanceValues(60, 1),
            Support(),
            42,
            true,
            false,
            null,
            null));

        Assert.DoesNotContain("is_vrr_supported", json);
        Assert.DoesNotContain("is_vrr_enabled", json);
        Assert.DoesNotContain("display_refresh_manual_hz", json);
        Assert.DoesNotContain("is_manual_display_refresh_rate_available", json);
    }

    [Fact]
    public void SupportedControlsUseValvesOwnFieldNames()
    {
        var json = Serialize(NativeQamPerfProjection.Project(
            new PerformanceValues(60, 2),
            Support(true, true),
            42,
            true,
            true,
            true,
            120));

        // A renamed field is silently a missing control, so the wire names are asserted directly.
        Assert.Contains("\"fps_limit_options\":[30,60,120]", json);
        Assert.Contains("\"is_vrr_supported\":true", json);
        Assert.Contains("\"is_vrr_enabled\":true", json);
        Assert.Contains("\"display_refresh_manual_hz\":120", json);
        // Notch 2 travels as Valve's enum value Basic=1 (see SteamOverlayLevelWire).
        Assert.Contains("\"perf_overlay_level\":1", json);
        Assert.Contains("\"currentGameId\":\"42\"", json);
    }

    [Fact]
    public void FrameLimitOptionsAreDeduplicatedAndOrderedBecauseTheyAreTheSlidersNotches()
    {
        var state = NativeQamPerfProjection.Project(
            PerformanceValues.Empty,
            Support(options: [120, 30, 60, 30, 0]),
            null,
            false,
            false,
            null,
            null);

        Assert.Equal([30, 60, 120], state.Limits?.FpsLimitOptions);
    }

    [Fact]
    public void AnUnsetFrameLimitSitsAtTheHighestCapAndReadsAsOff()
    {
        var state = NativeQamPerfProjection.Project(
            PerformanceValues.Empty,
            Support(options: [120, 30, 60, 0]),
            null,
            false,
            false,
            null,
            null);

        Assert.Equal(120, state.PerApp?.FpsLimit);
        Assert.False(state.PerApp?.IsFpsLimitEnabled);
    }

    [Fact]
    public void NoFrameLimitOptionsHidesTheSliderRatherThanShowingAnEmptyOne()
    {
        var state = NativeQamPerfProjection.Project(
            PerformanceValues.Empty,
            Support(options: []),
            null,
            false,
            false,
            null,
            null);

        Assert.Null(state.Limits?.FpsLimitOptions);
    }

    [Fact]
    public void AnActiveProfileMatchesTheRunningGameOnlyWhenPerGameIsOn()
    {
        // Steam decides the per-game profile is in use by comparing the two ids, so this pair is
        // the whole of that decision.
        var perGame = NativeQamPerfProjection.Project(
            new PerformanceValues(60, null),
            Support(),
            42,
            true,
            false,
            null,
            null);
        Assert.Equal("42", perGame.CurrentGameId);
        Assert.Equal("42", perGame.ActiveProfileGameId);

        var global = NativeQamPerfProjection.Project(
            new PerformanceValues(60, null),
            Support(),
            42,
            false,
            false,
            null,
            null);

        Assert.Equal("42", global.CurrentGameId);
        // 769 is the Steam client's own pseudo-app, Valve's vocabulary for "default settings";
        // publishing "0" made the header look up a game that does not exist.
        Assert.Equal("769", global.ActiveProfileGameId);
    }

    [Fact]
    public void AForegroundOnlyIdentityIsPresentedAsTheGlobalProfile()
    {
        // The foreground supplies an executable, never an AppID, and Valve's per-game header is
        // built entirely from one. Claiming an id WSGM does not have would name the wrong game.
        var state = NativeQamPerfProjection.Project(
            new PerformanceValues(60, null),
            Support(),
            null,
            true,
            false,
            null,
            null);

        Assert.Equal("769", state.CurrentGameId);
        Assert.Equal("769", state.ActiveProfileGameId);
        Assert.Null(state.PerApp?.IsGamePerfProfileEnabled);
        // The cap still applies; only its presentation as a named game profile is withheld.
        Assert.Equal(60, state.PerApp?.FpsLimit);
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(null, false)]
    public void TheCapAndItsEnabledFlagAgree(int? cap, bool expected)
    {
        // Steam draws the slider from the cap and its on/off state from the flag; disagreeing
        // renders a slider sitting at a value it reports as off.
        var state = NativeQamPerfProjection.Project(
            new PerformanceValues(cap, null),
            Support(),
            null,
            false,
            false,
            null,
            null);

        Assert.Equal(expected, state.PerApp?.IsFpsLimitEnabled);
    }
}
