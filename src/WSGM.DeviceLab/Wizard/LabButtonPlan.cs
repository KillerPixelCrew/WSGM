using System.Collections.Generic;
using System.Linq;
using WSGM.DeviceLab.Knowledge;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One control the tester is asked to use.</summary>
/// <param name="Id">Segment-safe ID, for example <c>a</c> or <c>oem-left</c>.</param>
/// <param name="Name">Name shown to the tester.</param>
/// <param name="Instruction">What to do with it.</param>
/// <param name="Optional">Whether many devices lack it, so skipping is expected.</param>
/// <param name="Known">How the knowledge record believes it arrives, when it has a belief.</param>
/// <param name="Detailed">Whether every analog or pointer change is recorded, for sticks, triggers and touchpads.</param>
/// <param name="QuietMs">
///     How long the step waits for quiet after the first input before it finishes by itself (AllyXLab's
///     windows: 600 ms for a press, 900 ms for a stick, 1200 ms for a movement). Zero means only the
///     tester's Next ends it, for holds and chords whose first report is not the end.
/// </param>
internal sealed record LabControl(
    string Id,
    string Name,
    string Instruction,
    bool Optional,
    string? Known = null,
    bool Detailed = false,
    int QuietMs = 600);

/// <summary>The controls the buttons stage asks for, in order.</summary>
internal static class LabButtonPlan
{
    /// <summary>Builds the list: the standard gamepad, then the device's own buttons, then common extras.</summary>
    /// <param name="record">The confirmed knowledge record, if any.</param>
    /// <returns>The controls.</returns>
    public static IReadOnlyList<LabControl> For(DeviceKnowledgeRecord? record)
    {
        List<LabControl> controls =
        [
            new("channel-check", "Any button",
                "Press any button on the controller once. This shows which channels report before the real steps start.",
                false),
            Press("a", "A"), Press("b", "B"), Press("x", "X"), Press("y", "Y"),
            Press("dpad-up", "D-pad up"), Press("dpad-down", "D-pad down"),
            Press("dpad-left", "D-pad left"), Press("dpad-right", "D-pad right"),
            Press("lb", "Left bumper (LB)"), Press("rb", "Right bumper (RB)"),
            new("lt", "Left trigger (LT)", "Press the left trigger slowly all the way in, then let it go.", false,
                Detailed: true, QuietMs: 900),
            new("rt", "Right trigger (RT)", "Press the right trigger slowly all the way in, then let it go.", false,
                Detailed: true, QuietMs: 900),
            new("left-stick", "Left stick", "Move the left stick slowly all the way round twice, then let it go.",
                false,
                Detailed: true, QuietMs: 900),
            new("right-stick", "Right stick", "Move the right stick slowly all the way round twice, then let it go.",
                false, Detailed: true, QuietMs: 900),
            Press("ls", "Left stick click", "Press the left stick down like a button, then let it go."),
            Press("rs", "Right stick click", "Press the right stick down like a button, then let it go."),
            Press("view", "View button (two squares)"), Press("menu", "Menu button (three lines)"),
            new("guide", "Xbox or Home button", "Press and release the Xbox or Home button, if there is one.", true)
        ];

        // The plan's device controls, each skippable. A knowledge record names the ones it knows and
        // says how it believes they arrive; buttons it has that are not in this list are added after.
        List<LabControl> extras =
        [
            Extra("oem-left", "Extra button left of the screen",
                "If there is an extra button on the left side of the screen (not A, B, X, Y or the sticks), press and release it."),
            Extra("oem-right", "Extra button right of the screen",
                "If there is an extra button on the right side of the screen, press and release it."),
            Extra("back-left1", "Back button, left (upper or only)",
                "If there is a button on the back, left side, press and release it. If there are two, use the upper one."),
            Extra("back-left2", "Back button, left (lower)",
                "If there is a second button on the back, left side, press and release it."),
            Extra("back-right1", "Back button, right (upper or only)",
                "If there is a button on the back, right side, press and release it. If there are two, use the upper one."),
            Extra("back-right2", "Back button, right (lower)",
                "If there is a second button on the back, right side, press and release it."),
            Extra("touchpad-left-click", "Left touchpad click",
                "If there is a touchpad on the left, press it down until it clicks, then let go."),
            Extra("touchpad-left-surface", "Left touchpad surface",
                "Slide your finger across the left touchpad, then lift it.", true),
            Extra("touchpad-right-click", "Right touchpad click",
                "If there is a touchpad (or the only touchpad) on the right, press it down until it clicks, then let go."),
            Extra("touchpad-right-surface", "Right touchpad surface",
                "Slide your finger across the right touchpad, then lift it.", true),
            Extra("stick-touch-left", "Left stick touch",
                "Rest your thumb on the left stick without moving it, then lift it.", true),
            Extra("stick-touch-right", "Right stick touch",
                "Rest your thumb on the right stick without moving it, then lift it.", true),
            Extra("volume-up", "Volume up", "Press and release volume up."),
            Extra("volume-down", "Volume down", "Press and release volume down."),
            Extra("power", "Power button",
                "Tap the power button very briefly. If the screen turns off, press it again to wake the device, then come back here.")
        ];
        List<LabControl> unlisted = [];
        foreach (var button in record?.Buttons ?? [])
        {
            var id = Slug(button.WizardButton ?? button.Name);
            if (id.Length == 0 || controls.Any(control => control.Id == id))
            {
                continue;
            }

            var index = extras.FindIndex(control => control.Id == id);
            if (index >= 0)
            {
                extras[index] = extras[index] with
                {
                    Name = $"{button.Name} ({extras[index].Name.ToLowerInvariant()})",
                    Instruction = $"Press and release the {button.Name} button.",
                    Known = Belief(button)
                };
            }
            else if (unlisted.All(control => control.Id != id))
            {
                unlisted.Add(new LabControl(id, button.Name, $"Press and release the {button.Name} button.", true,
                    Belief(button)));
            }
        }

        controls.AddRange(extras);
        controls.AddRange(unlisted);

        // Firmware often treats a long press of an OEM button as a separate action, so those get a hold
        // of their own; an ordinary button just keeps reporting the same bit. Then the rear buttons
        // combined with A, which is how chord handling shows up.
        var guide = controls.First(control => control.Id == "guide");
        IEnumerable<LabControl> holdable =
        [
            guide,
            .. controls.Where(control => control.Id is "oem-left" or "oem-right" or "back-left1" or "back-left2"
                or "back-right1" or "back-right2"),
            .. unlisted
        ];
        controls.AddRange(holdable.ToList().Select(control => control with
        {
            Id = control.Id + "-hold",
            Name = control.Name + ", held",
            Instruction = $"Hold {DisplayName(control)} for about two seconds, then let go.",
            QuietMs = 0
        }));
        foreach (var rear in new[] { "back-left1", "back-right1" })
        {
            var control = controls.First(item => item.Id == rear);
            controls.Add(new LabControl($"{rear}-with-a", $"{control.Name} with A",
                $"Hold {DisplayName(control)}, press A, then release both.", true, control.Known, QuietMs: 0));
        }

        return controls;
    }

