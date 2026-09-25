using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.DeviceLab.Wizard;

/// <summary>How one power, mode, fan or charge test went.</summary>
internal sealed record LabPowerTestResult
{
    /// <summary>Feature, for example <c>tdp</c>.</summary>
    public required string Feature { get; init; }

    /// <summary>Transport, for example <c>atkacpi</c>.</summary>
    public required string Transport { get; init; }

    /// <summary><c>passed</c>, <c>failed</c>, <c>skipped</c> or <c>unsupported</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>When it finished.</summary>
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Why, in a sentence.</summary>
    public string? Detail { get; init; }

    /// <summary>The state before the test.</summary>
    public object? Original { get; init; }

    /// <summary>The value written.</summary>
    public object? TestValue { get; init; }

    /// <summary>What was read back after the write.</summary>
    public object? Readback { get; init; }

    /// <summary>Whether the original was put back and read back; null when nothing was written.</summary>
    public bool? Restored { get; init; }

    /// <summary>What the tester answered.</summary>
    public string? TesterAnswer { get; init; }

    /// <summary>Telemetry taken during the test.</summary>
    public IReadOnlyList<LabPowerSample>? Samples { get; init; }
}

/// <summary>Plain-language names and the stage summary line.</summary>
internal static class LabPowerSummary
{
    /// <summary>The tester-facing name of a feature.</summary>
    /// <param name="feature">Feature ID.</param>
    /// <returns>The name.</returns>
    public static string Name(string feature)
    {
        return feature switch
        {
            "tdp" => "TDP",
            "power-profile" => "performance mode",
            "fan" => "fans",
            "fan-full-speed" => "100% fan speed",
            "charge-limit" => "charge limit",
            _ => feature
        };
    }

    /// <summary>Joins names as <c>a, b and c</c>.</summary>
    /// <param name="items">Names.</param>
    /// <returns>The list.</returns>
    public static string List(IReadOnlyList<string> items)
    {
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1]
        };
    }

    /// <summary>The power half of the stage summary.</summary>
    /// <param name="tests">Test results.</param>
    /// <param name="fallback">What to say when no test ran.</param>
    /// <returns>The line.</returns>
    public static string Power(IReadOnlyList<LabPowerTestResult> tests, string fallback)
    {
        var ran = tests.Where(test => test.Restored is not null).ToArray();
        if (ran.Length == 0)
        {
            return fallback;
        }

        List<string> parts = [];
        var passed = ran.Where(test => IsPass(test.Outcome) && test.Restored == true)
            .Select(test => Name(test.Feature)).Distinct().ToArray();
        var failed = ran.Where(test => !IsPass(test.Outcome)).Select(test => Name(test.Feature)).Distinct().ToArray();
        var unrestored = ran.Where(test => test.Restored == false).Select(test => Name(test.Feature)).Distinct()
            .ToArray();
        if (passed.Length > 0)
        {
            parts.Add($"{Capital(List(passed))} tested and restored");
        }

        if (failed.Length > 0)
        {
            parts.Add($"{List(failed)} test failed");
        }

        if (unrestored.Length > 0)
        {
            parts.Add($"{List(unrestored)} NOT restored");
        }

        return string.Join("; ", parts);
    }

    /// <summary>The lighting half of the stage summary.</summary>
    /// <param name="shown">Colours shown and what the tester saw, as (shown, seen) pairs.</param>
    /// <param name="found">Whether any lighting was found.</param>
    /// <returns>The line.</returns>
    public static string Lighting(IReadOnlyList<(string Shown, string Seen)> shown, bool found)
    {
        if (!found)
        {
            return "lighting: none found";
        }

        if (shown.Count == 0)
        {
            return "lighting: not tested";
        }

        string[] colours = ["red", "green", "blue"];
        var seen = colours.Where(colour => shown.Any(item =>
            item.Shown.Split(':', ' ').Contains(colour)
            && (item.Seen == colour || item.Seen == "matched"))).ToArray();
        return seen.Length == 0 ? "lighting: no colour seen" : $"lighting: {List(seen)} seen";
    }

    /// <summary>Whether a test outcome counts as a pass.</summary>
    /// <param name="outcome">The outcome string.</param>
    /// <returns>True for a pass.</returns>
    public static bool IsPass(string outcome)
    {
        return outcome is "passed" or "applied-readback-matched";
    }

    private static string Capital(string text)
    {
        return text.Length == 0 || char.IsUpper(text[0]) ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
