using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WSGM.DeviceLab.Knowledge;

namespace WSGM.DeviceLab.Wizard;

// Buttons: each control's latest candidates.json against the record's belief for that control.
internal static partial class LabReview
{
    private static readonly HashSet<string> GamepadControls =
    [
        .. LabButtonPlan.For(null).Where(control => !control.Optional).Select(control => control.Id), "guide"
    ];

    /// <summary>The wizard button name for a control ID, when it round-trips through the plan's slug.</summary>
    /// <param name="controlId">Control ID, for example <c>oem-left</c>.</param>
    /// <returns>For example <c>OemLeft</c>, or null.</returns>
    internal static string? WizardButton(string controlId)
    {
        var name = string.Concat(controlId.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        return name.Length > 0 && LabButtonPlan.Slug(name) == controlId ? name : null;
    }

    /// <summary>Describes what a record believes about a button.</summary>
    /// <param name="button">Button.</param>
    /// <returns>One line.</returns>
    internal static string DescribeButton(DeviceButtonKnowledge button)
    {
        return button.Source switch
        {
            DeviceButtonSourceKind.KeyboardChord => $"keys {string.Join("+", button.PressKeys)}",
            DeviceButtonSourceKind.HidReport => string.Create(CultureInfo.InvariantCulture,
                $"HID report {button.ReportId:X2} byte {button.ByteOffset} {(button.MatchesValue ? "=" : "&")} {button.Mask:X2}"),
            DeviceButtonSourceKind.WmiEvent => string.Create(CultureInfo.InvariantCulture,
                $"WMI event {button.EventCode}"),
            DeviceButtonSourceKind.Gamepad => "gamepad",
            _ => "declared, source unknown"
        };
    }

    private static void ReviewButtons(LabReviewArchive archive, DeviceKnowledgeRecord? record,
        List<LabReviewItem> items)
    {
        var prefix = LabStages.Buttons + "/";
        foreach (var segment in archive.Segments.Where(segment =>
                     segment.Id.StartsWith(prefix, StringComparison.Ordinal)))
        {
            items.Add(ReviewButton(archive, segment, segment.Id[prefix.Length..], record));
        }
    }

    private static LabReviewItem ReviewButton(
        LabReviewArchive archive,
        LabReviewSegment segment,
        string controlId,
        DeviceKnowledgeRecord? record)
    {
        var field = $"button:{controlId}";
        DeviceButtonKnowledge[] beliefs =
        [
            .. record?.Buttons.Where(button => LabButtonPlan.Slug(button.WizardButton ?? button.Name) == controlId) ??
               []
        ];
        var specific = beliefs.Where(button => button.Source is not DeviceButtonSourceKind.Declared).ToArray();
        var recordText = beliefs.Length == 0 ? null : string.Join("; ", beliefs.Select(DescribeButton));
        var evidence = archive.ReadEvidence(segment, "candidates.json");
        if (evidence is null)
        {
            return new LabReviewItem
            {
                Field = field,
                Area = "buttons",
                Verdict = LabReviewVerdict.Unresolved,
                Record = recordText,
                Detail = segment.Attempts == 0 ? "Never recorded." : "The attempt holds no candidates.",
                Evidence = segment.Reference
            };
        }

        var control = LabReviewArchive.Get(evidence, "control");
        var name = LabReviewArchive.Text(LabReviewArchive.Get(control, "name")) ?? controlId;
        var answer = LabReviewArchive.Text(LabReviewArchive.Get(evidence, "answer"));
        var flagged = LabReviewArchive.Boolean(LabReviewArchive.Get(evidence, "flagged")) == true;
        var detailed = LabReviewArchive.Boolean(LabReviewArchive.Get(control, "detailed")) == true;
        var observed = ObserveInput(evidence);
        var baseItem = new LabReviewItem
        {
            Field = field,
            Area = "buttons",
            Verdict = LabReviewVerdict.Unresolved,
            Record = recordText,
            Observed = observed.Summary,
            Evidence = segment.Reference,
            Candidates = observed.Lines
        };

        if (answer == "not-on-device")
        {
            return beliefs.Length > 0
                ? baseItem with
                {
                    Verdict = LabReviewVerdict.Disagrees,
                    Observed = "not on this device",
                    Detail = "The tester says the device does not have this control; the record lists it."
                }
                : baseItem with
                {
                    Verdict = LabReviewVerdict.Observed, Observed = "not on this device",
                    Detail = "Skipped by the tester."
                };
        }

        if (answer != "done" || flagged)
        {
            return baseItem with
            {
                Detail = flagged
                    ? "Nothing reacted, and the tester kept the attempt."
                    : $"The attempt ended with '{answer}'."
            };
        }

        if (detailed && beliefs.Length == 0)
        {
            return baseItem with
            {
                Verdict = LabReviewVerdict.Observed, Detail = "Analog or surface control; ranges are listed."
            };
        }

        var agreeing = specific.FirstOrDefault(belief => Agrees(belief, observed));
        if (agreeing is not null)
        {
            return baseItem with
            {
                Verdict = LabReviewVerdict.Confirmed,
                Detail = $"The record's {DescribeButton(agreeing)} was seen.",
                Proposal = new LabButtonProposal(agreeing, agreeing with
                {
                    WizardButton = agreeing.WizardButton ?? WizardButton(controlId)
                })
            };
        }

        var original = specific.FirstOrDefault() ?? beliefs.FirstOrDefault();
        var (derived, why) = Derive(observed, original?.Name ?? name, original?.WizardButton ?? WizardButton(controlId),
            original?.HcFlag);
        if (specific.Length > 0)
        {
            return baseItem with
            {
                Verdict = derived is null ? LabReviewVerdict.Unresolved : LabReviewVerdict.Disagrees,
                Detail = derived is null
                    ? $"The record's {recordText} was not seen, and no single source answered ({why})."
                    : $"The record's {recordText} was not seen; the evidence shows {DescribeButton(derived)}.",
                Proposal = derived is null ? null : new LabButtonProposal(original, derived)
            };
        }

        if (GamepadControls.Contains(controlId) && observed.Gamepad.Count > 0)
        {
            return baseItem with
            {
                Verdict = LabReviewVerdict.Observed, Detail = "Arrives on the standard gamepad, as expected."
            };
        }

        return baseItem with
        {
            Verdict = derived is null ? LabReviewVerdict.Unresolved : LabReviewVerdict.New,
            Detail = derived is null
                ? $"No single source answered ({why})."
                : beliefs.Length > 0
                    ? $"The record declares the button without a source; the evidence shows {DescribeButton(derived)}."
                    : $"Not in the record; the evidence shows {DescribeButton(derived)}.",
            Proposal = derived is null ? null : new LabButtonProposal(original, derived)
        };
    }

    private static bool Agrees(DeviceButtonKnowledge belief, ObservedInput observed)
    {
        switch (belief.Source)
        {
            case DeviceButtonSourceKind.KeyboardChord:
                return belief.PressKeys.Count > 0
                       && belief.PressKeys.All(key => observed.Keys.Contains(key, StringComparer.OrdinalIgnoreCase));
            case DeviceButtonSourceKind.WmiEvent:
                return belief.EventCode is { } code
                       && observed.WmiValues.Any(value => value == code || (value & 0xFF) == code);
            case DeviceButtonSourceKind.HidReport:
                return belief is { ReportId: { } report, ByteOffset: { } offset, Mask: { } mask }
                       && observed.Hid.Any(bit => bit.ReportId == report && bit.Offset == offset
                                                                         && (belief.MatchesValue
                                                                             ? bit.Values.Contains(mask)
                                                                             : (bit.Changed & mask) != 0));
            case DeviceButtonSourceKind.Gamepad:
                return observed.Gamepad.Count > 0;
            default:
                return false;
        }
    }

    // One clear source, in order of specificity: a WMI event code, one single-bit vendor HID change,
    // the keys the firmware typed, the standard gamepad. Two candidates of the same kind are ambiguous.
    private static (DeviceButtonKnowledge? Button, string Why) Derive(
        ObservedInput observed,
        string name,
        string? wizardButton,
        string? hcFlag)
    {
        DeviceButtonKnowledge Button(DeviceButtonSourceKind source)
        {
            return new DeviceButtonKnowledge
            {
                Name = name, WizardButton = wizardButton, HcFlag = hcFlag, Source = source
            };
        }

        if (observed.WmiValues.Count == 1)
        {
            return (Button(DeviceButtonSourceKind.WmiEvent) with { EventCode = (int)observed.WmiValues[0] },
                "WMI event");
        }

        if (observed.WmiValues.Count > 1)
        {
            return (null, $"{observed.WmiValues.Count} WMI event codes");
        }

        HidBit[] single = [.. observed.Hid.Where(bit => BitOperations.PopCount((uint)bit.Changed) == 1)];
        HidBit[] known = [.. single.Where(bit => bit.Role is not null)];
        var pick = known.Length > 0 ? known : single;
        if (pick.Length == 1)
        {
            return (Button(DeviceButtonSourceKind.HidReport) with
            {
                ReportId = pick[0].ReportId, ByteOffset = pick[0].Offset, Mask = pick[0].Changed
            }, "HID report");
        }

        if (pick.Length > 1)
        {
            return (null, $"{pick.Length} HID report bits changed");
        }

        if (observed.Keys.Count > 0)
        {
            return (Button(DeviceButtonSourceKind.KeyboardChord) with { PressKeys = observed.Keys }, "keys");
        }

        if (observed.Gamepad.Count > 0)
        {
            return (Button(DeviceButtonSourceKind.Gamepad), "gamepad");
        }

        return (null, observed.Hid.Count > 0 ? "only multi-bit HID changes" : "nothing attributable");
    }

    private static ObservedInput ObserveInput(JsonNode evidence)
    {
        List<string> keys = [];
        List<string> keyLines = [];
        foreach (var key in LabReviewArchive.Objects(LabReviewArchive.Get(evidence, "keys")))
        {
            var keyName = LabReviewArchive.Text(key["key"]);
            var presses = LabReviewArchive.Integer(key["presses"]) ?? 0;
            if (keyName is null || presses <= 0)
            {
                continue;
            }

            if (!keys.Contains(keyName, StringComparer.OrdinalIgnoreCase))
            {
                keys.Add(keyName);
            }

            if (keyLines.Count < CandidateLines)
            {
                keyLines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"key {keyName} x{presses} ({LabReviewArchive.Text(key["source"])})"));
            }
        }

