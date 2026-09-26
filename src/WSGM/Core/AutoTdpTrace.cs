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
///     One AutoTDP trace row. Every value is optional; an empty CSV cell means the value was not
///     available at that point, never zero.
/// </summary>
/// <remarks>
///     The columns describe the current controller only. Classifications the issue asks a later
///     controller to make, such as long-stall quarantine, are not invented here: the raw RTSS window
///     bounds and timings are recorded so they can be derived offline from the same trace.
/// </remarks>
internal sealed class AutoTdpTraceRow
{
    internal AutoTdpTraceEvent Event { get; init; }

    internal long Row { get; set; }

    internal double ElapsedMs { get; set; }

    internal DateTimeOffset WallClock { get; set; }

    internal double? TickIntervalMs { get; set; }

    internal string? TraceId { get; set; }

    internal string? WsgmVersion { get; set; }

    internal string? Package { get; set; }

    internal string? PackageVersion { get; set; }

    internal string? ApplicationId { get; set; }

    internal string? Executable { get; set; }

    internal uint? ProcessId { get; set; }

    internal long? RunningGeneration { get; set; }

    internal string? ContextKey { get; set; }

    internal double? TargetFrametimeMs { get; set; }

    internal bool? AcPower { get; set; }

    internal double? BatteryPercent { get; set; }

    internal string? PowerMode { get; set; }

    internal string? PowerScheme { get; set; }

    internal AutoTdpLimits? Limits { get; set; }

    internal string? PowerCapability { get; set; }

    internal string? PairedCapability { get; set; }

    internal long? ObservedWatts { get; set; }

    internal string? ObservedQuality { get; set; }

    internal long? PairedObservedWatts { get; set; }

    internal long? CycleGeneration { get; set; }

    internal int? Renderers { get; set; }

    internal string? Selection { get; set; }

    internal RtssFrametimeSample? Frametime { get; set; }

    internal bool? WindowRepeat { get; set; }

    internal long? WindowGapMs { get; set; }

    internal bool? Capped { get; set; }

    internal bool? ControllerStarted { get; set; }

    /// <summary>
    ///     The limit the controller took as current when it started or re-based: the observed
    ///     value, else the value last written, else the ceiling on a device without readback.
    /// </summary>
    internal int? StartWatts { get; set; }

    internal bool? Rebased { get; set; }

    internal AutoTdpControllerSnapshot? Controller { get; set; }

    internal int? PriorLearnedFloor { get; set; }

    internal int? PriorFailedProbeFloor { get; set; }

    internal long? ProbeId { get; set; }

    internal AutoTdpDecision? Decision { get; set; }

    internal int? PreviousWatts { get; set; }

    internal double? SinceDecisionMs { get; set; }

    internal double? SinceWriteMs { get; set; }

    internal bool? WriteDispatched { get; set; }

    internal string? WriteOutcome { get; set; }

    internal long? WriteReadbackWatts { get; set; }

    internal bool? WriteApplied { get; set; }

    internal double? WriteMs { get; set; }

    internal string? WriteNote { get; set; }

    internal long? PostObservedWatts { get; set; }

    internal long? PostPairedObservedWatts { get; set; }

    internal RtssOsdMetrics? Metrics { get; set; }

    internal string? Status { get; set; }

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
    internal const int Version = 1;

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
        ("capped", row => Flag(row.Capped)),
        ("controller_started", row => Flag(row.ControllerStarted)),
        ("start_w", row => Integer(row.StartWatts)),
        ("controller_rebased", row => Flag(row.Rebased)),
        ("prior_learned_floor_w", row => Integer(row.PriorLearnedFloor)),
        ("prior_failed_probe_floor_w", row => Integer(row.PriorFailedProbeFloor)),
        ("judged", row => Flag(row.Controller?.Judged)),
        ("ratio", row => row.Controller is { Judged: true } snapshot ? Number(snapshot.Ratio) : null),
        ("window_missed", row => row.Controller is { Judged: true } snapshot ? Flag(snapshot.Missed) : null),
        ("window_comfortable", row => row.Controller is { Judged: true } snapshot
            ? Flag(snapshot.Comfortable)
            : null),
        ("state", row => State(row.Controller)),
        ("believed_w", row => Integer(row.Controller?.Watts)),
        ("last_good_w", row => Integer(row.Controller?.LastGood)),
        ("learned_floor_w", row => Integer(row.Controller?.LearnedFloor)),
        ("failed_probe_floor_w", row => Integer(row.Controller?.FailedProbeFloor)),
        ("miss_count", row => Integer(row.Controller?.MissedWindows)),
        ("comfortable_count", row => Integer(row.Controller?.ComfortableWindows)),
        ("probe_elapsed", row => Integer(row.Controller?.ProbeWindows)),
        ("settling_remaining", row => Integer(row.Controller?.SettlingWindows)),
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

    private static string? State(AutoTdpControllerSnapshot? snapshot)
    {
        return snapshot switch
        {
            null => null,
            { IsPaused: true } => "paused",
            { SettlingWindows: > 0 } => "settling",
            { IsProbing: true } => "probing",
            _ => "tracking"
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
///     once; AutoTDP carries on without it. Rows are buffered and flushed by the tick loop, so a row
///     written from another thread never waits on the disk.
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
    /// <returns>The writer, or null when the file could not be created.</returns>
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

    /// <summary>Pushes buffered rows to disk.</summary>
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
