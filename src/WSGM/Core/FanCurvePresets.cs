using System;
using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>The fan curve shapes offered as a starting point.</summary>
/// <remarks>
///     Three names every handheld user knows from HandheldCompanion. The shapes behind them come from a
///     <see cref="FanCurvePresetTable" />: HC's own arrays for the devices HC also supports, so a user
///     moving between the two tools gets the same fans, and measured tables for devices only WSGM drives.
/// </remarks>
internal enum FanCurvePreset
{
    /// <summary>Holds a low floor and only ramps hard once the device is genuinely hot.</summary>
    Quiet,

    /// <summary>The balance of noise against temperature most users want.</summary>
    Default,

    /// <summary>Starts higher and reaches full speed sooner.</summary>
    Aggressive
}

/// <summary>Eleven duty percentages per preset, sampled at 0, 10, … 100 °C, for one device or family.</summary>
/// <param name="Name">Where the shapes come from, for diagnostics.</param>
/// <param name="Quiet">The Quiet samples.</param>
/// <param name="Default">The Default samples.</param>
/// <param name="Aggressive">The Aggressive samples.</param>
/// <remarks>
///     Tables are stored at HC's resolution rather than pre-reduced, because the breakpoints a fan table
///     uses are the firmware's and differ per device — the reference Claw stores six. Fitting happens at
///     apply time against the curve the device published, so a preset never invents a temperature.
/// </remarks>
internal sealed record FanCurvePresetTable(string Name, int[] Quiet, int[] Default, int[] Aggressive)
{
    /// <summary>HandheldCompanion's arrays from <c>IDevice.fanPresets</c>, in its Quiet, Default, Aggressive order.</summary>
    internal static readonly FanCurvePresetTable HandheldCompanion = new("HandheldCompanion",
        [20, 20, 20, 20, 20, 25, 30, 40, 70, 70, 100],
        [20, 20, 20, 30, 40, 50, 70, 80, 90, 100, 100],
        [40, 40, 40, 40, 40, 50, 70, 80, 90, 100, 100]);

    /// <summary>Acer Predator Atlas 8, from the 2026-10-29 calibration in LibHandheld's Acer PROVENANCE.</summary>
    /// <remarks>
    ///     Duty there is an absolute share of full speed, about 70 RPM per percent, and 10 is the lowest
    ///     speed that spins reliably. The firmware's own automatic mode holds about 3600 RPM (51%) at any
    ///     idle temperature, while the SoC idles at 52..66 °C with the fans far slower, so these shapes keep
    ///     the fans near inaudible until 60 °C and reach full speed at the processor's 100 °C limit.
    /// </remarks>
    internal static readonly FanCurvePresetTable AcerAtlas8 = new("Acer Predator Atlas 8",
        [10, 10, 10, 10, 10, 10, 15, 25, 45, 75, 100],
        [15, 15, 15, 15, 15, 20, 30, 45, 65, 85, 100],
        [20, 20, 20, 20, 25, 35, 45, 60, 80, 100, 100]);

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

/// <summary>The preset curves, and how one is fitted to the breakpoints a device actually has.</summary>
internal static class FanCurvePresets
{
    /// <summary>The exact LibHandheld definition whose fans use the measured Atlas table.</summary>
    internal const string AcerAtlas8DefinitionId = "acer-predator-atlas-8-pa08-i51";

    /// <summary>The temperature step between preset samples, in °C.</summary>
    private const int SampleStepCelsius = 10;

    /// <summary>The table for one exact device definition; HandheldCompanion's shapes for every other device.</summary>
    /// <param name="deviceDefinitionId">The running definition, or null without one.</param>
    internal static FanCurvePresetTable For(string? deviceDefinitionId)
    {
        return deviceDefinitionId == AcerAtlas8DefinitionId
            ? FanCurvePresetTable.AcerAtlas8
            : FanCurvePresetTable.HandheldCompanion;
    }

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

    /// <summary>The HandheldCompanion preset's duty at one temperature, interpolated between its samples.</summary>
    /// <param name="preset">The preset to read.</param>
    /// <param name="celsius">The temperature to evaluate at.</param>
    /// <returns>A duty percentage in 0..100.</returns>
    internal static int DutyAt(FanCurvePreset preset, int celsius)
    {
        return DutyAt(FanCurvePresetTable.HandheldCompanion, preset, celsius);
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

    /// <summary>Fits a HandheldCompanion preset onto the temperatures a device's own curve uses.</summary>
    /// <param name="preset">The preset to fit.</param>
    /// <param name="current">The curve the device published, whose inputs are kept.</param>
    /// <returns>A curve with the same inputs and the preset's duties, or empty when there is none.</returns>
    internal static IReadOnlyList<CurvePoint> SampleOnto(FanCurvePreset preset, IReadOnlyList<CurvePoint> current)
    {
        return SampleOnto(FanCurvePresetTable.HandheldCompanion, preset, current);
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
