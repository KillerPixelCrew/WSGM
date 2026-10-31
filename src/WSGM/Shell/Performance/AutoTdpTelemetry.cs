using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>What AutoTDP looks at each tick: the running game's RTSS window, the sensor sample and the context key.</summary>
/// <remarks>
///     Read-only. It decides which renderer belongs to the running application and turns its RTSS
///     sample into the raw window the controller judges, so every judgement stays with the policy and
///     a replayed trace needs only these inputs.
/// </remarks>
internal sealed class AutoTdpTelemetry
{
    private readonly IFrametimeSource _frametimes;
    private readonly Func<RtssOsdMetrics>? _metrics;

    /// <summary>Creates the telemetry over the session's frame-time and sensor sources.</summary>
    /// <param name="frametimes">The RTSS frame-time source; its owner disposes it.</param>
    /// <param name="metrics">The shared sensor sample, or null when no provider is published.</param>
    internal AutoTdpTelemetry(IFrametimeSource frametimes, Func<RtssOsdMetrics>? metrics)
    {
        ArgumentNullException.ThrowIfNull(frametimes);
        _frametimes = frametimes;
        _metrics = metrics;
    }

    /// <summary>The RTSS window in the shape the controller judges, or null when nothing rendered.</summary>
    /// <param name="sample">The selected RTSS sample, or null.</param>
    /// <returns>The window, or null.</returns>
    internal static AutoTdpWindow? ToWindow(RtssFrametimeSample? sample)
    {
        return sample is null
            ? null
            : new AutoTdpWindow(
                sample.WindowStartTicks,
                sample.WindowEndTicks,
                sample.Frames,
                sample.MeanFrametimeMs,
                sample.FrameTimeRaw > 0 ? sample.FrameTimeRaw / 1000d : null,
                sample.AgeMs);
    }

    /// <summary>The application and deadline the controller's evidence belongs to.</summary>
    /// <param name="running">The running-application snapshot, when one identified a game.</param>
    /// <param name="sample">The RTSS renderer, used only when Steam gave no identity.</param>
    /// <param name="targetFrametimeMs">The active deadline.</param>
    /// <returns>The context key, or null to keep the controller's current one.</returns>
    /// <remarks>
    ///     The deadline is part of the identity. A session that moved a game from a 120 FPS cap to a
    ///     60 FPS one kept one key across both, so evidence gathered for the harder problem went on
    ///     constraining the easier one (Claw, 2026-09-26).
    /// </remarks>
    internal static string? ContextKey(
        RunningApplicationTargetSnapshot? running,
        RtssFrametimeSample? sample,
        double targetFrametimeMs)
    {
        string identity;
        if (running?.ApplicationId is { Length: > 0 } application)
        {
            identity = application;
        }
        else if (sample?.ExecutablePath is { Length: > 0 } executable)
        {
            identity = $"process:{Path.GetFileName(executable)}";
        }
        else
        {
            // A stall long enough to retire the RTSS entry leaves an unidentified game running. The
            // controller keeps the context it already has rather than being told the game changed,
            // which would discard the quarantine the stall just opened.
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{identity}|{targetFrametimeMs:F2}ms");
    }

    /// <summary>Selects the running application's RTSS sample and records the choice on the trace.</summary>
    /// <param name="running">The application snapshot captured for this tick.</param>
    /// <param name="trace">The tick's trace row, or null when the trace is off.</param>
    /// <returns>The selected sample, or null when nothing renders or the renderer is ambiguous.</returns>
    internal RtssFrametimeSample? SelectSample(RunningApplicationTargetSnapshot? running, AutoTdpTraceRow? trace)
    {
        var live = _frametimes.ReadLive();
        var selected = SelectSample(running, live, out var selection);
        if (trace is not null)
        {
            trace.Renderers = live.Count;
            trace.Selection = selection;
            trace.Frametime = selected;
            trace.ProcessId = selected?.ProcessId;
        }

        return selected;
    }

    /// <summary>Samples the sensors, never letting a failing provider stop a control decision.</summary>
    /// <returns>What the sensor provider published, or an empty sample.</returns>
    /// <remarks>
    ///     Utilization is advisory: every rule that consults it is skipped when the value is absent, so
    ///     an unavailable provider makes the controller frametime-only rather than wrong.
    /// </remarks>
    internal RtssOsdMetrics ReadMetrics()
    {
        if (_metrics is null)
        {
            return RtssOsdMetrics.Empty;
        }

        try
        {
            return _metrics();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
        {
            Log.Change("autotdp-sensors", $"AutoTDP sensors unavailable: {ex.Message}");
            return RtssOsdMetrics.Empty;
        }
    }

    private static RtssFrametimeSample? SelectSample(
        RunningApplicationTargetSnapshot? running,
        IReadOnlyList<RtssFrametimeSample> live,
        out string selection)
    {
        if (live.Count == 0)
        {
            selection = "none";
            return null;
        }

        // The running-application monitor knows which executable Steam launched; RTSS knows which
        // process is drawing. Matching them is what keeps AutoTDP from tuning power for a launcher
        // or a background renderer that happens to be in the table.
        if (running?.ExecutablePath is { Length: > 0 } executable
            && live.FirstOrDefault(sample => string.Equals(
                Path.GetFileName(sample.ExecutablePath),
                Path.GetFileName(executable),
                StringComparison.OrdinalIgnoreCase)) is { } matched)
        {
            selection = "executable";
            return matched;
        }

        // With exactly one renderer there is nothing to confuse it with. With several and no
        // identity, AutoTDP declines rather than guessing which one the user is playing.
        selection = live.Count == 1 ? "only-renderer" : "ambiguous";
        return live.Count == 1 ? live[0] : null;
    }
}
