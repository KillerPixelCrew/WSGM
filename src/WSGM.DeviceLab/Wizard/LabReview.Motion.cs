using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using WSGM.DeviceLab.Knowledge;

namespace WSGM.DeviceLab.Wizard;

// Motion: the axis maps the stage deduced per sensor against the record's maps.
internal static partial class LabReview
{
    private static readonly string[] MotionAxes = ["X", "Y", "Z"];

    /// <summary>
    ///     Converts a map in the motion stage's device frame to HC's output frame (X, Z, -Y), the inverse of
    ///     <see cref="LabMotionAnalysis.ToDeviceFrame" />.
    /// </summary>
    /// <param name="device">Map in the device frame.</param>
    /// <returns>The same map as a record stores it.</returns>
    internal static DeviceAxisMap FromDeviceFrame(DeviceAxisMap device)
    {
        Dictionary<string, string> swap = [];
        Dictionary<string, int> sign = [];
        foreach (var (raw, axis) in device.Swap.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var deviceSign = device.Sign.GetValueOrDefault(axis, 1);
            var (output, factor) = axis switch
            {
                "X" => ("X", 1),
                "Z" => ("Y", 1),
                "Y" => ("Z", -1),
                _ => (axis, 1)
            };
            swap[raw] = output;
            sign[output] = deviceSign * factor;
        }

        return new DeviceAxisMap { Swap = swap, Sign = sign };
    }

    /// <summary>Describes a map, for example <c>X→+X, Y→-Z, Z→+Y</c>.</summary>
    /// <param name="map">Map.</param>
    /// <returns>One line; raw axis, then the signed axis it becomes.</returns>
    internal static string DescribeMap(DeviceAxisMap map)
    {
        return string.Join(", ", map.Swap.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}→{(map.Sign.GetValueOrDefault(pair.Value, 1) < 0 ? "-" : "+")}{pair.Value}"));
    }

