using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One button the tester pressed.</summary>
/// <param name="At">When.</param>
/// <param name="Answer">
///     <c>felt</c>, <c>not-felt</c>, <c>replay</c>, <c>left</c>, <c>right</c>, <c>both</c> or <c>lowest</c>.
/// </param>
/// <param name="Value">The strength or pulse length the answer is about, where there is one.</param>
internal sealed record LabRumbleAnswer(DateTimeOffset At, string Answer, int? Value = null);

/// <summary>The one-buzz check of a route.</summary>
internal sealed class LabRumbleProbe
{
    /// <summary>Route ID.</summary>
    public required string Route { get; init; }

    /// <summary>Whether the tester felt it; null when it could not be played.</summary>
    public bool? Felt { get; set; }

    /// <summary>Why the buzz could not be played.</summary>
    public string? Error { get; set; }

    /// <summary>Every answer, replays included.</summary>
    public List<LabRumbleAnswer> Answers { get; } = [];
}

/// <summary>One press of a button on the pulse page.</summary>
internal sealed class LabRumblePulseTrial
{
    /// <summary><c>full</c> for 100 percent, <c>lowest</c> for the side's marked minimum.</summary>
    public required string Strength { get; init; }

    /// <summary>Strength in percent.</summary>
    public required int Percent { get; init; }

    /// <summary>Requested length in milliseconds.</summary>
    public required int Milliseconds { get; init; }

    /// <summary>Whether the tester felt it; null when it could not be played.</summary>
    public bool? Felt { get; set; }

    /// <summary>Why it could not be played.</summary>
    public string? Error { get; set; }

    /// <summary>How long the motor was on for each play, in milliseconds.</summary>
    public List<double> HeldMilliseconds { get; } = [];

    /// <summary>Every answer, replays included.</summary>
    public List<LabRumbleAnswer> Answers { get; } = [];
}

/// <summary>What was measured for one physical side of the device.</summary>
internal sealed class LabRumbleSideCalibration
{
    /// <summary><c>left</c> or <c>right</c>: the side the tester feels.</summary>
    public required string Side { get; init; }

    /// <summary>The motor channel (<c>left</c> or <c>right</c>) that drives this side.</summary>
    public required string Channel { get; init; }

    /// <summary>
    ///     Every "Lowest I can feel" press on the slider page, in order; the value is the slider's
    ///     percent.
    /// </summary>
    public List<LabRumbleAnswer> Marks { get; } = [];

    /// <summary>The last marked strength, in percent: the side's <c>MinimumStartIntensity</c>.</summary>
    public int? MinimumStartIntensityPercent { get; set; }

    /// <summary>Every pulse-page button press for this side.</summary>
    public List<LabRumblePulseTrial> Pulses { get; } = [];

    /// <summary>The shortest pulse felt at full strength, in milliseconds: the side's <c>MinimumPulse</c>.</summary>
    public int? MinimumPulseMilliseconds => Shortest("full");

    /// <summary>The shortest pulse felt at the marked minimum, in milliseconds.</summary>
    public int? MinimumPulseAtLowestMilliseconds => Shortest("lowest");

    private int? Shortest(string strength)
    {
        var felt = Pulses.Where(pulse => pulse.Strength == strength && pulse.Felt == true)
            .Select(pulse => pulse.Milliseconds)
            .ToArray();
        return felt.Length == 0 ? null : felt.Min();
    }
}

/// <summary>The calibration of one route.</summary>
internal sealed class LabRumbleRouteCalibration
{
    /// <summary>Route ID.</summary>
    public required string Route { get; init; }

    /// <summary>Route name, for the summary.</summary>
    public required string Name { get; init; }

    /// <summary>Where the tester felt the left channel alone: <c>left</c>, <c>right</c>, <c>both</c> or <c>not-felt</c>.</summary>
    public string? LeftChannelFelt { get; set; }

    /// <summary>Where the tester felt the right channel alone.</summary>
    public string? RightChannelFelt { get; set; }

