using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using WSGM.DeviceLab.Knowledge;

namespace WSGM.DeviceLab.Wizard;

// Power, fans, charge limit and lighting against the record's mechanisms and capability flags, and
// what came back after sleep.
internal static partial class LabReview
{
    private static readonly string[] PowerFeatures = ["tdp", "power-profile", "fan", "charge-limit", "lighting"];

    // HC capability flags a passed test demonstrates.
    private static readonly (string Feature, string Capability)[] FeatureCapabilities =
    [
        ("fan", "FanControl"), ("charge-limit", "BatteryChargeLimit"), ("lighting", "DynamicLighting")
    ];

    private static void ReviewPower(LabReviewArchive archive, DeviceKnowledgeRecord? record, List<LabReviewItem> items)
    {
        var segment = archive.Segment(LabStages.Power);
        var reference = segment?.Reference ?? $"segments/{LabStages.Power}";
        var mechanisms = record?.Mechanisms.Where(mechanism => PowerFeatures.Contains(mechanism.Feature)).ToArray() ??
                         [];
        if (segment?.Prefix is null)
        {
            foreach (var mechanism in mechanisms)
            {
                items.Add(new LabReviewItem
                {
                    Field = $"mechanism:{mechanism.Feature}:{mechanism.Transport}",
                    Area = "power",
                    Verdict = LabReviewVerdict.Unresolved,
                    Record = DescribeMechanism(mechanism),
                    Detail = "The power stage never ran.",
                    Evidence = reference
                });
            }

            return;
        }

        // Every test result per feature and transport; a feature passes when every run of it passed and
        // was restored, and fails when any run failed.
        Dictionary<(string Feature, string Transport), List<JsonObject>> results = [];
        foreach (var test in LabReviewArchive.Objects(
                     LabReviewArchive.Get(archive.ReadEvidence(segment, "power-tests.json"), "tests")))
        {
            if (LabReviewArchive.Text(test["feature"]) is { } feature &&
                LabReviewArchive.Text(test["transport"]) is { } transport)
            {
                var key = (feature, transport);
                (results.TryGetValue(key, out var list) ? list : results[key] = []).Add(test);
            }
        }

        // Lighting answers: Aura channels are named by colour, Dynamic Lighting lamps "lamparray:name:colour".
        var lighting = archive.ReadEvidence(segment, "lighting.json");
        List<(string Transport, string Shown, string Seen)> shown = [];
        foreach (var answer in LabReviewArchive.Objects(LabReviewArchive.Get(lighting, "shown")))
        {
            if (LabReviewArchive.Text(answer["shown"]) is { } colour &&
                LabReviewArchive.Text(answer["seen"]) is { } seen)
            {
                shown.Add(colour.StartsWith("lamparray:", StringComparison.Ordinal)
                    ? ("lamparray", colour[(colour.LastIndexOf(':') + 1)..], seen)
                    : ("hid-output", colour, seen));
            }
        }

        List<(string Feature, string Transport, string Outcome, string Detail, bool HasReadback)> outcomes = [];
        foreach (var ((feature, transport), runs) in results
                     .OrderBy(pair => Array.IndexOf(PowerFeatures, pair.Key.Feature))
                     .ThenBy(pair => pair.Key.Transport, StringComparer.Ordinal))
        {
            var ran = runs.Where(run => LabReviewArchive.Get(run, "restored") is not null).ToArray();
            var passed = ran.Length > 0
                         && ran.All(run => LabReviewArchive.Text(run["outcome"]) == "passed"
                                           && LabReviewArchive.Boolean(run["restored"]) == true);
            var failed = runs.Any(run => LabReviewArchive.Text(run["outcome"]) == "failed")
                         || ran.Any(run => LabReviewArchive.Boolean(run["restored"]) == false);
            var outcome = passed ? "passed" :
                failed ? "failed" : LabReviewArchive.Text(runs[^1]["outcome"]) ?? "skipped";
            var detail = string.Join(" | ", runs.Select(run =>
                $"{LabReviewArchive.Text(run["outcome"])}: {LabReviewArchive.Text(run["detail"])}"
                + (LabReviewArchive.Get(run, "readback") is { } readback
                    ? $" (read back {Compact(readback)})"
                    : string.Empty)
                + (LabReviewArchive.Text(run["testerAnswer"]) is { } tester ? $" (tester: {tester})" : string.Empty)));
            outcomes.Add((feature, transport, outcome, detail,
                runs.Any(run => LabReviewArchive.Get(run, "readback") is not null)));
        }

        foreach (var transport in shown.Select(item => item.Transport).Distinct(StringComparer.Ordinal))
        {
            var answers = shown.Where(item => item.Transport == transport).ToArray();
            var seenRight = answers.Where(item => item.Shown != "off" && item.Shown == item.Seen)
                .Select(item => item.Shown)
                .Distinct(StringComparer.Ordinal).ToArray();
            outcomes.Add(("lighting", transport, seenRight.Length > 0 ? "passed" : "failed",
                string.Join(", ", answers.Select(item => $"showed {item.Shown}, tester saw {item.Seen}")), false));
        }

        foreach (var (feature, transport, outcome, detail, hasReadback) in outcomes)
        {
            var mechanism = mechanisms.FirstOrDefault(item => item.Feature == feature && item.Transport == transport);
            var field = $"mechanism:{feature}:{transport}";
            var observed = $"{LabPowerSummary.Name(feature)} via {transport}: {outcome}";
            items.Add(outcome switch
            {
                "passed" when mechanism is not null => new LabReviewItem
                {
                    Field = field,
                    Area = "power",
                    Verdict = LabReviewVerdict.Confirmed,
                    Record = DescribeMechanism(mechanism),
                    Observed = observed,
                    Detail = detail,
                    Evidence = reference,
                    Proposal = new LabMechanismProposal(mechanism, mechanism)
                },
                "passed" => new LabReviewItem
                {
                    Field = field,
                    Area = "power",
                    Verdict = LabReviewVerdict.New,
                    Observed = observed,
                    Detail = $"The record has no {feature} mechanism over {transport}. {detail}",
                    Evidence = reference,
                    Proposal = new LabMechanismProposal(null, new DeviceMechanismKnowledge
                    {
                        Feature = feature, Transport = transport, HasReadback = hasReadback
                    })
                },
                "failed" when mechanism is not null => new LabReviewItem
                {
                    Field = field,
                    Area = "power",
                    Verdict = LabReviewVerdict.Disagrees,
                    Record = DescribeMechanism(mechanism),
                    Observed = observed,
                    Detail = detail,
                    Evidence = reference
                },
                _ => new LabReviewItem
                {
                    Field = field,
                    Area = "power",
                    Verdict = mechanism is null ? LabReviewVerdict.Observed : LabReviewVerdict.Unresolved,
                    Record = mechanism is null ? null : DescribeMechanism(mechanism),
                    Observed = observed,
                    Detail = detail,
                    Evidence = reference
                }
            });
        }

        foreach (var mechanism in mechanisms.Where(mechanism =>
                     !outcomes.Any(item => item.Feature == mechanism.Feature && item.Transport == mechanism.Transport)))
        {
            items.Add(new LabReviewItem
            {
                Field = $"mechanism:{mechanism.Feature}:{mechanism.Transport}",
                Area = "power",
                Verdict = LabReviewVerdict.Unresolved,
                Record = DescribeMechanism(mechanism),
                Detail = "Not tested in this report.",
                Evidence = reference
            });
        }

        // Capability flags: a passed test of a feature shows the flag; a failure of every test
        // contradicts a flag the record claims.
        foreach (var (feature, capability) in FeatureCapabilities)
        {
            var tested = outcomes.Where(item => item.Feature == feature).ToArray();
            if (tested.Length == 0)
            {
                continue;
            }

            var claimed = record?.Capabilities.Contains(capability, StringComparer.Ordinal) == true;
            var passed = tested.Where(item => item.Outcome == "passed").Select(item => item.Transport).ToArray();
            var allFailed = tested.All(item => item.Outcome == "failed");
            items.Add(new LabReviewItem
            {
                Field = $"capability:{capability}",
                Area = "power",
                Verdict = passed.Length > 0
                    ? claimed ? LabReviewVerdict.Confirmed : LabReviewVerdict.New
                    : claimed && allFailed
                        ? LabReviewVerdict.Disagrees
                        : LabReviewVerdict.Unresolved,
                Record = claimed ? capability : null,
                Observed = string.Join("; ", tested.Select(item => $"{item.Transport}: {item.Outcome}")),
                Detail = passed.Length > 0
                    ? $"{LabPowerSummary.Name(feature)} worked over {string.Join(", ", passed)}."
                    : $"No {LabPowerSummary.Name(feature)} test passed.",
                Evidence = reference,
                Proposal = passed.Length > 0 ? new LabCapabilityProposal(capability) : null
            });
        }
    }

