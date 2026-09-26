using System.Globalization;
using System.Text;
using WSGM.Core;

namespace WSGM.Tests.Builders;

/// <summary>Replays a recorded frametime trace through the controller.</summary>
/// <remarks>
///     The regression harness for this feature. A reported oscillation or a limit that walked to maximum
///     is reproduced by recording the trace and replaying it here, with no device involved; the
///     controller is only allowed to grow more sophisticated when a recorded trace defeats the simple
///     one. It lives in the test project because nothing in the application replays a trace.
/// </remarks>
internal static class AutoTdpReplay
{
    /// <summary>Runs a trace and returns every decision in order.</summary>
    internal static IReadOnlyList<AutoTdpDecision> Run(
        AutoTdpController controller,
        AutoTdpLimits limits,
        IEnumerable<AutoTdpSample> trace)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(trace);
        return [.. trace.Select(sample => controller.Evaluate(sample, limits))];
    }

    /// <summary>Builds a run of identical windows.</summary>
    internal static IEnumerable<AutoTdpSample> Run(
        int count,
        double frametimeMs,
        double targetFrametimeMs,
        string contextKey,
        bool capped = false)
    {
        for (var index = 0; index < count; index++)
        {
            yield return new AutoTdpSample(frametimeMs, targetFrametimeMs, capped, contextKey);
        }
    }
}

/// <summary>One decision from a recorded AutoTDP trace and the decision replay produced for it.</summary>
/// <param name="Row">The trace row number.</param>
/// <param name="Recorded">What the live controller decided.</param>
/// <param name="Replayed">What a fresh controller decides from the same recorded inputs.</param>
internal sealed record AutoTdpReplayedDecision(long Row, AutoTdpDecision Recorded, AutoTdpDecision Replayed);

/// <summary>Replays an AutoTDP CSV trace, as written by the trace recorder, through a controller.</summary>
/// <remarks>
///     Columns are read by name, so a trace with columns appended later still replays. Replay starts at
///     the first window that started the controller; rows recorded before it depend on state the file
///     never captured. Learning is seeded per context from the prior-floor columns before that
///     context's first window, because learning outlives the generation a file covers.
/// </remarks>
internal static class AutoTdpTraceReplay
{
    /// <summary>Replays a trace file with a fresh controller.</summary>
    internal static IReadOnlyList<AutoTdpReplayedDecision> Run(string path)
    {
        return Run(File.ReadAllLines(path), new AutoTdpController());
    }

    /// <summary>Replays trace lines through a controller, which may be a candidate policy under test.</summary>
    internal static IReadOnlyList<AutoTdpReplayedDecision> Run(IReadOnlyList<string> lines, AutoTdpController controller)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(controller);
        var header = ParseLine(lines[0]);
        var index = header.Select((name, position) => (name, position))
            .ToDictionary(column => column.name, column => column.position, StringComparer.Ordinal);
        var actions = Enum.GetValues<AutoTdpAction>()
            .ToDictionary(AutoTdpTraceCsv.ActionToken, action => action, StringComparer.Ordinal);
        HashSet<string> seeded = new(StringComparer.Ordinal);
        List<AutoTdpReplayedDecision> decisions = [];
        var started = false;
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = ParseLine(line);
            string Field(string name)
            {
                return index.TryGetValue(name, out var position) && position < fields.Count
                    ? fields[position]
                    : string.Empty;
            }

            int? Integer(string name)
            {
                return Field(name) is { Length: > 0 } value
                    ? int.Parse(value, CultureInfo.InvariantCulture)
                    : null;
            }

            switch (Field("event"))
            {
                case "manual-pause" when started:
                    controller.PauseForManualChange(Integer("believed_w")!.Value);
                    continue;
                case "resume" when started:
                    controller.ResumeAutomaticControl();
                    continue;
                case "tick":
                    break;
                default:
                    continue;
            }

            if (Field("action") is not { Length: > 0 } action)
            {
                // The service returned before the controller saw this window.
                continue;
            }

            var context = Field("context_key");
            if (seeded.Add(context))
            {
                controller.RestoreLearning(
                    context,
                    Integer("prior_learned_floor_w"),
                    Integer("prior_failed_probe_floor_w"));
            }

            AutoTdpLimits limits = new(
                Integer("limit_min_w")!.Value,
                Integer("limit_max_w")!.Value,
                Integer("limit_step_w")!.Value);
            if (Field("controller_started") == "1")
            {
                started = true;
                controller.Start(Integer("start_w")!.Value, limits, context);
            }

            if (!started)
            {
                continue;
            }

            var replayed = controller.Evaluate(
                new AutoTdpSample(
                    double.Parse(Field("rtss_mean_frametime_ms"), CultureInfo.InvariantCulture),
                    double.Parse(Field("target_frametime_ms"), CultureInfo.InvariantCulture),
                    Field("capped") == "1",
                    context),
                limits);
            decisions.Add(new AutoTdpReplayedDecision(
                long.Parse(Field("row"), CultureInfo.InvariantCulture),
                new AutoTdpDecision(actions[action], Integer("decision_w")!.Value, Field("reason")),
                replayed));
        }

        return decisions;
    }

    /// <summary>Splits one CSV line, honouring quoted fields with doubled quotes.</summary>
    internal static IReadOnlyList<string> ParseLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        List<string> fields = [];
        StringBuilder field = new();
        var quoted = false;
        for (var position = 0; position < line.Length; position++)
        {
            var character = line[position];
            if (quoted)
            {
                if (character != '"')
                {
                    field.Append(character);
                }
                else if (position + 1 < line.Length && line[position + 1] == '"')
                {
                    field.Append('"');
                    position++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (character == '"')
            {
                quoted = true;
            }
            else if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }
}