    /// <summary>Turns a name into a segment-safe ID.</summary>
    /// <param name="name">Name.</param>
    /// <returns>Lower-case letters, digits and hyphens.</returns>
    public static string Slug(string name)
    {
        List<char> result = [];
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && result.Count > 0 && result[^1] != '-' && char.IsLower(name[i - 1]))
            {
                result.Add('-');
            }

            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                result.Add(c);
            }
            else if (c is >= 'A' and <= 'Z')
            {
                result.Add(char.ToLowerInvariant(c));
            }
            else if (result.Count > 0 && result[^1] != '-')
            {
                result.Add('-');
            }
        }

        return new string([.. result]).Trim('-');
    }

    private static string Belief(DeviceButtonKnowledge button)
    {
        return button.Source switch
        {
            DeviceButtonSourceKind.KeyboardChord => $"keys {string.Join("+", button.PressKeys)}",
            DeviceButtonSourceKind.HidReport =>
                $"HID report {button.ReportId:X2} byte {button.ByteOffset} {(button.MatchesValue ? "=" : "&")} {button.Mask:X2}",
            DeviceButtonSourceKind.WmiEvent => $"WMI event {button.EventCode}",
            _ => button.Source.ToString()
        };
    }

    private static LabControl Extra(string id, string name, string instruction, bool detailed = false)
    {
        return new LabControl(id, name, instruction, true, Detailed: detailed, QuietMs: detailed ? 1200 : 600);
    }

    private static string DisplayName(LabControl control)
    {
        return "the " + control.Name;
    }

    private static LabControl Press(string id, string name, string? instruction = null)
    {
        return new LabControl(id, name, instruction ?? $"Press {name} and hold it for a moment, then let go.", false);
    }
}