    private static void ReviewSleep(LabReviewArchive archive, DeviceKnowledgeRecord? record, List<LabReviewItem> items)
    {
        var segment = archive.Segment(LabStages.Sleep);
        var sleep = segment is null ? null : archive.ReadEvidence(segment, "sleep.json");
        var init = record?.Mechanisms.FirstOrDefault(mechanism =>
            mechanism.Feature is "controller-mode" or "button-init");
        if (sleep is null)
        {
            items.Add(new LabReviewItem
            {
                Field = "sleep",
                Area = "sleep",
                Verdict = LabReviewVerdict.Unresolved,
                Record = init is null ? null : DescribeMechanism(init),
                Detail = segment?.Prefix is null
                    ? "The sleep stage never ran."
                    : segment.Summary ?? "No sleep evidence.",
                Evidence = segment?.Reference ?? $"segments/{LabStages.Sleep}"
            });
            return;
        }

        var missing = LabReviewArchive.Strings(LabReviewArchive.Get(sleep, "missing")).ToArray();
        var back = LabReviewArchive.Number(LabReviewArchive.Get(sleep, "backAfterMs"));
        var initNode = LabReviewArchive.Get(sleep, "init");
        var resent = LabReviewArchive.Get(initNode, "resent") is not null;
        var mode = LabReviewArchive.Integer(LabReviewArchive.Get(initNode, "modeAfterWake"));
        List<string> lines = [];
        if (LabReviewArchive.Text(LabReviewArchive.Get(sleep, "outcome")) is { } outcome)
        {
            lines.Add(outcome);
        }

        lines.AddRange(missing.Take(CandidateLines).Select(item => $"missing after wake: {item}"));
        if (mode is not null)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"controller mode after wake: {mode}"));
        }

        var observed = segment!.Summary
                       ?? (missing.Length == 0
                           ? string.Create(CultureInfo.InvariantCulture, $"everything came back after {back ?? 0:0} ms")
                           : $"missing after wake: {string.Join(", ", missing)}");
        items.Add(new LabReviewItem
        {
            Field = "sleep",
            Area = "sleep",
            Verdict = segment.Status == nameof(LabSegmentStatus.Completed) && missing.Length == 0
                ? LabReviewVerdict.Observed
                : LabReviewVerdict.Unresolved,
            Record = init is null ? null : DescribeMechanism(init),
            Observed = observed,
            Detail = init is null
                ? "The record holds no sleep behaviour; listed for the plugin author."
                : initNode is null
                    ? $"The record's {init.Feature} was not checked across sleep."
                    : resent
                        ? $"The record's {init.Feature} did not survive sleep and had to be sent again."
                        : $"The record's {init.Feature} survived sleep.",
            Evidence = segment.Reference,
            Candidates = lines
        });
    }

    private static string Compact(JsonNode node)
    {
        var text = node.ToJsonString();
        return text.Length > 120 ? text[..120] + "..." : text;
    }
}
