using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using WSGM.DeviceLab.Knowledge;

namespace WSGM.DeviceLab.Wizard;

// Rumble: which route the tester felt, and its calibration, against the record's rumble mechanism.
internal static partial class LabReview
{
    private static void ReviewRumble(LabReviewArchive archive, DeviceKnowledgeRecord? record, List<LabReviewItem> items)
    {
        var mechanisms = record?.Mechanisms.Where(mechanism => mechanism.Feature == "rumble").ToArray() ?? [];
        var segment = archive.Segment(LabStages.Rumble);
        var reference = segment?.Reference ?? $"segments/{LabStages.Rumble}";
        var routesJson = segment is null ? null : archive.ReadEvidence(segment, "rumble-routes.json");
        if (routesJson is null)
        {
            items.Add(new LabReviewItem
            {
                Field = "rumble:route",
                Area = "rumble",
                Verdict = LabReviewVerdict.Unresolved,
                Record = mechanisms.Length == 0 ? null : string.Join("; ", mechanisms.Select(DescribeMechanism)),
                Detail = segment?.Prefix is null ? "The rumble stage never ran." : "The rumble attempt holds no route evidence.",
                Evidence = reference
            });
            return;
        }

        Dictionary<string, (string Kind, string Name, string? Report)> routes = new(StringComparer.Ordinal);
        List<string> order = [];
        foreach (var route in LabReviewArchive.Objects(LabReviewArchive.Get(routesJson, "routes")))
        {
            if (LabReviewArchive.Text(route["id"]) is { } id && LabReviewArchive.Text(route["kind"]) is { } kind
                                                           && routes.TryAdd(id, (kind, LabReviewArchive.Text(route["name"]) ?? kind,
                                                               LabReviewArchive.Text(route["report"]))))
            {
                order.Add(id);
            }
        }

        Dictionary<string, bool?> felt = new(StringComparer.Ordinal);
        foreach (var probe in LabReviewArchive.Objects(LabReviewArchive.Get(routesJson, "probes")))
        {
            if (LabReviewArchive.Text(probe["route"]) is { } id)
            {
                felt[id] = LabReviewArchive.Boolean(probe["felt"]);
            }
        }

        var calibrations = LabReviewArchive.Objects(LabReviewArchive.Get(
                archive.ReadEvidence(segment!, "rumble-calibration.json"), "routes"))
            .Where(calibration => LabReviewArchive.Text(calibration["route"]) is not null)
            .ToDictionary(calibration => LabReviewArchive.Text(calibration["route"])!, StringComparer.Ordinal);
        var lines = order.Select(id => $"{routes[id].Name} ({routes[id].Kind}): {felt.GetValueOrDefault(id) switch
        {
            true => "felt",
            false => "not felt",
            null => "not played"
        }}{(calibrations.TryGetValue(id, out var calibration) ? "; " + DescribeCalibration(calibration) : string.Empty)}").ToList();
        lines.AddRange(LabReviewArchive.Strings(LabReviewArchive.Get(routesJson, "notes")).Take(CandidateLines));
        var working = order.Where(id => felt.GetValueOrDefault(id) == true).ToArray();
        var observed = working.Length == 0
            ? "no route was felt"
            : string.Join(", ", working.Select(id => routes[id].Name).Distinct(StringComparer.Ordinal)) + " felt";

        // One item per recorded mechanism: the route of its transport was felt, not felt, or not offered.
        foreach (var mechanism in mechanisms)
        {
            var same = order.Where(id => routes[id].Kind == mechanism.Transport).ToArray();
            var feltHere = same.FirstOrDefault(id => felt.GetValueOrDefault(id) == true);
            var tried = same.Any(id => felt.GetValueOrDefault(id) == false);
            items.Add(new LabReviewItem
            {
                Field = $"rumble:{mechanism.Transport}",
                Area = "rumble",
                Verdict = feltHere is not null
                    ? LabReviewVerdict.Confirmed
                    : tried
                        ? LabReviewVerdict.Disagrees
                        : LabReviewVerdict.Unresolved,
                Record = DescribeMechanism(mechanism),
                Observed = observed,
                Detail = feltHere is not null
                    ? $"The tester felt the record's {mechanism.Transport} route"
                      + (calibrations.TryGetValue(feltHere, out var calibrated) ? $"; {DescribeCalibration(calibrated)}." : ".")
                    : tried
                        ? $"The record's {mechanism.Transport} route was played and not felt."
                        : $"The record's {mechanism.Transport} route was not offered; see the route notes.",
                Evidence = reference,
                Candidates = lines,
                Proposal = feltHere is null ? null : new LabMechanismProposal(mechanism, mechanism)
            });
        }

        // The route that works, as a mechanism, when the record has none.
        if (mechanisms.Length == 0)
        {
            var first = working.FirstOrDefault();
            items.Add(new LabReviewItem
            {
                Field = "rumble:route",
                Area = "rumble",
                Verdict = first is null ? LabReviewVerdict.Unresolved : LabReviewVerdict.New,
                Observed = observed,
                Detail = first is null
                    ? "No route was felt."
                    : $"The record has no rumble mechanism; {routes[first].Name} works.",
                Evidence = reference,
                Candidates = lines,
                Proposal = first is null
                    ? null
                    : new LabMechanismProposal(null, new DeviceMechanismKnowledge
                    {
                        Feature = "rumble",
                        Transport = routes[first].Kind,
                        Parameters = CalibrationParameters(
                            calibrations.GetValueOrDefault(first), routes[first].Report)
                    })
            });
        }
    }

