using System;
using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>The fan curve shapes offered as a starting point.</summary>
/// <remarks>
/// HandheldCompanion's three, by name and by value. They are the shapes handheld users already
/// know, and reproducing them exactly means a user moving between the two tools gets the same fans
/// rather than something that merely sounds similar.
/// </remarks>
internal enum FanCurvePreset
{
    /// <summary>Holds a low floor and only ramps hard once the device is genuinely hot.</summary>
    Quiet,

    /// <summary>HandheldCompanion's default balance of noise against temperature.</summary>
    Default,

    /// <summary>Starts higher and reaches full speed sooner.</summary>
    Aggressive,
}

/// <summary>
/// The preset curves, and how one is fitted to the breakpoints a device actually has.
/// </summary>
/// <remarks>
/// Each preset is HandheldCompanion's own array: eleven duty percentages at 0, 10, … 100 °C, from
/// <c>IDevice.fanPresets</c> (Quiet, Default, Aggressive in that order). They are stored at HC's
/// resolution rather than pre-reduced, because the breakpoints a fan table uses are the firmware's
/// and differ per device — the reference Claw stores six. Fitting happens at apply time against the
/// curve the device published, so a preset never invents a temperature the table does not have.
/// </remarks>
internal static class FanCurvePresets
{
    /// <summary>The temperature step between HandheldCompanion's preset samples, in °C.</summary>
    private const int SampleStepCelsius = 10;

    private static readonly int[] Quiet = [20, 20, 20, 20, 20, 25, 30, 40, 70, 70, 100];
    private static readonly int[] Default = [20, 20, 20, 30, 40, 50, 70, 80, 90, 100, 100];
    private static readonly int[] Aggressive = [40, 40, 40, 40, 40, 50, 70, 80, 90, 100, 100];

    /// <summary>What the user reads on the preset's button.</summary>
    /// <param name="preset">The preset.</param>
    /// <returns>Its label.</returns>
    internal static string Label(FanCurvePreset preset) => preset switch
    {
        FanCurvePreset.Quiet => "Quiet",
        FanCurvePreset.Default => "Default",
        FanCurvePreset.Aggressive => "Aggressive",
        _ => preset.ToString(),
    };

    /// <summary>The preset's duty at one temperature, interpolated between its samples.</summary>
    /// <param name="preset">The preset to read.</param>
    /// <param name="celsius">The temperature to evaluate at.</param>
    /// <returns>A duty percentage in 0..100.</returns>
    internal static int DutyAt(FanCurvePreset preset, int celsius)
    {
        int[] samples = SamplesFor(preset);
        int last = samples.Length - 1;
        int clamped = Math.Clamp(celsius, 0, last * SampleStepCelsius);
        int lower = Math.Min(clamped / SampleStepCelsius, last);
        int upper = Math.Min(lower + 1, last);
        if (lower == upper)
        {
            return samples[lower];
        }

        int lowerCelsius = lower * SampleStepCelsius;
        int span = samples[upper] - samples[lower];
        return samples[lower] + (((clamped - lowerCelsius) * span) / SampleStepCelsius);
    }

    /// <summary>Fits a preset onto the temperatures a device's own curve uses.</summary>
    /// <param name="preset">The preset to fit.</param>
    /// <param name="current">The curve the device published, whose inputs are kept.</param>
    /// <returns>A curve with the same inputs and the preset's duties, or empty when there is none.</returns>
    /// <remarks>
    /// Inputs are carried over untouched. The temperatures in a fan table are the firmware's own
    /// breakpoints, and a preset is a statement about how hard to blow at a temperature, not about
    /// which temperatures the table should contain.
    /// <para>
    /// Duties are then forced not to decrease. Interpolating a rising preset onto rising inputs
    /// already produces a rising result, so this only catches a device whose stored inputs are not
    /// ascending — which the router refuses anyway — and it costs one pass to be certain the curve
    /// this hands back is one the firmware will accept.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<CurvePoint> SampleOnto(
        FanCurvePreset preset,
        IReadOnlyList<CurvePoint> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Count == 0)
        {
            return [];
        }

        List<CurvePoint> sampled = new(current.Count);
        int floor = 0;
        foreach (CurvePoint point in current)
        {
            int duty = Math.Max(floor, DutyAt(preset, point.Input));
            floor = duty;
            sampled.Add(new CurvePoint(point.Input, duty));
        }

        return sampled;
    }

    private static int[] SamplesFor(FanCurvePreset preset) => preset switch
    {
        FanCurvePreset.Quiet => Quiet,
        FanCurvePreset.Aggressive => Aggressive,
        _ => Default,
    };
}
