using System.Globalization;
using System.Text;
using WSGM.Core;

namespace WSGM.Tests.Builders;

/// <summary>Replays an AutoTDP CSV trace, as written by the trace recorder, through a controller.</summary>
/// <remarks>
///     Columns are read by name, so a trace with columns appended later still replays. Replay starts at
///     the first window that started the controller; rows recorded before it depend on state the file
///     never captured.
///     <para>
///         Only raw inputs are fed back in — the RTSS window, the deadline, the device bounds and the
///         sensor sample — so a file recorded under one policy can drive a different one. Comparing the
///         recorded decision with the replayed one is then meaningful only within a policy; across a
///         policy change the replayed series is the thing under test.
///     </para>
/// </remarks>
internal static class AutoTdpTraceReplay
{
    /// <summary>Replays a trace file with a fresh controller.</summary>
    /// <param name="path">The CSV file.</param>
    /// <returns>Each evaluated row, recorded decision beside replayed decision.</returns>
    internal static IReadOnlyList<AutoTdpReplayedDecision> Run(string path)
    {
        return Run(File.ReadAllLines(path), new AutoTdpController());
    }

    /// <summary>Replays trace lines through a controller, which may be a candidate policy under test.</summary>
    /// <param name="lines">The CSV lines, header first.</param>
    /// <param name="controller">The controller to drive.</param>
    /// <returns>Each evaluated row, recorded decision beside replayed decision.</returns>
    internal static IReadOnlyList<AutoTdpReplayedDecision> Run(
        IReadOnlyList<string> lines,
        AutoTdpController controller)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(controller);
        var header = ParseLine(lines[0]);
        var index = header.Select((name, position) => (name, position))
            .ToDictionary(column => column.name, column => column.position, StringComparer.Ordinal);
        var actions = Enum.GetValues<AutoTdpAction>()
            .ToDictionary(AutoTdpTraceCsv.ActionToken, action => action, StringComparer.Ordinal);
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

            double? Number(string name)
            {
                return Field(name) is { Length: > 0 } value
                    ? double.Parse(value, CultureInfo.InvariantCulture)
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

            var action = Field("action");
            if (action.Length == 0 && Field("rtss_time1").Length == 0)
            {
                // The service returned before the controller saw this window.
                continue;
            }

            var context = Field("context_key");
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

            var replayed = controller.Observe(
                new AutoTdpObservation(
                    Number("elapsed_ms") ?? 0,
                    context,
                    Number("target_frametime_ms") ?? 0,
                    ReadWindow(Number, Integer),
                    Number("gpu_load_percent"),
                    Number("cpu_load_percent")),
                limits);
            decisions.Add(new AutoTdpReplayedDecision(
                long.Parse(Field("row"), CultureInfo.InvariantCulture),
                action.Length == 0
                    ? null
                    : new AutoTdpDecision(actions[action], Integer("decision_w")!.Value, Field("reason")),
                replayed));
        }

        return decisions;
    }

    /// <summary>Splits one CSV line, honouring quoted fields with doubled quotes.</summary>
    /// <param name="line">The line.</param>
    /// <returns>Its fields.</returns>
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

    private static AutoTdpWindow? ReadWindow(Func<string, double?> number, Func<string, int?> integer)
    {
        if (number("rtss_time0") is not { } start || number("rtss_time1") is not { } end)
        {
            return null;
        }

        var raw = number("rtss_frametime_raw");
        return new AutoTdpWindow(
            (uint)start,
            (uint)end,
            (uint)(integer("rtss_frames") ?? 0),
            number("rtss_mean_frametime_ms") ?? 0,
            raw > 0 ? raw / 1000d : null,
            number("rtss_age_ms") ?? 0);
    }
}