    private static string DescribeMechanism(DeviceMechanismKnowledge mechanism)
    {
        return $"{mechanism.Feature} via {mechanism.Transport}{(mechanism.HasReadback ? " with readback" : string.Empty)}";
    }

    private static string DescribeCalibration(JsonObject calibration)
    {
        List<string> parts = [];
        if (LabReviewArchive.Boolean(calibration["swapped"]) is { } swapped)
        {
            parts.Add(swapped ? "motors swapped" : "motors not swapped");
        }

        foreach (var side in LabReviewArchive.Objects(calibration["sides"]))
        {
            var minimum = LabReviewArchive.Integer(side["minimumStartIntensityPercent"]);
            var pulse = LabReviewArchive.Integer(side["minimumPulseMilliseconds"]);
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"{LabReviewArchive.Text(side["side"])} floor {(minimum is null ? "?" : $"{minimum} %")}, shortest pulse {(pulse is null ? "?" : $"{pulse} ms")}"));
        }

        if (LabReviewArchive.Text(calibration["stopped"]) is { } stopped)
        {
            parts.Add($"stopped: {stopped}");
        }

        return parts.Count == 0 ? "not calibrated" : string.Join(", ", parts);
    }

    private static Dictionary<string, string> CalibrationParameters(JsonObject? calibration, string? report)
    {
        Dictionary<string, string> parameters = new(StringComparer.Ordinal);
        if (report is not null)
        {
            parameters["report"] = report;
        }

        if (calibration is null)
        {
            return parameters;
        }

        if (LabReviewArchive.Boolean(calibration["swapped"]) is { } swapped)
        {
            parameters["swapped"] = swapped ? "true" : "false";
        }

        foreach (var side in LabReviewArchive.Objects(calibration["sides"]))
        {
            var name = LabReviewArchive.Text(side["side"]);
            if (name is not ("left" or "right"))
            {
                continue;
            }

            if (LabReviewArchive.Integer(side["minimumStartIntensityPercent"]) is { } minimum)
            {
                parameters[$"{name}MinimumStartPercent"] = minimum.ToString(CultureInfo.InvariantCulture);
            }

            if (LabReviewArchive.Integer(side["minimumPulseMilliseconds"]) is { } pulse)
            {
                parameters[$"{name}MinimumPulseMs"] = pulse.ToString(CultureInfo.InvariantCulture);
            }
        }

        return parameters;
    }
}