    private static void ReviewMotion(LabReviewArchive archive, DeviceKnowledgeRecord? record, List<LabReviewItem> items)
    {
        var segment = archive.Segment(LabStages.Motion);
        JsonNode? summary = null;
        if (segment is not null)
        {
            // The stage writes motion-summary.json; any other file whose root lists analysed sensors is
            // accepted in its place.
            summary = archive.ReadEvidence(segment, "motion-summary.json");
            foreach (var file in summary is null ? archive.JsonFiles(segment) : [])
            {
                var node = archive.ReadJson(file);
                if (LabReviewArchive.Objects(LabReviewArchive.Get(node, "sensors"))
                    .Any(sensor => LabReviewArchive.Text(sensor["sensorId"]) is not null))
                {
                    summary = node;
                    break;
                }
            }
        }

        foreach (var (kind, known) in new[]
                 {
                     ("gyrometer", record?.Motion?.Gyrometer), ("accelerometer", record?.Motion?.Accelerometer)
                 })
        {
            var recordText = known is null
                ? null
                : $"{DescribeMap(known)} (device frame {DescribeMap(LabMotionAnalysis.ToDeviceFrame(known))})";
            var baseItem = new LabReviewItem
            {
                Field = $"motion:{kind}",
                Area = "motion",
                Verdict = LabReviewVerdict.Unresolved,
                Record = recordText,
                Evidence = segment?.Reference ?? $"segments/{LabStages.Motion}"
            };
            if (summary is null)
            {
                items.Add(baseItem with
                {
                    Detail = segment?.Prefix is null ? "The motion stage never ran." : "The motion attempt holds no analysis."
                });
                continue;
            }

            List<(string Sensor, DeviceAxisMap Map, bool Clear)> maps = [];
            foreach (var sensor in LabReviewArchive.Objects(LabReviewArchive.Get(summary, "sensors")))
            {
                if (!string.Equals(LabReviewArchive.Text(sensor["kind"]), kind, StringComparison.OrdinalIgnoreCase)
                    || ReadMap(sensor["map"]) is not { } map)
                {
                    continue;
                }

                var label = $"{LabReviewArchive.Text(sensor["source"])} {LabReviewArchive.Text(sensor["name"]) ?? LabReviewArchive.Text(sensor["sensorId"])}".Trim();
                maps.Add((label, map, LabReviewArchive.Boolean(sensor["mapClear"]) == true && map.Swap.Count == 3));
            }

            var lines = maps.Select(item => $"{item.Sensor}: {DescribeMap(item.Map)}{(item.Clear ? string.Empty : " (unclear)")}")
                .ToArray();
            var clear = maps.Where(item => item.Clear).ToArray();
            var distinct = clear.Select(item => DescribeMap(item.Map)).Distinct(StringComparer.Ordinal).ToArray();
            if (distinct.Length != 1)
            {
                items.Add(baseItem with
                {
                    Candidates = lines,
                    Observed = distinct.Length == 0 ? null : string.Join(" | ", distinct),
                    Detail = distinct.Length == 0
                        ? $"No {kind} gave a clear map."
                        : $"The {kind} sources deduced different maps."
                });
                continue;
            }

            var deduced = clear[0].Map;
            var hc = FromDeviceFrame(deduced);
            var observed = $"{DescribeMap(hc)} (device frame {distinct[0]}) from {string.Join(", ", clear.Select(item => item.Sensor))}";
            if (known is null)
            {
                items.Add(baseItem with
                {
                    Verdict = LabReviewVerdict.New,
                    Observed = observed,
                    Candidates = lines,
                    Detail = $"The record has no {kind} map.",
                    Proposal = new LabAxisProposal(kind, hc)
                });
                continue;
            }

            var comparison = LabMotionAnalysis.Compare(deduced, LabMotionAnalysis.ToDeviceFrame(known));
            var agrees = comparison.All(axis => axis.Agrees == true);
            items.Add(baseItem with
            {
                Verdict = agrees ? LabReviewVerdict.Confirmed : LabReviewVerdict.Disagrees,
                Observed = observed,
                Candidates = lines,
                Detail = agrees
                    ? "Every axis and sign agrees."
                    : "Differs on " + string.Join(", ", comparison.Where(axis => axis.Agrees != true).Select(axis =>
                        $"device {axis.DeviceAxis} (lab {Signed(axis.DeducedSign)}{axis.DeducedRaw ?? "?"}, record {Signed(axis.KnownSign)}{axis.KnownRaw ?? "?"})")),
                Proposal = new LabAxisProposal(kind, hc)
            });
        }
    }

    private static string Signed(int? sign)
    {
        return sign switch { < 0 => "-", > 0 => "+", _ => string.Empty };
    }

    private static DeviceAxisMap? ReadMap(JsonNode? node)
    {
        Dictionary<string, string> swap = new(StringComparer.Ordinal);
        Dictionary<string, int> sign = new(StringComparer.Ordinal);
        if (LabReviewArchive.Get(node, "swap") is JsonObject swapNode)
        {
            foreach (var (raw, value) in swapNode)
            {
                if (MotionAxes.Contains(raw) && LabReviewArchive.Text(value) is { } axis && MotionAxes.Contains(axis))
                {
                    swap[raw] = axis;
                }
            }
        }

        if (LabReviewArchive.Get(node, "sign") is JsonObject signNode)
        {
            foreach (var (axis, value) in signNode)
            {
                if (MotionAxes.Contains(axis) && LabReviewArchive.Integer(value) is 1 or -1)
                {
                    sign[axis] = LabReviewArchive.Integer(value)!.Value;
                }
            }
        }

        // A usable map is a permutation with a sign for every output axis.
        return swap.Count == 0
               || swap.Values.Distinct(StringComparer.Ordinal).Count() != swap.Count
               || !swap.Values.All(sign.ContainsKey)
            ? null
            : new DeviceAxisMap { Swap = swap, Sign = sign };
    }
}
