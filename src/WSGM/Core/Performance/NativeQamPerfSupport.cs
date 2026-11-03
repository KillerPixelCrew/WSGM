using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What the device can currently back, as far as the performance panel is concerned.</summary>
/// <param name="FrameLimitOptions">Frame caps to offer, or empty to hide the slider.</param>
/// <param name="VariableRefreshRateSupported">Whether the panel supports VRR.</param>
/// <param name="VariableRefreshRateEnabled">
///     Whether VRR is on now, read from the same capability that reports support so the toggle cannot
///     show a state the device disagrees with.
/// </param>
/// <param name="RefreshRates">
///     The rates Windows lists for the current mode, ascending. Windows takes a mode or it does not, so
///     the refresh slider is notched to exactly these.
/// </param>
/// <param name="CurrentRefreshRateHz">The rate in force, or null when the display cannot be read.</param>
internal readonly record struct NativeQamPerfSupport(
    IReadOnlyList<int> FrameLimitOptions,
    bool VariableRefreshRateSupported,
    bool VariableRefreshRateEnabled = false,
    IReadOnlyList<int>? RefreshRates = null,
    int? CurrentRefreshRateHz = null);
