using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>What the device can currently back, as far as the performance panel is concerned.</summary>
/// <param name="FrameLimitOptions">Frame caps to offer, or empty to hide the slider.</param>
/// <param name="VariableRefreshRateSupported">Whether the panel supports VRR.</param>
/// <param name="VariableRefreshRateEnabled">
/// Whether VRR is on now, read from the same capability that reports support so the toggle cannot
/// show a state the device disagrees with.
/// </param>
/// <param name="RefreshRatesSelectable">
/// Whether the user may choose a refresh rate by hand. False under the pairing strategies, where
/// WSGM owns the refresh rate and a manual row would fight it.
/// </param>
/// <param name="RefreshRateMinHz">Lowest selectable refresh rate.</param>
/// <param name="RefreshRateMaxHz">Highest selectable refresh rate.</param>
/// <param name="CurrentRefreshRateHz">
/// The rate in force, which the manual refresh row needs a concrete value for. Null only when that
/// row is not offered.
/// </param>
/// <param name="RefreshForCap">
/// The refresh rate each cap will be presented at, keyed by cap. Empty under
/// <c>FrameLimitOnly</c>, where the cap changes no display state and there is nothing to name.
/// <para>
/// Sent as a map rather than as a rule the injected half re-derives: the pairing policy is one
/// decision and it belongs in one place. The slider reads it to label a cap the way SteamOS does —
/// "60 FPS (60 Hz)" — while the user is still dragging, before anything has been applied.
/// </para>
/// </param>
/// <param name="RefreshRates">
/// Every rate the display actually accepted, ascending. Windows takes a mode or it does not —
/// there is no continuum to slide along — so the unified row's refresh mode is NOTCHED to exactly
/// these, unlike its frame-cap mode, where the limiter really does hold any integer.
/// </param>
internal readonly record struct NativeQamPerfSupport(
    IReadOnlyList<int> FrameLimitOptions,
    bool VariableRefreshRateSupported,
    bool RefreshRatesSelectable,
    int? RefreshRateMinHz,
    int? RefreshRateMaxHz,
    bool VariableRefreshRateEnabled = false,
    int? CurrentRefreshRateHz = null,
    IReadOnlyDictionary<int, int>? RefreshForCap = null,
    IReadOnlyList<int>? RefreshRates = null);

