using System.Globalization;
using System.Text;
using WSGM.Core;

namespace WSGM.Tests.Builders;

/// <summary>Builds observation runs and replays them through the controller.</summary>
/// <remarks>
///     The regression harness for this feature. A reported oscillation or a limit that walked to maximum
///     is reproduced by recording the trace and replaying it here, with no device involved; the
///     controller is only allowed to grow more sophisticated when a recorded trace defeats the simple
///     one. It lives in the test project because nothing in the application replays a trace.
/// </remarks>
internal static class AutoTdpReplay
{
    /// <summary>The window length RTSS produces at a one-second averaging interval.</summary>
    internal const double WindowMs = 1016;

    /// <summary>Runs a trace and returns every decision in order.</summary>
    /// <param name="controller">The controller under test.</param>
    /// <param name="limits">The device bounds each observation is judged against.</param>
    /// <param name="trace">The observations, in order.</param>
    /// <returns>One decision per observation.</returns>
    internal static IReadOnlyList<AutoTdpDecision> Run(
        AutoTdpController controller,
        AutoTdpLimits limits,
        IEnumerable<AutoTdpObservation> trace)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(trace);
        return [.. trace.Select(observation => controller.Observe(observation, limits))];
    }

    /// <summary>Builds a run of identical windows, each one a fresh measurement.</summary>
    /// <param name="count">How many windows.</param>
    /// <param name="frametimeMs">Their mean frametime.</param>
    /// <param name="targetFrametimeMs">The deadline they are judged against.</param>
    /// <param name="contextKey">The context they belong to.</param>
    /// <param name="gpuLoadPercent">GPU load to report, or null for no sensor.</param>
    /// <returns>The observations.</returns>
    internal static IEnumerable<AutoTdpObservation> Run(
        int count,
        double frametimeMs,
        double targetFrametimeMs,
        string contextKey,
        double? gpuLoadPercent = null)
    {
        for (var index = 0; index < count; index++)
        {
            yield return Window(frametimeMs, targetFrametimeMs, contextKey, gpuLoadPercent);
        }
    }

    /// <summary>Builds one fresh window on the shared clock.</summary>
    /// <param name="frametimeMs">Its mean frametime.</param>
    /// <param name="targetFrametimeMs">The deadline it is judged against.</param>
    /// <param name="contextKey">The context it belongs to.</param>
    /// <param name="gpuLoadPercent">GPU load to report, or null for no sensor.</param>
    /// <param name="durationMs">How long the window covered.</param>
    /// <param name="lastFrameMs">The window's final frame, or null to derive it from the mean.</param>
    /// <returns>The observation.</returns>
    internal static AutoTdpObservation Window(
        double frametimeMs,
        double targetFrametimeMs,
        string contextKey,
        double? gpuLoadPercent = null,
        double durationMs = WindowMs,
        double? lastFrameMs = null)
    {
        var start = Clock.NextWindowStart(durationMs);
        return new AutoTdpObservation(
            Clock.ElapsedMs,
            contextKey,
            targetFrametimeMs,
            new AutoTdpWindow(
                start,
                start + (uint)durationMs,
                Math.Max(1, (uint)(durationMs / frametimeMs)),
                frametimeMs,
                lastFrameMs ?? frametimeMs,
                Clock.AgeMs),
            gpuLoadPercent);
    }

    /// <summary>Builds a tick that read the same window as the one before it.</summary>
    /// <param name="previous">The observation whose window is being read again.</param>
    /// <param name="ageMs">How long ago that window ended, as of this read.</param>
    /// <returns>The observation.</returns>
    /// <remarks>
    ///     The tick is placed so the age is true: a window that ended at one instant cannot be both
    ///     1032 ms and 1300 ms old at the same moment, and a builder that emitted such a pair would
    ///     be testing arithmetic the real reader never produces.
    /// </remarks>
    internal static AutoTdpObservation Repeat(AutoTdpObservation previous, double ageMs)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var window = previous.Window!;
        var elapsed = previous.ElapsedMs - window.AgeMs + ageMs;
        Clock.AdvanceTo(elapsed);
        return previous with
        {
            ElapsedMs = elapsed,
            Window = window with { AgeMs = ageMs }
        };
    }

    /// <summary>Builds a tick with no renderer at all.</summary>
    /// <param name="contextKey">The context that is still running.</param>
    /// <param name="targetFrametimeMs">The deadline still in force.</param>
    /// <returns>The observation.</returns>
    internal static AutoTdpObservation Silent(string contextKey, double targetFrametimeMs)
    {
        Clock.Advance();
        return new AutoTdpObservation(Clock.ElapsedMs, contextKey, targetFrametimeMs, null);
    }

    /// <summary>Restarts the shared clock, so one test's windows never follow another's.</summary>
    internal static void ResetClock()
    {
        Clock.Reset();
    }

    /// <summary>
    ///     The tick clock and RTSS tick counter the builders share.
    /// </summary>
    /// <remarks>
    ///     Window identity and elapsed time are both inputs now, so a builder cannot emit constants:
    ///     two windows with the same bounds are one measurement read twice, and a controller whose
    ///     dwells are sums of window time never advances on a frozen clock. Test parallelization is
    ///     off in this project, which is what lets this be static.
    /// </remarks>
    private static class Clock
    {
        private const uint FirstTick = 100_000;
        private static uint _tick = FirstTick;

        internal static double ElapsedMs { get; private set; }

        /// <summary>How long ago the newest window ended, as the service would have read it.</summary>
        internal static double AgeMs => 16;

        internal static void Reset()
        {
            _tick = FirstTick;
            ElapsedMs = 0;
        }

        internal static void Advance()
        {
            ElapsedMs += WindowMs;
        }

        internal static void AdvanceTo(double elapsedMs)
        {
            ElapsedMs = Math.Max(ElapsedMs, elapsedMs);
        }

        internal static uint NextWindowStart(double durationMs)
        {
            var start = _tick;
            _tick += (uint)durationMs;
            ElapsedMs += durationMs;
            return start;
        }
    }
}

/// <summary>One decision from a recorded AutoTDP trace and the decision replay produced for it.</summary>
/// <param name="Row">The trace row number.</param>
/// <param name="Recorded">What the live controller decided, or null in a hand-authored fixture.</param>
/// <param name="Replayed">What a fresh controller decides from the same recorded inputs.</param>
internal sealed record AutoTdpReplayedDecision(long Row, AutoTdpDecision? Recorded, AutoTdpDecision Replayed);

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
