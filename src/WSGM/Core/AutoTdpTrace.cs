using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace WSGM.Core;

/// <summary>What caused one AutoTDP trace row.</summary>
internal enum AutoTdpTraceEvent
{
    /// <summary>One controller window.</summary>
    Tick,

    /// <summary>A control generation started.</summary>
    Enabled,

    /// <summary>A control generation ended and its restoration finished.</summary>
    Disabled,

    /// <summary>The running application changed.</summary>
    Application,

    /// <summary>A manual power change paused control.</summary>
    ManualPause,

    /// <summary>Control resumed after a scoped override ended.</summary>
    Resume
}

/// <summary>
///     One AutoTDP trace row. Nullable observations are absent when unavailable;
///     an empty CSV cell must not be interpreted as zero.
/// </summary>
/// <remarks>
///     Raw RTSS window bounds and timings preserve the evidence used for a controller decision.
///     Device observations may be the last published write rather than a hardware readback.
/// </remarks>
internal sealed class AutoTdpTraceRow
{
    /// <summary>Event that caused this row.</summary>
    internal AutoTdpTraceEvent Event { get; init; }

    /// <summary>One-based row sequence within the trace, assigned at commit.</summary>
    internal long Row { get; set; }

    /// <summary>Control clock supplied by the caller, or milliseconds since this trace began.</summary>
    internal double ElapsedMs { get; set; }

    /// <summary>The control clock the caller judged this tick on, when it owns one.</summary>
    internal double? ControlClockMs { get; init; }

    /// <summary>Wall-clock timestamp captured when the row began.</summary>
    internal DateTimeOffset WallClock { get; set; }

    /// <summary>Milliseconds since the previous committed tick; null for the first tick.</summary>
    internal double? TickIntervalMs { get; set; }

    /// <summary>Identifier shared by rows in the current recording.</summary>
    internal string? TraceId { get; set; }

    /// <summary>Application version recorded with this row.</summary>
    internal string? WsgmVersion { get; set; }

    /// <summary>Active device package identity, cached for event rows.</summary>
    internal string? Package { get; set; }

    /// <summary>Active device package version, cached for event rows.</summary>
    internal string? PackageVersion { get; set; }

    /// <summary>Authoritative running application identity, when known.</summary>
    internal string? ApplicationId { get; set; }

    /// <summary>Running application executable identity, when known.</summary>
    internal string? Executable { get; set; }

    /// <summary>Process identity of the selected RTSS renderer.</summary>
    internal uint? ProcessId { get; set; }

    /// <summary>Running-application snapshot generation.</summary>
    internal long? RunningGeneration { get; set; }

    /// <summary>Controller operating-point identity used to detect a required rebase.</summary>
    internal string? ContextKey { get; set; }

    /// <summary>Resolved target frametime in milliseconds.</summary>
    internal double? TargetFrametimeMs { get; set; }

    /// <summary>Windows AC-line state, when available.</summary>
    internal bool? AcPower { get; set; }

    /// <summary>Windows battery percentage, when available.</summary>
    internal double? BatteryPercent { get; set; }

    /// <summary>Effective Windows power-mode name.</summary>
    internal string? PowerMode { get; set; }

    /// <summary>Active Windows power-scheme GUID text.</summary>
    internal string? PowerScheme { get; set; }

    /// <summary>Resolved sustained-power minimum and ceiling for this decision.</summary>
    internal AutoTdpLimits? Limits { get; set; }

    /// <summary>Device capability identifier for the sustained limit.</summary>
    internal string? PowerCapability { get; set; }

    /// <summary>Paired power-limit capability identifier, when applicable.</summary>
    internal string? PairedCapability { get; set; }

    /// <summary>Published sustained-power value before control; may be the last written value.</summary>
    internal long? ObservedWatts { get; set; }

    /// <summary>Quality of the published sustained-power projection.</summary>
    internal string? ObservedQuality { get; set; }

    /// <summary>Published paired-limit value before control.</summary>
    internal long? PairedObservedWatts { get; set; }