    /// <summary>The answers of the side check, replays included.</summary>
    public List<LabRumbleAnswer> SideAnswers { get; } = [];

    /// <summary>Whether the left channel drives the right side and the other way round; null when unclear.</summary>
    public bool? Swapped { get; set; }

    /// <summary>The two physical sides, left first.</summary>
    public List<LabRumbleSideCalibration> Sides { get; } = [];

    /// <summary>Why calibration stopped early, when it did.</summary>
    public string? Stopped { get; set; }
}

/// <summary>One visit to the slider page.</summary>
/// <param name="Route">Route ID.</param>
/// <param name="StartedAt">When streaming started.</param>
/// <param name="StoppedAt">When streaming stopped and the zero was written.</param>
/// <param name="IdleStops">How often an unchanged slider was switched off by itself.</param>
/// <param name="Error">The write failure that ended streaming, if any.</param>
/// <param name="ZeroFailed">Whether the final zero failed.</param>
internal sealed record LabRumbleSliderSession(
    string Route,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    int IdleStops,
    string? Error,
    bool ZeroFailed);

/// <summary>Builds the stage summary.</summary>
internal static class LabRumbleSummary
{
    /// <summary>One line for the stage list, for example "XInput works; floor 18 %, shortest pulse 25 ms".</summary>
    /// <param name="routeCount">Routes found.</param>
    /// <param name="working">Names of the routes the tester felt, in order, one per route.</param>
    /// <param name="calibrations">Calibrated routes.</param>
    public static string Describe(int routeCount, IReadOnlyList<string> working,
        IReadOnlyList<LabRumbleRouteCalibration> calibrations)
    {
        if (routeCount == 0)
        {
            return "No way to drive the motors was found. Nothing was written.";
        }

        if (working.Count == 0)
        {
            return routeCount == 1
                ? "No rumble was felt on the one way tried."
                : $"No rumble was felt on any of the {routeCount} ways tried.";
        }

        List<string> parts = [.. calibrations.Select(Calibrated)];
        var others = working.Except(calibrations.Select(calibration => calibration.Name)).Distinct().ToArray();
        if (others.Length > 0)
        {
            parts.Add($"{string.Join(" and ", others)} also {(others.Length == 1 ? "works" : "work")}");
        }

        return string.Join(". ", parts) + ".";
    }

    private static string Calibrated(LabRumbleRouteCalibration calibration)
    {
        List<string> parts = [];
        if (calibration.Swapped == true)
        {
            parts.Add("left and right motors are swapped");
        }

        if (Measure(calibration, side => side.MinimumStartIntensityPercent, "%") is { } floor)
        {
            parts.Add($"floor {floor}");
        }

        if (Measure(calibration, side => side.MinimumPulseMilliseconds, "ms") is { } pulse)
        {
            parts.Add($"shortest pulse {pulse}");
        }

        return parts.Count == 0
            ? $"{calibration.Name} works; not measured"
            : $"{calibration.Name} works; {string.Join(", ", parts)}";
    }

    private static string? Measure(LabRumbleRouteCalibration calibration,
        Func<LabRumbleSideCalibration, int?> pick, string unit)
    {
        var measured = calibration.Sides.Select(side => (side.Side, Value: pick(side))).ToArray();
        if (measured.All(item => item.Value is null))
        {
            return null;
        }

        if (measured.All(item => item.Value == measured[0].Value))
        {
            return $"{measured[0].Value} {unit}";
        }

        return string.Join(", ",
            measured.Select(item =>
                item.Value is { } value ? $"{item.Side} {value} {unit}" : $"{item.Side} not measured"));
    }
}

/// <summary>The tester reported that the motors kept vibrating.</summary>
/// <param name="At">When.</param>
/// <param name="ZeroFailures">Routes whose immediate zero failed.</param>
internal sealed record LabRumbleStillVibrating(DateTimeOffset At, IReadOnlyList<string> ZeroFailures);