/// <summary>Builds the performance state from what WSGM knows, supplying only backed fields.</summary>
/// <remarks>
/// The state's shape, its field names and the rules about which fields hide which controls are the
/// toolkit's (<see cref="SteamPerformanceState"/>). This is WSGM's policy about what to put in it.
/// </remarks>
internal static class NativeQamPerfProjection
{
    /// <summary>Projects WSGM's performance state into Valve's state message shape.</summary>
    /// <param name="values">The resolved frame limit and overlay level for the active profile.</param>
    /// <param name="support">What the device can back.</param>
    /// <param name="steamAppId">The running Steam AppID, or null when none is running.</param>
    /// <param name="perApplicationProfileEnabled">
    /// Whether the running application keeps its own profile.
    /// </param>
    /// <param name="advancedSettingsEnabled">Whether the advanced rows are shown.</param>
    /// <param name="variableRefreshRateEnabled">Current VRR state, or null when unsupported.</param>
    /// <param name="refreshRateHz">Current refresh rate, or null when WSGM owns it.</param>
    /// <returns>The state to publish to the injected gate.</returns>
    /// <remarks>
    /// Pure, and the only place that decides what the panel shows. A field is supplied when WSGM can
    /// both report and honour it; anything else is left null so the control does not render at all,
    /// which is safer than rendering a control whose writes go nowhere.
    /// </remarks>
    internal static SteamPerformanceState Project(
        PerformanceValues values,
        NativeQamPerfSupport support,
        uint? steamAppId,
        bool perApplicationProfileEnabled,
        bool advancedSettingsEnabled,
        bool? variableRefreshRateEnabled,
        int? refreshRateHz)
    {
        // A foreground-only identity has no AppID, and Steam's per-game header is built entirely
        // from one. The profile still applies — it is simply presented as the global one, because
        // claiming an AppID WSGM does not have would put the wrong game's name in Valve's header.
        string gameId = steamAppId is { } appId ? appId.ToString() : SteamPerformanceState.NoGame;
        bool perGame = perApplicationProfileEnabled && gameId != SteamPerformanceState.NoGame;

        IReadOnlyList<int>? frameLimitOptions = support.FrameLimitOptions.Count > 0
            ? [.. support.FrameLimitOptions.Where(option => option > 0).Distinct().Order()]
            : null;
        int? frameLimit = frameLimitOptions is not null
            ? values.FrameLimit ?? LowestOption(support.FrameLimitOptions)
            : null;
        bool? frameLimitEnabled = frameLimitOptions is not null ? values.FrameLimit is > 0 : null;
        int? manualRefreshHz = support.RefreshRatesSelectable
            ? refreshRateHz ?? support.RefreshRateMaxHz ?? 0
            : null;

        return new SteamPerformanceState
        {
            Limits = new SteamPerformanceLimits
            {
                // Both twins carry the same values; see the external-twin rule on
                // SteamPerformanceLimits.FpsLimitOptionsExternal.
                FpsLimitOptions = frameLimitOptions,
                FpsLimitOptionsExternal = frameLimitOptions,
                IsVrrSupported = support.VariableRefreshRateSupported ? true : null,
                IsManualDisplayRefreshRateAvailable = support.RefreshRatesSelectable ? true : null,
                DisplayRefreshManualHzMin = support.RefreshRatesSelectable
                    ? support.RefreshRateMinHz
                    : null,
                DisplayRefreshManualHzMax = support.RefreshRatesSelectable
                    ? support.RefreshRateMaxHz
                    : null,
                DisplayExternalRefreshManualHzMin = support.RefreshRatesSelectable
                    ? support.RefreshRateMinHz
                    : null,
                DisplayExternalRefreshManualHzMax = support.RefreshRatesSelectable
                    ? support.RefreshRateMaxHz
                    : null,
            },
            Global = new SteamPerformanceGlobalSettings
            {
                // Always a number, and always one of the five the selector knows — see the field's
                // own remarks for the crash an absent or out-of-range value causes. The wire value
                // is Valve's enum, not the notch WSGM's levels are defined on.
                PerfOverlayLevel = SteamOverlayLevelWire.ToSteam(
                    Math.Clamp(values.OverlayLevel ?? 0, 0, SteamOverlayLevelWire.MaximumNotch)),
                IsAdvancedSettingsEnabled = advancedSettingsEnabled,
                AllowExternalDisplayRefreshControl = support.RefreshRatesSelectable ? true : null,
            },
            PerApp = new SteamPerformanceApplicationSettings
            {
                // LIMITS AND SETTINGS ARE A PAIR, and getting this wrong crashed the whole
                // Performance tab on 2026-08-30. Every field here is supplied exactly when the
                // limits field that reveals its control is, and carries a concrete value: the
                // lowest offered notch when no cap is set, never 0, because zero is filtered out of
                // the options above and "off" is carried by the flag below.
                //
                // The `_external` twins follow the rule on FpsLimitOptionsExternal.
                FpsLimit = frameLimit,
                FpsLimitExternal = frameLimit,
                // Steam draws the cap and its on/off state from two fields. Without the flag the
                // slider renders at the cap but reads as disabled, so an unset cap is off and any
                // cap at all is on.
                IsFpsLimitEnabled = frameLimitEnabled,
                IsVrrEnabled = support.VariableRefreshRateSupported
                    ? variableRefreshRateEnabled ?? false
                    : null,
                DisplayRefreshManualHz = manualRefreshHz,
                DisplayExternalRefreshManualHz = manualRefreshHz,
                IsGamePerfProfileEnabled = gameId == SteamPerformanceState.NoGame
                    ? null
                    : perApplicationProfileEnabled,
            },
            CurrentGameId = gameId,
            ActiveProfileGameId = perGame ? gameId : SteamPerformanceState.NoGame,
        };
    }

    /// <summary>The lowest cap actually offered, or zero when none is.</summary>
    /// <remarks>
    /// Mirrors the filter applied to <c>fps_limit_options</c> above, so the value reported can never
    /// be one the slider does not have a notch for. Shared with the frame-limit projection and the
    /// enable-toggle default, which need the same "lowest playable cap" answer.
    /// </remarks>
    internal static int LowestOption(IReadOnlyList<int> options)
    {
        int lowest = 0;
        foreach (int option in options)
        {
            if (option > 0 && (lowest == 0 || option < lowest))
            {
                lowest = option;
            }
        }

        return lowest;
    }
}