    /// <summary>Device capability projection generation used by the decision.</summary>
    internal long? CycleGeneration { get; set; }

    /// <summary>Number of live RTSS renderers considered for sample selection.</summary>
    internal int? Renderers { get; set; }

    /// <summary>Sample-selection result: none, executable, only-renderer, or ambiguous.</summary>
    internal string? Selection { get; set; }

    /// <summary>Selected RTSS averaging window, including raw bounds and age.</summary>
    internal RtssFrametimeSample? Frametime { get; set; }

    /// <summary>Whether the selected window matches the previous window for this renderer.</summary>
    internal bool? WindowRepeat { get; set; }

    /// <summary>Signed gap from the previous window end to this window start, using wrapping RTSS ticks.</summary>
    internal long? WindowGapMs { get; set; }

    /// <summary>Whether this tick started the controller.</summary>
    internal bool? ControllerStarted { get; set; }

    /// <summary>
    ///     The limit the controller took as current when it started or re-based: the observed
    ///     value, else the value last written, else the ceiling on a device without readback.
    /// </summary>
    internal int? StartWatts { get; set; }

    /// <summary>Whether this tick rebased the controller to a changed operating point.</summary>
    internal bool? Rebased { get; set; }

    /// <summary>Controller diagnostic snapshot associated with this row.</summary>
    internal AutoTdpControllerSnapshot? Controller { get; set; }

    /// <summary>Recording-local probe sequence for a probe or its restoration.</summary>
    internal long? ProbeId { get; set; }

    /// <summary>Controller action and requested power for this tick.</summary>
    internal AutoTdpDecision? Decision { get; set; }

    /// <summary>Controller power before the decision, used to derive the requested delta.</summary>
    internal int? PreviousWatts { get; set; }

    /// <summary>Milliseconds since the preceding committed decision that required a write.</summary>
    internal double? SinceDecisionMs { get; set; }

    /// <summary>Milliseconds since the preceding committed dispatched write.</summary>
    internal double? SinceWriteMs { get; set; }

    /// <summary>Whether a device write was submitted for this row.</summary>
    internal bool? WriteDispatched { get; set; }

    /// <summary>Device command outcome token.</summary>
    internal string? WriteOutcome { get; set; }

    /// <summary>Optional readback carried by the command result; absence or disagreement does not gate acceptance.</summary>
    internal long? WriteReadbackWatts { get; set; }

    /// <summary>Whether the command outcome counts as applied under device-write policy.</summary>
    internal bool? WriteApplied { get; set; }

    /// <summary>Elapsed time awaiting the dispatched device command, in milliseconds.</summary>
    internal double? WriteMs { get; set; }

    /// <summary>Diagnostic reason a write was skipped or failed.</summary>
    internal string? WriteNote { get; set; }

    /// <summary>Published sustained-power projection after the write attempt.</summary>
    internal long? PostObservedWatts { get; set; }

    /// <summary>Published paired-limit projection after the write attempt.</summary>
    internal long? PostPairedObservedWatts { get; set; }

    /// <summary>Shared RTSS sensor sample used as advisory controller evidence.</summary>
    internal RtssOsdMetrics? Metrics { get; set; }

    /// <summary>AutoTDP service state token for this row.</summary>
    internal string? Status { get; set; }

    /// <summary>Additional diagnostic context for the service state.</summary>
    internal string? Detail { get; set; }
}

/// <summary>The AutoTDP trace CSV schema: one ordered column list shared by the writer and replay.</summary>
/// <remarks>
///     Columns are only ever appended. Replay reads them by name, so an older trace stays readable and
///     a newer column never shifts an older one. Raise <see cref="Version" /> when a column's meaning
///     changes.
/// </remarks>
internal static class AutoTdpTraceCsv
{
    /// <summary>Schema version written into every row.</summary>
    /// <remarks>
    ///     Version 2 replaced the learned-floor controller. The floor columns are gone and the state
    ///     column names a control phase rather than three booleans, so a version 1 file describes a
    ///     policy this build no longer has. Its raw RTSS columns still replay.
    /// </remarks>
    internal const int Version = 2;

