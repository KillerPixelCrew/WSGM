using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WSGM.Core;

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
