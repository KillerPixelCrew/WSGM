using System.Collections.Generic;
using System.Linq;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One wizard stage as the tester sees it.</summary>
/// <param name="Id">Segment ID.</param>
/// <param name="Title">Stage title.</param>
/// <param name="Description">What the stage does, in one or two sentences.</param>
/// <param name="Available">Whether this build implements the stage.</param>
internal sealed record LabStage(string Id, string Title, string Description, bool Available);

/// <summary>The wizard's stages, in order.</summary>
internal static class LabStages
{
    /// <summary>Checks the machine is ready: other managers, HidHide, recovery and PawnIO.</summary>
    public const string Preflight = "preflight";

    /// <summary>Identifies the device and has the tester confirm it.</summary>
    public const string Identity = "identity";

    /// <summary>Every stage, in wizard order.</summary>
    public static IReadOnlyList<LabStage> All { get; } =
    [
        new(Preflight, "Get ready",
            "Closes programs that would hide or change the controller, lets this tool see hidden devices, and installs PawnIO if it is missing.",
            true),
        new(Identity, "Your device",
            "Reads the board, BIOS and firmware versions and compares them with the devices this tool knows.",
            true),
        new("system-dump", "System details",
            "Records the ACPI tables, device tree, HID devices and sensors.", false),
        new("buttons", "Buttons", "Press each button when it is shown.", false),
        new("motion", "Motion sensors", "Measures the gyro and accelerometer at rest and in six positions.", false),
        new("rumble", "Rumble", "Finds the weakest rumble you can feel and the shortest pulse.", false),
        new("power", "Power and fans", "Tests power limits, fans and lighting, and puts them back.", false),
        new("sleep", "Sleep and wake", "Checks the controller and sensors come back after sleep.", false)
    ];

    /// <summary>Stage IDs in wizard order.</summary>
    public static IEnumerable<string> Ids => All.Select(stage => stage.Id);
}