    private static readonly (string Name, Func<AutoTdpTraceRow, string?> Value)[] Columns =
    [
        ("schema", _ => Version.ToString(CultureInfo.InvariantCulture)),
        ("row", row => Integer(row.Row)),
        ("event", row => EventToken(row.Event)),
        ("elapsed_ms", row => Number(row.ElapsedMs)),
        ("wall_utc", row => row.WallClock.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
        ("tick_interval_ms", row => Number(row.TickIntervalMs)),
        ("trace_id", row => row.TraceId),
        ("wsgm_version", row => row.WsgmVersion),
        ("device_package", row => row.Package),
        ("device_package_version", row => row.PackageVersion),
        ("app_id", row => row.ApplicationId),
        ("executable", row => row.Executable),
        ("process_id", row => Integer(row.ProcessId)),
        ("running_generation", row => Integer(row.RunningGeneration)),
        ("context_key", row => row.ContextKey),
        ("target_frametime_ms", row => Number(row.TargetFrametimeMs)),
        ("target_fps", row => row.TargetFrametimeMs is > 0 and var target ? Number(1000d / target) : null),
        ("ac_power", row => Flag(row.AcPower)),
        ("battery_percent", row => Number(row.BatteryPercent)),
        ("power_mode", row => row.PowerMode),
        ("power_scheme", row => row.PowerScheme),
        ("limit_min_w", row => Integer(row.Limits?.Minimum)),
        ("limit_max_w", row => Integer(row.Limits?.Maximum)),
        ("limit_step_w", row => Integer(row.Limits?.Step)),
        ("power_capability", row => row.PowerCapability),
        ("paired_capability", row => row.PairedCapability),
        ("observed_w", row => Integer(row.ObservedWatts)),
        ("observed_quality", row => row.ObservedQuality),
        ("paired_observed_w", row => Integer(row.PairedObservedWatts)),
        ("cycle_generation", row => Integer(row.CycleGeneration)),
        ("rtss_renderers", row => Integer(row.Renderers)),
        ("rtss_selection", row => row.Selection),
        ("rtss_time0", row => Integer(row.Frametime?.WindowStartTicks)),
        ("rtss_time1", row => Integer(row.Frametime?.WindowEndTicks)),
        ("rtss_window_ms", row => row.Frametime is { } sample
            ? Integer(unchecked(sample.WindowEndTicks - sample.WindowStartTicks))
            : null),
        ("rtss_frames", row => Integer(row.Frametime?.Frames)),
        ("rtss_mean_frametime_ms", row => Number(row.Frametime?.MeanFrametimeMs)),
        ("rtss_fps", row => row.Frametime?.MeanFrametimeMs is > 0 and var mean ? Number(1000d / mean) : null),
        ("rtss_frametime_raw", row => Integer(row.Frametime?.FrameTimeRaw)),
        ("rtss_age_ms", row => Integer(row.Frametime?.AgeMs)),
        ("rtss_window_repeat", row => Flag(row.WindowRepeat)),
        ("rtss_window_gap_ms", row => Integer(row.WindowGapMs)),
        ("controller_started", row => Flag(row.ControllerStarted)),
        ("start_w", row => Integer(row.StartWatts)),
        ("controller_rebased", row => Flag(row.Rebased)),
        ("window_class", row => row.Controller is { } snapshot ? ClassToken(snapshot.Class) : null),
        ("ratio", row => row.Controller is { } snapshot && double.IsFinite(snapshot.Ratio)
            ? Number(snapshot.Ratio)
            : null),
        ("present_gap_ms", row => Number(row.Controller?.GapMs)),
        ("hiatus", row => Flag(row.Controller?.Hiatus)),
        ("state", row => row.Controller is { } snapshot ? PhaseToken(snapshot.Phase, snapshot.IsPaused) : null),
        ("believed_w", row => Integer(row.Controller?.Watts)),
        ("last_good_w", row => Integer(row.Controller?.LastGood)),
        ("miss_count", row => Integer(row.Controller?.MissedWindows)),
        ("dwell_ms", row => Number(row.Controller?.DwellMs)),
        ("dwell_required_ms", row => Number(row.Controller?.RequiredDwellMs)),
        ("raise_baseline", row => row.Controller is { } snapshot && double.IsFinite(snapshot.RaiseBaseline)
            ? Number(snapshot.RaiseBaseline)
            : null),
        ("raise_unimproved", row => Integer(row.Controller?.RaiseUnimproved)),
        ("probe_elapsed", row => Integer(row.Controller?.ProbeWindows)),
        ("settling_windows", row => Integer(row.Controller?.SettlingWindows)),
        ("severe_windows", row => Integer(row.Controller?.SevereWindows)),
        ("utilization_rule", row => row.Controller?.Utilization),
        ("probe_id", row => Integer(row.ProbeId)),
        ("action", row => row.Decision is { } decision ? ActionToken(decision.Action) : null),
        ("reason", row => row.Decision?.Reason),
        ("decision_w", row => Integer(row.Decision?.Watts)),
        ("delta_w", row => row.Decision is { } decision && row.PreviousWatts is { } previous
            ? Integer(decision.Watts - previous)
            : null),
        ("since_decision_ms", row => Number(row.SinceDecisionMs)),
        ("since_write_ms", row => Number(row.SinceWriteMs)),
        ("write_dispatched", row => Flag(row.WriteDispatched)),
        ("write_outcome", row => row.WriteOutcome),
        ("write_readback_w", row => Integer(row.WriteReadbackWatts)),
        ("write_applied", row => Flag(row.WriteApplied)),
        ("write_ms", row => Number(row.WriteMs)),
        ("write_note", row => row.WriteNote),
        ("post_observed_w", row => Integer(row.PostObservedWatts)),
        ("post_paired_observed_w", row => Integer(row.PostPairedObservedWatts)),
        ("cpu_load_percent", row => Number(row.Metrics?.CpuLoadPercent)),
        ("gpu_load_percent", row => Number(row.Metrics?.GpuLoadPercent)),
        ("cpu_power_w", row => Number(row.Metrics?.CpuPowerWatts)),
        ("gpu_power_w", row => Number(row.Metrics?.GpuPowerWatts)),
        ("battery_w", row => Number(row.Metrics?.BatteryWatts)),
        ("status", row => row.Status),
        ("detail", row => row.Detail)
    ];

    /// <summary>The header line.</summary>
    internal static string Header { get; } = string.Join(',', Array.ConvertAll(Columns, column => column.Name));

    /// <summary>The column names, in order.</summary>
    internal static IReadOnlyList<string> ColumnNames { get; } = Array.ConvertAll(Columns, column => column.Name);

    /// <summary>Formats one row as a CSV line without its terminator.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The line.</returns>
    internal static string Format(AutoTdpTraceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        StringBuilder line = new(512);
        for (var index = 0; index < Columns.Length; index++)
        {
            if (index > 0)
            {
                line.Append(',');
            }

            AppendField(line, Columns[index].Value(row));
        }

        return line.ToString();
    }

    /// <summary>Stable token for an event.</summary>
    /// <param name="value">The event.</param>
    /// <returns>The token.</returns>
    internal static string EventToken(AutoTdpTraceEvent value)
    {
        return value switch
        {
            AutoTdpTraceEvent.Tick => "tick",
            AutoTdpTraceEvent.Enabled => "enabled",
            AutoTdpTraceEvent.Disabled => "disabled",
            AutoTdpTraceEvent.Application => "application",
            AutoTdpTraceEvent.ManualPause => "manual-pause",
            AutoTdpTraceEvent.Resume => "resume",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    /// <summary>Stable token for a controller action.</summary>
    /// <param name="value">The action.</param>
    /// <returns>The token.</returns>
    internal static string ActionToken(AutoTdpAction value)
    {
        return value switch
        {
            AutoTdpAction.Hold => "hold",
            AutoTdpAction.Raise => "raise",
            AutoTdpAction.Probe => "probe",
            AutoTdpAction.Restore => "restore",
            AutoTdpAction.Release => "release",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    /// <summary>Stable token for a control phase.</summary>
    /// <param name="phase">The phase.</param>
    /// <param name="paused">Whether a manual change has suspended control.</param>
    /// <returns>The token.</returns>
    internal static string PhaseToken(AutoTdpPhase phase, bool paused)
    {
        if (paused)
        {
            return "paused";
        }

        return phase switch
        {
            AutoTdpPhase.Settling => "settling",
            AutoTdpPhase.Tracking => "tracking",
            AutoTdpPhase.Raising => "raising",
            AutoTdpPhase.Unresponsive => "unresponsive",
            AutoTdpPhase.Quarantine => "quarantine",
            AutoTdpPhase.Probing => "probing",
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };
    }

    /// <summary>Stable token for a window classification.</summary>
    /// <param name="value">The classification.</param>
    /// <returns>The token.</returns>
    internal static string ClassToken(AutoTdpWindowClass value)
    {
        return value switch
        {
            AutoTdpWindowClass.None => "none",
            AutoTdpWindowClass.Severe => "severe",
            AutoTdpWindowClass.Missed => "missed",
            AutoTdpWindowClass.OnTarget => "on-target",
            AutoTdpWindowClass.Comfortable => "comfortable",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private static void AppendField(StringBuilder line, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (value.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            line.Append(value);
            return;
        }

        line.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }

    private static string? Flag(bool? value)
    {
        return value switch
        {
            null => null,
            true => "1",
            false => "0"
        };
    }

    private static string? Integer(long? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture);
    }

    private static string? Number(double? value)
    {
        return value is { } number && double.IsFinite(number)
            ? number.ToString("R", CultureInfo.InvariantCulture)
            : null;
    }
}

/// <summary>Appends AutoTDP trace rows to one CSV file.</summary>
/// <remarks>
///     A diagnostic, never a dependency of control. The first I/O failure closes the file and is logged
///     once; AutoTDP carries on without it. This writer performs synchronous buffered I/O;
///     the recorder owns the queue that keeps it off the controller's thread.
/// </remarks>
internal sealed class AutoTdpTraceWriter : IDisposable
{
    private readonly Lock _gate = new();
    private StreamWriter? _writer;

    private AutoTdpTraceWriter(StreamWriter writer, string path)
    {
        _writer = writer;
        Path = path;
    }

    /// <summary>The file being written.</summary>
    internal string Path { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            Close();
        }
    }

    /// <summary>Creates a new trace file.</summary>
    /// <param name="directory">The folder traces are kept in.</param>
    /// <param name="traceId">The session identifier used in the file name.</param>
    /// <param name="now">The session start time.</param>
    /// <returns>The writer, or null on an I/O or access failure; invalid path arguments can throw.</returns>
    internal static AutoTdpTraceWriter? Create(string directory, string traceId, DateTimeOffset now)
    {
        var path = System.IO.Path.Combine(
            directory,
            $"autotdp-{now.ToLocalTime():yyyyMMdd-HHmmss}-{traceId}.csv");
        try
        {
            Directory.CreateDirectory(directory);
            StreamWriter writer = new(
                new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false),
                64 * 1024);
            writer.Write(AutoTdpTraceCsv.Header);
            writer.Write("\r\n");
            writer.Flush();
            return new AutoTdpTraceWriter(writer, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"AutoTDP trace could not be created at {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Appends one row.</summary>
    /// <param name="line">The formatted row.</param>
    internal void Write(string line)
    {
        lock (_gate)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.Write(line);
                _writer.Write("\r\n");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Fail(ex);
            }
        }
    }

    /// <summary>Flushes the managed writer and underlying stream; does not request a durable disk flush.</summary>
    internal void Flush()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Fail(ex);
            }
        }
    }

    private void Fail(Exception ex)
    {
        Log.Warn($"AutoTDP trace stopped writing {Path}: {ex.Message}");
        Close();
    }

    private void Close()
    {
        var writer = _writer;
        _writer = null;
        try
        {
            writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Log.Warn($"AutoTDP trace did not close cleanly: {ex.Message}");
        }
    }
}
