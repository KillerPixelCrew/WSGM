using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WSGM.DeviceLab.Wizard;

/// <summary>What a motion step measures.</summary>
internal enum LabMotionStepKind
{
    /// <summary>Lying still, for bias and noise.</summary>
    Rest,

    /// <summary>Held still in one orientation, for which axis carries gravity.</summary>
    Pose,

    /// <summary>
    ///     Turned back and forth about one axis, for which gyro axis answers. Started by the movement and
    ///     finished when the device is still again, as AllyXLab's movement steps are.
    /// </summary>
    Rotation
}

/// <summary>One motion step the tester performs.</summary>
/// <param name="Id">Step ID, used in segment IDs and evidence names.</param>
/// <param name="Title">Short title.</param>
/// <param name="Instruction">What the tester does, in plain words.</param>
/// <param name="Kind">What the step measures.</param>
/// <param name="CountdownSeconds">Seconds to get into position before recording starts.</param>
/// <param name="Seconds">
///     Recording length in seconds for a still step, as AllyXLab timed them (rest 12 s, poses 5 s). A
///     rotation starts with the movement and ends once the device is still again; this is how long it
///     records when no sensor can tell movement.
/// </param>
/// <param name="Axis">
///     Device axis the step is about, in the device frame of <see cref="LabMotionAnalysis" />: for a
///     pose the axis pointing up, for a rotation the axis turned about.
/// </param>
/// <param name="Sign">
///     For a pose, +1 when the positive axis points up. For a rotation, the sign of the first movement
///     about <paramref name="Axis" /> by the right-hand rule.
/// </param>
internal sealed record LabMotionStep(
    string Id,
    string Title,
    string Instruction,
    LabMotionStepKind Kind,
    int CountdownSeconds,
    int Seconds,
    char Axis,
    int Sign);

/// <summary>The motion steps, in the order the tester does them.</summary>
/// <remarks>
///     Rotation signs follow from the instructions. Held as when playing, the screen faces the tester:
///     tilting the top away moves +Y towards -Z, a negative turn about X; turning like a steering wheel
///     to the left is counterclockwise as the tester sees it, a positive turn about Z; turning the
///     screen towards the tester's left moves +Z towards -X, a negative turn about Y.
/// </remarks>
internal static class LabMotionSteps
{
    /// <summary>Resting flat, screen up.</summary>
    public const string Rest = "rest";

    /// <summary>Every step, in order.</summary>
    public static IReadOnlyList<LabMotionStep> All { get; } =
    [
        new(Rest, "Lying still",
            "Put it flat on a table, screen up. Do not touch it until the time is up.",
            LabMotionStepKind.Rest, 5, 12, 'Z', 1),
        new("screen-up", "Screen up",
            "Keep it flat on the table, screen up.",
            LabMotionStepKind.Pose, 4, 5, 'Z', 1),
        new("screen-down", "Screen down",
            "Turn it over so the screen faces the table. Lay it down gently.",
            LabMotionStepKind.Pose, 6, 5, 'Z', -1),
        new("upright", "Standing up",
            "Stand it on its bottom edge, screen facing you, as when playing. Hold it still.",
            LabMotionStepKind.Pose, 6, 5, 'Y', 1),
        new("upside-down", "Upside down",
            "Turn it upside down: top edge down, screen facing you. Hold it still.",
            LabMotionStepKind.Pose, 6, 5, 'Y', -1),
        new("left-edge-down", "Left edge down",
            "Stand it on its left edge, screen facing you. Hold it still.",
            LabMotionStepKind.Pose, 6, 5, 'X', 1),
        new("right-edge-down", "Right edge down",
            "Stand it on its right edge, screen facing you. Hold it still.",
            LabMotionStepKind.Pose, 6, 5, 'X', -1),
        new("pitch", "Tilt forward and back",
            "Hold it as when playing. When the countdown ends, tilt the top away from you and back, three times, then hold still.",
            LabMotionStepKind.Rotation, 4, 8, 'X', -1),
        new("roll", "Steering wheel",
            "Hold it as when playing. When the countdown ends, turn it like a steering wheel, left first, then right. Do it three times, then hold still.",
            LabMotionStepKind.Rotation, 4, 8, 'Z', 1),
        new("yaw", "Shake your head",
            "Hold it as when playing. When the countdown ends, turn it left and right like shaking your head, starting to your left. Do it three times, then hold still.",
            LabMotionStepKind.Rotation, 4, 8, 'Y', -1)
    ];

    /// <summary>The nested segment ID of a step.</summary>
    /// <param name="step">The step.</param>
    /// <returns><c>motion/&lt;step&gt;</c>.</returns>
    public static string Segment(LabMotionStep step)
    {
        return $"{LabStages.Motion}/{step.Id}";
    }

    /// <summary>Loads the take the tester last kept for a step, from any earlier attempt.</summary>
    /// <param name="project">The project.</param>
    /// <param name="step">The step.</param>
    /// <returns>The kept record, or null when the step was never kept. Blocking.</returns>
    public static LabMotionStepRecord? LoadKept(LabProject project, LabMotionStep step)
    {
        var segment = Segment(step);
        var state = project.Segment(segment);
        if (project.CurrentAttemptDirectory(segment) is not { } latest
            || Path.GetDirectoryName(latest) is not { } parent)
        {
            return null;
        }

        for (var attempt = state.Attempts; attempt > 0; attempt--)
        {
            var path = Path.Combine(parent, $"attempt-{attempt}", $"motion-{step.Id}.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<LabMotionStepRecord>(File.ReadAllText(path),
                    LabProject.JsonOptions);
                if (record is { Outcome: "kept" })
                {
                    return record;
                }
            }
            catch (JsonException)
            {
                // An unreadable take is skipped; an older kept one may still be there.
            }
        }

        return null;
    }
}
