using System.Collections.Generic;
using System.Linq;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One wizard stage as the tester sees it.</summary>
/// <param name="Id">Segment ID.</param>
/// <param name="Title">Stage title.</param>
/// <param name="Description">What the stage does, in one or two sentences.</param>
internal sealed record LabStage(string Id, string Title, string Description);

/// <summary>The wizard's stages, in order.</summary>
internal static class LabStages
{
    /// <summary>Checks the machine is ready: other managers, HidHide, recovery and PawnIO.</summary>
    public const string Preflight = "preflight";

    /// <summary>Identifies the device and has the tester confirm it.</summary>
    public const string Identity = "identity";

    /// <summary>Records ACPI, SMBIOS, the device tree, HID, sensors, WMI, display, battery and power.</summary>
    public const string SystemDump = "system-dump";

    /// <summary>Captures every input while the tester presses each control.</summary>
    public const string Buttons = "buttons";

    /// <summary>Measures the gyro and accelerometer on every source.</summary>
    public const string Motion = "motion";

    /// <summary>Finds working rumble routes and calibrates them.</summary>
    public const string Rumble = "rumble";

    /// <summary>Tests power limits, fans, lighting and the charge limit, and puts them back.</summary>
    public const string Power = "power";

    /// <summary>Checks the controller and sensors after sleep.</summary>
    public const string Sleep = "sleep";

    /// <summary>Every stage, in wizard order.</summary>
    public static IReadOnlyList<LabStage> All { get; } =
    [
        new(Preflight, "Get ready",
            "Closes programs that would hide or change the controller, lets this tool see hidden devices, and installs PawnIO if it is missing."),
        new(Identity, "Your device",
            "Reads the board, BIOS and firmware versions and compares them with the devices this tool knows."),
        new(SystemDump, "System details",
            "Records the ACPI tables, device tree, HID devices, sensors, WMI, display, battery and power settings. Nothing is changed."),
        new(Buttons, "Buttons", "Press each control when it is shown. Every input from every device is recorded."),
        new(Motion, "Motion sensors",
            "Measures the gyro and accelerometer at rest, in six positions and while turning."),
        new(Rumble, "Rumble",
            "Finds which way of driving the motors works, then the weakest rumble you can feel and the shortest pulse."),
        new(Power, "Power, fans and lighting",
            "Tests power limits, fans, lighting and the charge limit where this device supports it, and puts everything back."),
        new(Sleep, "Sleep and wake", "Puts the device to sleep and checks the controller and sensors come back.")
    ];

    /// <summary>Stage IDs in wizard order.</summary>
    public static IEnumerable<string> Ids => All.Select(stage => stage.Id);
}
