using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>The preset curves, and how one is fitted to the breakpoints a device actually has.</summary>
internal static class FanCurvePresets
{
    /// <summary>The temperature step between preset samples, in °C.</summary>
    private const int SampleStepCelsius = 10;

    /// <summary>The inputs of WSGM's software fan curve: 0, 10, … 100 °C.</summary>
    internal static IReadOnlyList<int> SoftwareCurveInputs { get; } =
        Array.AsReadOnly(Enumerable.Range(0, 11).Select(index => index * SampleStepCelsius).ToArray());

    /// <summary>What the user reads on the preset's button.</summary>
    /// <param name="preset">The preset.</param>
    /// <returns>Its label.</returns>
    internal static string Label(FanCurvePreset preset)
    {
        return preset switch
        {
            FanCurvePreset.Quiet => "Quiet",
            FanCurvePreset.Default => "Default",
            FanCurvePreset.Aggressive => "Aggressive",
            _ => preset.ToString()
        };
    }

    /// <summary>A table's Default preset on the given inputs, clamped to the control's duty range.</summary>
    /// <param name="table">The device's table.</param>
    /// <param name="inputs">The curve's temperatures.</param>
    /// <param name="minimum">The lowest duty the control accepts.</param>
    /// <param name="maximum">The highest duty the control accepts.</param>
    /// <returns>One point per input.</returns>
    internal static CurvePoint[] DefaultCurve(FanCurvePresetTable table, IReadOnlyList<int> inputs, int minimum,
        int maximum)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return
        [
            .. inputs.Select(input =>
                new CurvePoint(input, Math.Clamp(DutyAt(table, FanCurvePreset.Default, input), minimum, maximum)))
        ];
    }

    /// <summary>A table's preset duty at one temperature, interpolated between its samples.</summary>
    /// <param name="table">The device's table.</param>
    /// <param name="preset">The preset to read.</param>
    /// <param name="celsius">The temperature to evaluate at.</param>
    /// <returns>A duty percentage in 0..100.</returns>
    internal static int DutyAt(FanCurvePresetTable table, FanCurvePreset preset, int celsius)
    {
        ArgumentNullException.ThrowIfNull(table);
        var samples = table.Samples(preset);
        var last = samples.Length - 1;
        var clamped = Math.Clamp(celsius, 0, last * SampleStepCelsius);
        var lower = Math.Min(clamped / SampleStepCelsius, last);
        var upper = Math.Min(lower + 1, last);
        if (lower == upper)
        {
            return samples[lower];
        }

        var lowerCelsius = lower * SampleStepCelsius;
        var span = samples[upper] - samples[lower];
        return samples[lower] + (clamped - lowerCelsius) * span / SampleStepCelsius;
    }

    /// <summary>Fits a table's preset onto the temperatures a device's own curve uses.</summary>
    /// <param name="table">The device's table.</param>
    /// <param name="preset">The preset to fit.</param>
    /// <param name="current">The curve the device published, whose inputs are kept.</param>
    /// <returns>A curve with the same inputs and the preset's duties, or empty when there is none.</returns>
    /// <remarks>
    ///     Inputs are carried over untouched. The temperatures in a fan table are the firmware's own
    ///     breakpoints, and a preset is a statement about how hard to blow at a temperature, not about
    ///     which temperatures the table should contain.
    ///     <para>
    ///         Duties are then forced not to decrease. Interpolating a rising preset onto rising inputs
    ///         already produces a rising result, so this only catches a device whose stored inputs are not
    ///         ascending — which the router refuses anyway — and it costs one pass to be certain the curve
    ///         this hands back is one the firmware will accept.
    ///     </para>
    /// </remarks>
    internal static IReadOnlyList<CurvePoint> SampleOnto(FanCurvePresetTable table, FanCurvePreset preset,
        IReadOnlyList<CurvePoint> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Count == 0)
        {
            return [];
        }

        List<CurvePoint> sampled = [];
        var floor = 0;
        foreach (var point in current)
        {
            var duty = Math.Max(floor, DutyAt(table, preset, point.Input));
            floor = duty;
            sampled.Add(point with { Output = duty });
        }

        return sampled;
    }
}
