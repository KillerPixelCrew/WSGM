using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What the device can currently back, as far as the performance panel is concerned.</summary>
/// <param name="FrameLimitOptions">Frame caps to offer, or empty to hide the slider.</param>
/// <param name="VariableRefreshRateSupported">Whether the panel supports VRR.</param>
/// <param name="VariableRefreshRateEnabled">
///     Whether VRR is on now, read from the same capability that reports support so the toggle cannot
///     show a state the device disagrees with.
/// </param>
/// <param name="RefreshRatesSelectable">
///     Whether the user may choose a refresh rate by hand. False under the pairing strategies, where
///     WSGM owns the refresh rate and a manual row would fight it.
/// </param>
/// <param name="RefreshRateMinHz">Lowest selectable refresh rate.</param>
/// <param name="RefreshRateMaxHz">Highest selectable refresh rate.</param>
/// <param name="CurrentRefreshRateHz">
///     The rate in force, which the manual refresh row needs a concrete value for. Null only when that
///     row is not offered.
/// </param>
/// <param name="RefreshForCap">
///     The refresh rate each cap will be presented at, keyed by cap. Empty under
///     <c>FrameLimitOnly</c>, where the cap changes no display state and there is nothing to name.
///     <para>
///         Sent as a map rather than as a rule the injected half re-derives: the pairing policy is one
///         decision and it belongs in one place. The slider reads it to label a cap the way SteamOS does —
///         "60 FPS (60 Hz)" — while the user is still dragging, before anything has been applied.
///     </para>
/// </param>
/// <param name="RefreshRates">
///     Every rate the display actually accepted, ascending. Windows takes a mode or it does not —
///     there is no continuum to slide along — so the unified row's refresh mode is NOTCHED to exactly
///     these, unlike its frame-cap mode, where the limiter really does hold any integer.
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