        List<long> wmi = [];
        List<HidBit> hid = [];
        List<string> gamepad = [];
        List<string> lines = [];
        foreach (var candidate in LabReviewArchive.Objects(LabReviewArchive.Get(evidence, "candidates")))
        {
            var source = LabReviewArchive.Text(candidate["source"]) ?? "?";
            var description = LabReviewArchive.Text(candidate["deviceDescription"]);
            var role = LabReviewArchive.Text(candidate["knownRole"]);
            var evidenceLines = LabReviewArchive.Strings(candidate["evidence"]).ToArray();
            if (lines.Count < CandidateLines)
            {
                lines.Add(
                    $"{source} {description} {evidenceLines.FirstOrDefault()}{(role is null ? string.Empty : $" [{role}]")}"
                        .Replace("  ", " ", StringComparison.Ordinal).Trim());
            }

            var pointer = description is not null
                          && (description.StartsWith("mouse", StringComparison.Ordinal)
                              || description.Contains(" 000D:", StringComparison.Ordinal));
            foreach (var line in evidenceLines)
            {
                switch (source)
                {
                    case "wmi":
                        foreach (Match match in WmiProperty().Matches(line))
                        {
                            if (long.TryParse(match.Groups["value"].Value, NumberStyles.Integer,
                                    CultureInfo.InvariantCulture,
                                    out var value)
                                && value is >= int.MinValue and <= int.MaxValue
                                && !wmi.Contains(value))
                            {
                                wmi.Add(value);
                            }
                        }

                        break;
                    case "raw-input" when !pointer && HidChange().Match(line) is { Success: true } change:
                        int[] values =
                        [
                            .. change.Groups["values"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                .Select(LabInputAnalysis.HexByte)
                        ];
                        var changed = values.Aggregate(0, (mask, value) => mask | (value ^ values[0]));
                        if (changed != 0)
                        {
                            hid.Add(new HidBit(LabInputAnalysis.HexByte(change.Groups["id"].Value),
                                int.Parse(change.Groups["offset"].Value, CultureInfo.InvariantCulture), changed, values,
                                description, role));
                        }

                        break;
                    case "xinput" when line != "axes":
                    case "wgi" when line.StartsWith("buttons ", StringComparison.Ordinal):
                        if (!gamepad.Contains(line, StringComparer.Ordinal))
                        {
                            gamepad.Add(line);
                        }

                        break;
                }
            }
        }

        foreach (var analog in LabReviewArchive.Objects(LabReviewArchive.Get(evidence, "analog")).Take(2))
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"analog {LabReviewArchive.Integer(analog["samples"])} samples, LT max {LabReviewArchive.Integer(analog["leftTriggerMax"])}, RT max {LabReviewArchive.Integer(analog["rightTriggerMax"])}, left radius {LabReviewArchive.Number(LabReviewArchive.Get(analog["left"], "maxRadius"))} round {LabReviewArchive.Number(LabReviewArchive.Get(analog["left"], "circularity"))}, right radius {LabReviewArchive.Number(LabReviewArchive.Get(analog["right"], "maxRadius"))} round {LabReviewArchive.Number(LabReviewArchive.Get(analog["right"], "circularity"))}"));
        }

        List<string> summary = [];
        if (keys.Count > 0)
        {
            summary.Add($"keys {string.Join("+", keys)}");
        }

        summary.AddRange(wmi.Select(value => string.Create(CultureInfo.InvariantCulture, $"WMI event {value}")));
        summary.AddRange(hid.Take(3).Select(bit => string.Create(CultureInfo.InvariantCulture,
            $"HID report {bit.ReportId:X2} byte {bit.Offset} & {bit.Changed:X2}")));
        if (gamepad.Count > 0)
        {
            summary.Add($"gamepad {string.Join(", ", gamepad.Take(3))}");
        }

        return new ObservedInput(keys, wmi, hid, gamepad, [.. lines, .. keyLines],
            summary.Count == 0 ? "nothing attributable" : string.Join("; ", summary));
    }

    [GeneratedRegex(@"(?<name>\w*(?:[Ee]vt|[Ee]vent|[Cc]ode)\w*)=(?<value>-?\d+)(?=;|$)")]
    private static partial Regex WmiProperty();

    [GeneratedRegex(
        @"^report (?<id>[0-9A-Fa-f]{2}) byte (?<offset>\d{1,4}): (?<values>[0-9A-Fa-f]{2}(?: [0-9A-Fa-f]{2}){0,15})$")]
    private static partial Regex HidChange();

    /// <summary>One vendor HID byte that changed during a step.</summary>
    private sealed record HidBit(
        int ReportId,
        int Offset,
        int Changed,
        IReadOnlyList<int> Values,
        string? Device,
        string? Role);

    /// <summary>What a button step recorded, reduced to comparable facts.</summary>
    private sealed record ObservedInput(
        IReadOnlyList<string> Keys,
        IReadOnlyList<long> WmiValues,
        IReadOnlyList<HidBit> Hid,
        IReadOnlyList<string> Gamepad,
        IReadOnlyList<string> Lines,
        string Summary);
}
