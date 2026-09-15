using System.Text.Json;

namespace WSGM.AllyXLab;

internal static class Analysis
{
    internal static object Summarize(IReadOnlyList<Result> results)
    {
        List<object> buttonCandidates = [];
        List<object> stationary = [];
        var gravity = new Dictionary<string, List<(string Pose, double Mean)>>();
        foreach (Result result in results.Where(r => r.Request.Action == ActionKind.Capture))
        {
            var reports = result.Events.Where(e => e.Kind == "asus-hid").Select(e => (JsonElement)e.Data);
            var candidates = reports.SelectMany(e => e.GetProperty("ChangedFromFirstBytes").EnumerateArray()
                .Select(offset => new { Endpoint = e.GetProperty("Endpoint").GetString(), ReportId = e.GetProperty("ReportId").GetInt32(), Byte = offset.GetInt32() }))
                .Distinct().ToArray();
            buttonCandidates.Add(new
            {
                Step = result.Request.Label,
                CandidateBytes = candidates,
                KeyboardEvents = result.Events.Count(e => e.Kind == "asus-keyboard"),
                XInputEvents = result.Events.Count(e => e.Kind == "xinput"),
                Outcome = result.Outcome
            });
            foreach (LabEvent item in result.Events.Where(e => e.Kind == "motion-statistics"))
            {
                var data = (JsonElement)item.Data;
                if (result.Request.Label == "Gyro stationary bias")
                {
                    stationary.Add(new { Label = result.Request.Label, Statistics = data });
                }

                if (!result.Request.Label.StartsWith("Gravity:", StringComparison.Ordinal) || result.Error is not null)
                {
                    continue;
                }

                foreach (var field in data.GetProperty("Fields").EnumerateArray())
                {
                    if (field.GetProperty("Count").GetInt32() < 20)
                    {
                        continue;
                    }

                    string key = field.GetProperty("Field").GetString()!;
                    // Counter/timestamp fields are kept in raw evidence, never used as an axis.
                    bool axis = key.Contains("3f8a69a2-07c5-4e48-a965-cd797aab56d5:2:", StringComparison.OrdinalIgnoreCase)
                        || key.Contains("3f8a69a2-07c5-4e48-a965-cd797aab56d5:3:", StringComparison.OrdinalIgnoreCase)
                        || key.Contains("3f8a69a2-07c5-4e48-a965-cd797aab56d5:4:", StringComparison.OrdinalIgnoreCase)
                        || Enumerable.Range(7, 3).Any(pid => key.Contains($"b14c764f-07cf-41e8-9d82-ebe3d0776a6f:{pid}:", StringComparison.OrdinalIgnoreCase));
                    if (!axis)
                    {
                        continue;
                    }

                    if (!gravity.TryGetValue(key, out var values))
                    {
                        gravity[key] = values = [];
                    }

                    values.Add((result.Request.Label, field.GetProperty("Mean").GetDouble()));
                }
            }
        }
        var poses = gravity.Select(pair =>
        {
            var means = pair.Value.GroupBy(x => x.Pose).Select(g => (Pose: g.Key, Mean: g.Last().Mean)).ToArray();
            var min = means.MinBy(x => x.Mean); var max = means.MaxBy(x => x.Mean);
            return new
            {
                Field = pair.Key,
                PoseCount = means.Length,
                CompleteSixPoses = means.Length == 6,
                PositivePose = max.Pose,
                NegativePose = min.Pose,
                OffsetCandidate = (max.Mean + min.Mean) / 2,
                OneGravityMagnitudeCandidate = (max.Mean - min.Mean) / 2,
                Means = means.Select(x => new { x.Pose, x.Mean }).ToArray()
            };
        }).ToArray();
        return new
        {
            Status = "Source/measurement candidates only; not an accepted hardware contract",
            Input = buttonCandidates,
            StationaryBiasCandidates = stationary,
            GravityAxisCandidates = poses,
            Limits = "Sensor IDs are stable hashes of the local Sensor API ID. Custom fields may belong to a gyro rather than an accelerometer; classify by recorded sensor metadata. Six-pose summaries require all six poses, correct positioning, fresh samples and unit confirmation. Changed bytes may contain counters, axes or button bits. Repeated/counterexample trials are required.",
            Steps = results.Select(r => new { r.Request.Label, r.Request.Action, r.Outcome, r.Cleanup, r.Error }).ToArray(),
        };
    }
}
