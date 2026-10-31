namespace WSGM.Core;

/// <summary>Eleven duty percentages per preset, sampled at 0, 10, … 100 °C, and the smoothing, for one device.</summary>
/// <param name="Name">Where the shapes come from, for diagnostics.</param>
/// <param name="Quiet">The Quiet samples.</param>
/// <param name="Default">The Default samples.</param>
/// <param name="Aggressive">The Aggressive samples.</param>
/// <remarks>
///     <see cref="HandheldCompanion" /> serves every device whose definition carries no measured duties;
///     <see cref="WSGM.Shell.HandheldUiProjection.FanPresets" /> maps a definition's own into a table.
///     <para>
///         Tables are stored at HC's resolution rather than pre-reduced, because the breakpoints a fan table
///         uses are the firmware's and differ per device — the reference Claw stores six. Fitting happens at
///         apply time against the curve the device published, so a preset never invents a temperature.
///     </para>
/// </remarks>
internal sealed record FanCurvePresetTable(string Name, int[] Quiet, int[] Default, int[] Aggressive)
{
    /// <summary>How the software curve is steadied on this device.</summary>
    internal DeviceFanCurveSmoothing Smoothing { get; init; } = DeviceFanCurveSmoothing.Default;

    /// <summary>HandheldCompanion's arrays from <c>IDevice.fanPresets</c>, in its Quiet, Default, Aggressive order.</summary>
    internal static readonly FanCurvePresetTable HandheldCompanion = new("HandheldCompanion",
        [20, 20, 20, 20, 20, 25, 30, 40, 70, 70, 100],
        [20, 20, 20, 30, 40, 50, 70, 80, 90, 100, 100],
        [40, 40, 40, 40, 40, 50, 70, 80, 90, 100, 100]);

    /// <summary>The samples of one preset.</summary>
    internal int[] Samples(FanCurvePreset preset)
    {
        return preset switch
        {
            FanCurvePreset.Quiet => Quiet,
            FanCurvePreset.Aggressive => Aggressive,
            _ => Default
        };
    }
}
