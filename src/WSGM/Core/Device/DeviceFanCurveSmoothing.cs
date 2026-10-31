namespace WSGM.Core;

/// <summary>How the software curve's output is steadied before it reaches a fan.</summary>
/// <param name="HysteresisCelsius">
///     How far the temperature must move from the one the current target was read at before the target
///     is recomputed. Ignored beyond the curve's first and last breakpoints, where the output is pinned.
/// </param>
/// <param name="MaximumRisePerTick">The most the duty may rise in one one-second tick.</param>
/// <param name="MaximumFallPerTick">The most the duty may fall in one tick.</param>
/// <param name="AverageSamples">How many of the latest fresh readings are averaged before the curve is read.</param>
/// <remarks>
///     FanControl's sensor averaging, hysteresis and step limits, which handheld users already tune by
///     hand: one spiky reading is diluted, a sensor that jitters by a degree no longer moves the fan,
///     and a load spike spins up briskly but spins down slowly instead of surging. A device whose
///     temperature behaves differently carries its own values on its LibHandheld definition, which reach
///     the curve through its <see cref="FanCurvePresetTable" />.
/// </remarks>
internal readonly record struct DeviceFanCurveSmoothing(double HysteresisCelsius, int MaximumRisePerTick,
    int MaximumFallPerTick, int AverageSamples = 1)
{
    /// <summary>Every tick follows the curve exactly.</summary>
    internal static readonly DeviceFanCurveSmoothing None = new(0, 100, 100);

    /// <summary>A three-second average, 2 °C, and 10 points up or 3 down per second.</summary>
    internal static readonly DeviceFanCurveSmoothing Default = new(2, 10, 3, 3);
}
