using System.Text.Json;

namespace WSGM.AllyXLab;

internal static class Analysis
{
    private const string AccelerometerFields = "3f8a69a2-07c5-4e48-a965-cd797aab56d5";

    internal static object Summarize(IReadOnlyList<Result> results)
    {
        List<object> steps = [];
        List<object> stationary = [];
        var gravity = new Dictionary<string, List<(string Pose, double Mean, int Count)>>();
        foreach (Result result in results)
        {
            foreach (LabEvent item in result.Events)
            {
                JsonElement data = JsonSerializer.SerializeToElement(item.Data, SessionLog.Json);
                if (item.Kind == "step-summary")
                {
                    steps.Add(data);
                    continue;
                }

                if (item.Kind != "motion-statistics")
                {
                    continue;
                }

                string step = data.TryGetProperty("Step", out JsonElement label) ? label.GetString() ?? "" : "";
                if (step.Contains("rest", StringComparison.OrdinalIgnoreCase) || step.Contains("bias", StringComparison.OrdinalIgnoreCase))
                {
                    stationary.Add(new { Step = step, Statistics = data });
                }

                if (!step.StartsWith("Gravity:", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (JsonElement field in data.GetProperty("Fields").EnumerateArray())
                {
                    string key = field.GetProperty("Field").GetString()!;
                    int count = field.GetProperty("Count").GetInt32();
                    // A resting sensor reports only when it changes, so a still pose yields few
                    // samples. Every pose with a sample is kept, and the count travels with it.
                    if (count < 1 || !key.Contains(AccelerometerFields, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!gravity.TryGetValue(key, out var values))
                    {
                        gravity[key] = values = [];
                    }

                    values.Add((step, field.GetProperty("Mean").GetDouble(), count));
                }
            }
        }

        var poses = gravity.Select(pair =>
        {
            var means = pair.Value.GroupBy(x => x.Pose).Select(g => (Pose: g.Key, g.Last().Mean, g.Last().Count)).ToArray();
            var min = means.MinBy(x => x.Mean);
            var max = means.MaxBy(x => x.Mean);
            return new
            {
                Field = pair.Key,
                PoseCount = means.Length,
                CompleteSixPoses = means.Length == 6,
                PositivePose = max.Pose,
                NegativePose = min.Pose,
                OffsetCandidate = (max.Mean + min.Mean) / 2,
                OneGravityMagnitudeCandidate = (max.Mean - min.Mean) / 2,
                Means = means.Select(x => new { x.Pose, x.Mean, x.Count }).ToArray(),
            };
        }).ToArray();

        return new
        {
            Status = "Source/measurement candidates only; not an accepted hardware contract",
            Input = steps,
            RumbleCalibration = results.SelectMany(r => r.Events.Where(e => e.Kind == "rumble-calibration-summary").Select(e => e.Data)).ToArray(),
            MotorRoutes = results.SelectMany(r => r.Events.Where(e => e.Kind is "motor-routes" or "motor-probe").Select(e => e.Data)).ToArray(),
            StationaryBiasCandidates = stationary,
            GravityAxisCandidates = poses,
            Limits = "Sources listed for a step are what reported while it ran, not proof of which device produced the press. Sensor IDs are stable hashes of the local Sensor API ID; a pose with few samples is a weak mean. Changed bytes may be counters, axes or button bits. Rumble boundaries hold only for the route they were measured on.",
            Steps = results.Select(r => new { r.Request.Label, r.Request.Action, r.Outcome, r.Cleanup, r.Error }).ToArray(),
        };
    }
}
