namespace WSGM.AllyXLab;

/// <summary>How a step decides it is finished.</summary>
internal enum InputStepKind
{
    /// <summary>A quiet window that learns which reports move on their own.</summary>
    Baseline,

    /// <summary>Finished by the press itself: the press is the "ready", the release ends the step.</summary>
    Press,

    /// <summary>Held still for a fixed time, with a countdown instead of a prompt.</summary>
    Hold,

    /// <summary>Started by the movement and finished when the device is still again.</summary>
    Movement,
}

/// <summary>One guided step.</summary>
/// <param name="Name">Short label used in the report.</param>
/// <param name="Instruction">What the tester does.</param>
/// <param name="Kind">How the step completes.</param>
/// <param name="Seconds">The hold length, for <see cref="InputStepKind.Hold"/>.</param>
/// <param name="Motion">Whether the sensors run during this step.</param>
internal sealed record InputStep(string Name, string Instruction, InputStepKind Kind, int Seconds = 0, bool Motion = false);

internal static class InputSteps
{
    /// <summary>The guided sequence. One press per control, no confirmation prompts.</summary>
    /// <returns>The steps in order.</returns>
    /// <remarks>
    /// Every control is asked for once, because a second and third press of a control that already
    /// reported adds nothing, and a control that reports nothing is answered by "Nothing happened"
    /// rather than by repetition.
    /// </remarks>
    internal static IReadOnlyList<InputStep> All()
    {
        List<InputStep> steps =
        [
            new("Baseline", "Put the device down and take your hands off it. This learns what it sends on its own.", InputStepKind.Baseline, 4, true),
        ];
        foreach ((string name, string what) in new[]
        {
            ("A", "the A button"),
            ("B", "the B button"),
            ("X", "the X button"),
            ("Y", "the Y button"),
            ("D-pad up", "D-pad up"),
            ("D-pad down", "D-pad down"),
            ("D-pad left", "D-pad left"),
            ("D-pad right", "D-pad right"),
            ("Left bumper", "the left bumper"),
            ("Right bumper", "the right bumper"),
            ("Left stick click", "the left stick straight down until it clicks"),
            ("Right stick click", "the right stick straight down until it clicks"),
            ("View", "the View button"),
            ("Menu", "the Menu button"),
            ("Xbox", "the Xbox button"),
            ("Command Center", "the Command Center button"),
            ("Armoury Crate", "the Armoury Crate button"),
            ("Rear M1", "the rear M1 paddle"),
            ("Rear M2", "the rear M2 paddle"),
            ("Volume up", "volume up"),
            ("Volume down", "volume down"),
        })
        {
            steps.Add(new(name, $"Press and release {what}.", InputStepKind.Press));
        }

        steps.Add(new("Left trigger", "Squeeze the left trigger all the way, then let it go.", InputStepKind.Press));
        steps.Add(new("Right trigger", "Squeeze the right trigger all the way, then let it go.", InputStepKind.Press));
        steps.Add(new("Left stick", "Roll the left stick once around its edge, then let it centre.", InputStepKind.Press));
        steps.Add(new("Right stick", "Roll the right stick once around its edge, then let it centre.", InputStepKind.Press));
        steps.Add(new("Rear M1 with A", "Hold M1, press A, then release both.", InputStepKind.Press));
        steps.Add(new("Rear M2 with A", "Hold M2, press A, then release both.", InputStepKind.Press));
        steps.Add(new("Gyro rest", "Put the device flat on a table, screen up, and leave it alone.", InputStepKind.Hold, 12, true));
        foreach ((string pose, string how) in new[]
        {
            ("Screen up", "flat on the table, screen facing up"),
            ("Screen down", "flat on the table, screen facing down"),
            ("Left edge down", "upright on its left edge"),
            ("Right edge down", "upright on its right edge"),
            ("Top edge down", "upright with the top edge down"),
            ("Bottom edge down", "upright with the bottom edge down"),
        })
        {
            steps.Add(new("Gravity: " + pose, $"Rest the device {how} and hold it still.", InputStepKind.Hold, 5, true));
        }

        foreach ((string name, string how) in new[]
        {
            ("Yaw", "Lay it flat, screen up, then turn it a quarter turn clockwise and stop."),
            ("Pitch", "Hold it facing you, then tilt the top edge away from you and stop."),
            ("Roll", "Hold it facing you, then lower the right edge and stop."),
        })
        {
            steps.Add(new(name, how, InputStepKind.Movement, 0, true));
        }

        return steps;
    }

    /// <summary>How long a step waits for quiet before it counts as finished.</summary>
    /// <param name="step">The step.</param>
    /// <returns>The quiet window in milliseconds.</returns>
    internal static int QuietMilliseconds(InputStep step) => step.Kind switch
    {
        InputStepKind.Movement => 1200,
        InputStepKind.Press when step.Name.Contains("stick", StringComparison.OrdinalIgnoreCase) => 900,
        _ => 600,
    };
}
