using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Truthful state of AutoTDP for the overlay, native QAM, and diagnostics.</summary>
internal enum AutoTdpState
{
    /// <summary>The user has not enabled AutoTDP.</summary>
    Off,

    /// <summary>Enabled, but a prerequisite is missing.</summary>
    Unavailable,

    /// <summary>Enabled and waiting for a foreground application to render.</summary>
    Idle,

    /// <summary>Actively controlling the power limit.</summary>
    Controlling,

    /// <summary>Suspended because the power limit was changed by hand.</summary>
    Paused
}

/// <summary>The complete AutoTDP projection.</summary>
internal sealed record AutoTdpStatus(
    AutoTdpState State,
    int? Watts,
    double? FrametimeMs,
    double? TargetFrametimeMs,
    string? ApplicationId,
    string Detail,
    AutoTdpAction? Action = null);

/// <summary>Shared admission state for AutoTDP commands and every UI projection.</summary>
internal sealed record AutoTdpAvailability(bool Available, string Detail, double? TargetFrametimeMs);

/// <summary>
///     The one AutoTDP session service.
/// </summary>
/// <remarks>
///     A thin binding around <see cref="AutoTdpController" />: it decides nothing itself, so the whole
///     control policy stays replayable from a recorded trace without a device. What lives here is the
///     plumbing the controller must not know about — which application is in front, which capability is
///     the primary power limit, and the rule that only one power write may be in flight.
///     <para>
///         Every prerequisite is optional and checked each tick. No RTSS, no plugin, no power capability, or
///         no rendering application simply means AutoTDP holds; none of them is an error, and none of them
///         may take a frame limit or a manual power setting away from the user.
///     </para>
/// </remarks>
internal sealed class AutoTdpService : IAsyncDisposable
{
    /// <summary>How often frame delivery is judged.</summary>
    /// <remarks>
    ///     One second per window. Shorter windows judge a power change before the SoC has finished
    ///     responding to the previous one; longer ones let a stutter run for too long before power rises.
    /// </remarks>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly Func<IReadOnlyList<DeviceCapabilityView>> _capabilities;
    private readonly AutoTdpController _controller = new();

    private readonly IFrametimeSource _frametimes;
    private readonly Lock _gate = new();
    private readonly Func<RtssOsdMetrics>? _metrics;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private readonly Func<double> _targetFrametimeMs;
    private readonly AutoTdpTraceRecorder? _trace;
    private readonly SemaphoreSlim _write = new(1, 1);

    private readonly Func<DeviceCapabilityView, CapabilityValue, bool, CancellationToken, Task<CapabilityCommandResult>>
        _writeAsync;

    private CancellationTokenSource _applicationWrites = new();
    private bool _controllerStarted;
    private bool _disposed;
    private bool _enabled;
    private CancellationTokenSource? _generation;
    private Task<bool> _lastStop = Task.FromResult(true);
    private bool _powerMayDiffer;

    // What the last prerequisite refresh reported. The refresh runs on every tick and every
    // performance poll, and its subscribers rebuild Steam and OSD state, so it raises only when
    // availability or the enabled flag moved.
    private AutoTdpAvailability? _reportedAvailability;
    private bool _reportedEnabled;
    private DeviceCapabilityKey? _restoreCapability;
    private long? _restoreCycle;
    private DeviceCapabilityView? _restorePair;
    private int? _restoreTo;
    private bool _resync;
    private RunningApplicationTargetSnapshot? _running;

    private Task _worker = Task.CompletedTask;

    internal AutoTdpService(
        IFrametimeSource frametimes,
        Func<IReadOnlyList<DeviceCapabilityView>> capabilities,
        Func<DeviceCapabilityView, CapabilityValue, bool, CancellationToken, Task<CapabilityCommandResult>> writeAsync,
        Func<double> targetFrametimeMs,
        AutoTdpTraceRecorder? trace = null,
        Func<RtssOsdMetrics>? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(frametimes);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(writeAsync);
        ArgumentNullException.ThrowIfNull(targetFrametimeMs);
        _frametimes = frametimes;
        _capabilities = capabilities;
        _writeAsync = writeAsync;
        _targetFrametimeMs = targetFrametimeMs;
        _trace = trace;
        _metrics = metrics;
    }

    /// <summary>Current projection.</summary>
    internal AutoTdpStatus Status { get; private set; } = new(
        AutoTdpState.Off,
        null,
        null,
        null,
        null,
        "AutoTDP is off.");

    /// <summary>Whether automatic control is currently enabled for this session.</summary>
    internal bool Enabled => Volatile.Read(ref _enabled);

    internal bool OwnsPower
    {
        get
        {
            lock (_gate)
            {
                return _enabled && !_controller.IsPaused;
            }
        }
    }

    internal AutoTdpAvailability Availability
    {
        get
        {
            var target = _targetFrametimeMs();
            if (!double.IsFinite(target) || target <= 0)
            {
                return new AutoTdpAvailability(false, "Requires frame-rate limit.", null);
            }

            var power = FindPowerCapability();
            lock (_gate)
            {
                if (_restoreTo is not null && power is not null
                                           && (_restoreCycle != power.Projection.State.CycleGeneration
                                               || _restoreCapability !=
                                               new DeviceCapabilityKey(power.Descriptor.CapabilityId,
                                                   power.Descriptor.InstanceId)))
                {
                    return new AutoTdpAvailability(false,
                        "The previous power owner must be restored before control can resume.", target);
                }
            }

            if (power?.Descriptor.PairedPowerLimitId is not null
                && !IsObserved(FindPairedPower(power)))
            {
                return new AutoTdpAvailability(false, "The paired power limit is unavailable.", target);
            }

            return IsObserved(power)
                ? new AutoTdpAvailability(true, string.Empty, target)
                : new AutoTdpAvailability(false, "No primary power limit is available.", target);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task worker;
        Task<bool> lastStop;
        CancellationTokenSource? generation;
        CancellationTokenSource applicationWrites;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _enabled = false;
            worker = _worker;
            lastStop = _lastStop;
            generation = _generation;
            _worker = Task.CompletedTask;
            _generation = null;
            applicationWrites = _applicationWrites;
        }

        await applicationWrites.CancelAsync().ConfigureAwait(false);

        // A disable may already be restoring the previous value. Let that finish before stopping a
        // newer generation or disposing the shared write gate; otherwise its late write would race
        // a disposed semaphore and could leave AutoTDP's value latched during shutdown.
        _ = await lastStop.ConfigureAwait(false);

        // The tick loop ends first and the restore follows, while the write path still works:
        // exiting with WSGM's probe value latched would leave the user's handheld on a limit they
        // never chose, and a surviving tick could re-latch it after the restore.
        _ = await StopGenerationAsync(worker, generation).ConfigureAwait(false);
        await _shutdown.CancelAsync().ConfigureAwait(false);

        int? unrestored;
        lock (_gate)
        {
            unrestored = _restoreTo;
        }

        (_frametimes as IDisposable)?.Dispose();
        if (_trace is not null)
        {
            await _trace.DisposeAsync().ConfigureAwait(false);
        }

        applicationWrites.Dispose();
        _write.Dispose();
        _shutdown.Dispose();
        if (unrestored is { } watts)
        {
            throw new InvalidOperationException(
                $"AutoTDP could not verify restoration of the previous {watts} W power limit.");
        }
    }

    /// <summary>Raised when the projection changes.</summary>
    internal event Action<AutoTdpStatus>? StatusChanged;

    /// <summary>Switches the AutoTDP CSV trace on or off.</summary>
    /// <param name="enabled">Whether control generations are traced.</param>
    /// <remarks>Recording only. Control makes the same decisions with the trace on or off.</remarks>
    internal void SetTraceEnabled(bool enabled)
    {
        _trace?.SetEnabled(enabled);
    }

    /// <summary>Releases control immediately when the limiter disappears.</summary>
    /// <returns>Whether an active session was forced off.</returns>
    internal bool RefreshPrerequisites()
    {
        var availability = Availability;
        var disabled = Enabled && availability.TargetFrametimeMs is null;
        if (disabled)
        {
            Apply(false);
        }

        var enabled = Enabled;
        bool changed;
        lock (_gate)
        {
            changed = _reportedAvailability != availability || _reportedEnabled != enabled;
            _reportedAvailability = availability;
            _reportedEnabled = enabled;
        }

        if (changed)
        {
            StatusChanged?.Invoke(Status);
        }

        return disabled;
    }

    internal static double TargetFrametime(PerformanceState? state)
    {
        return state is { FrameLimitQuality: PerformanceReadbackQuality.Verified, Observed.FrameLimit: > 0 }
               && state.Desired.FrameLimit != 0
            ? 1000d / state.Observed.FrameLimit.Value
            : 0;
    }

    /// <summary>Enables or disables automatic control.</summary>
    /// <param name="enabled">Whether AutoTDP should run.</param>
    /// <remarks>
    ///     One tick loop exists at a time, and disabling ends the current one before the previous limit
    ///     is restored. Leaving the loop alive across a disable let the next enable start a second one
    ///     against the same flag: every off/on cycle then multiplied the policy rate and its hardware
    ///     writes, and a tick already inside <see cref="TickAsync" /> could write AutoTDP's own value
    ///     after the restore and leave it latched while the feature was off.
    /// </remarks>
    internal void Apply(bool enabled)
    {
        if (enabled && Availability.TargetFrametimeMs is null)
        {
            enabled = false;
        }

        Task<bool> stop;
        CancellationTokenSource applicationWrites;
        lock (_gate)
        {
            if (_disposed || _enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            if (enabled)
            {
                _resync = false;
                _controllerStarted = false;
                // The token is taken from a local, not from the field: a later disable clears the
                // field, and a worker that read it there would dereference null on the thread pool
                // instead of running.
                var started =
                    CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _generation = started;
                var priorStop = _lastStop;
                _worker = Task.Run(async () =>
                {
                    await priorStop.ConfigureAwait(false);
                    started.Token.ThrowIfCancellationRequested();
                    // Recorded here rather than by the caller: the previous generation's restore
                    // has finished, so its rows close the previous trace file before this one opens.
                    TraceEnabled();
                    await RunAsync(started.Token).ConfigureAwait(false);
                }, started.Token);
                return;
            }

            var worker = _worker;
            var generation = _generation;
            _worker = Task.CompletedTask;
            _generation = null;
            applicationWrites = _applicationWrites;
            _applicationWrites = new CancellationTokenSource();
            stop = StopGenerationAsync(worker, generation);
            _lastStop = stop;
        }

        applicationWrites.Cancel();
        applicationWrites.Dispose();
        Log.Observe(stop, "AutoTDP stop");
    }

    private void TraceEnabled()
    {
        var row = _trace?.Begin(AutoTdpTraceEvent.Enabled);
        if (row is null)
        {
            return;
        }

        lock (_gate)
        {
            row.ApplicationId = _running?.ApplicationId;
            row.Executable = _running?.ExecutablePath;
            row.RunningGeneration = _running?.Generation;
            row.Controller = _controller.Snapshot();
        }

        row.Detail = "AutoTDP enabled.";
        _trace!.Commit(row);
    }

    /// <summary>Records the running application whose frames are being judged.</summary>
    /// <param name="snapshot">The canonical running-application snapshot.</param>
    internal void ApplyRunningApplication(RunningApplicationTargetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool enabled;
        int? watts;
        CancellationTokenSource? previousApplicationWrites = null;
        lock (_gate)
        {
            if (_disposed || _running?.Generation > snapshot.Generation)
            {
                return;
            }

            if (_running?.Generation != snapshot.Generation)
            {
                previousApplicationWrites = _applicationWrites;
                _applicationWrites = new CancellationTokenSource();
            }

            _running = snapshot;
            enabled = _enabled;
            watts = Status.Watts;
        }

        previousApplicationWrites?.Cancel();
        previousApplicationWrites?.Dispose();
        if (enabled && previousApplicationWrites is not null)
        {
            var row = _trace?.Begin(AutoTdpTraceEvent.Application);
            if (row is not null)
            {
                row.ApplicationId = snapshot.ApplicationId;
                row.Executable = snapshot.ExecutablePath;
                row.RunningGeneration = snapshot.Generation;
                row.Detail = "context-changed";
            }

            _trace?.Commit(row);
        }

        if (enabled)
        {
            // Retire the previous application's visible status immediately. A tick may still be
            // awaiting its device write, but its generation guard below prevents that older result
            // from replacing this one when the write completes.
            Publish(
                AutoTdpState.Idle,
                watts,
                null,
                null,
                snapshot.ApplicationId,
                "context-changed",
                expectedRunningGeneration: snapshot.Generation);
        }
    }

    /// <summary>Suspends control because the power limit was set by hand.</summary>
    /// <param name="watts">The limit that was just set.</param>
    /// <remarks>
    ///     Called by whoever writes the power capability from a user action. Control does not resume by
    ///     itself — only switching AutoTDP off and on does: the user has overridden the controller, and
    ///     quietly taking the limit back would make the manual control look broken.
    /// </remarks>
    internal void NoteManualChange(int watts)
    {
        CancellationTokenSource previousWrites;
        var row = _trace?.Begin(AutoTdpTraceEvent.ManualPause);
        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            _controller.PauseForManualChange(watts);
            if (row is not null)
            {
                row.Controller = _controller.Snapshot();
                row.ApplicationId = _running?.ApplicationId;
                row.RunningGeneration = _running?.Generation;
                row.Detail = $"Manual power change to {watts} W.";
            }

            previousWrites = _applicationWrites;
            _applicationWrites = new CancellationTokenSource();
            if (_restoreTo is not null)
            {
                _restoreTo = watts;
                if (FindPowerCapability() is { } primary)
                {
                    _restorePair = FindPairedPower(primary);
                }
            }
        }

        previousWrites.Cancel();
        previousWrites.Dispose();
        _trace?.Commit(row);
        Publish(AutoTdpState.Paused, watts, null, null, "Paused by a manual power change.");
    }

    /// <summary>Resumes automatic control that a per-application limit had paused.</summary>
    /// <remarks>
    ///     Called when the application whose own limit paused control is no longer running and no limit
    ///     is preferred for what replaced it. The next window re-bases on whatever the device reports —
    ///     the same recovery path an unapplied write uses — so control continues from the real limit
    ///     rather than from a stale believed one. A no-op while AutoTDP is off: there is no control to
    ///     resume, and the next enable starts a fresh generation anyway.
    /// </remarks>
    internal void ResumeAutomaticControl()
    {
        var row = _trace?.Begin(AutoTdpTraceEvent.Resume);
        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            _controller.ResumeAutomaticControl();
            if (row is not null)
            {
                row.Controller = _controller.Snapshot();
                row.ApplicationId = _running?.ApplicationId;
                row.RunningGeneration = _running?.Generation;
                row.Detail = "Automatic control resumed.";
            }

            // Force the next tick through Start(current, …) so control re-bases on the limit the
            // device actually holds now, not the value it believed before the application's override.
            _controllerStarted = false;
        }

        _trace?.Commit(row);
        Publish(AutoTdpState.Idle, null, null, null, "Automatic control resumed.");
    }

    /// <summary>Ends one enable generation and restores the limit it took over from.</summary>
    /// <param name="worker">The tick loop that generation started.</param>
    /// <param name="generation">Its cancellation source, or null when none was running.</param>
    private async Task<bool> StopGenerationAsync(Task worker, CancellationTokenSource? generation)
    {
        if (generation is not null)
        {
            await generation.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // The restore below still has to run: a worker that died is exactly when the hardware
            // is most likely to be sitting on AutoTDP's last value.
            Log.Error("The AutoTDP tick loop ended with a failure", ex);
        }

        generation?.Dispose();
        var row = _trace?.Begin(AutoTdpTraceEvent.Disabled);
        var restored = await StopAsync(CancellationToken.None, row).ConfigureAwait(false);
        if (row is not null)
        {
            row.Status = AutoTdpTraceStatus(Status.State);
            row.Detail = Status.Detail;
        }

        _trace?.Commit(row);
        _trace?.EndGeneration();
        return restored;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(Window);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("AutoTDP stopped after an unexpected failure", ex);
            Publish(AutoTdpState.Unavailable, null, null, null, ex.Message);
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        if (RefreshPrerequisites())
        {
            return;
        }

        if (!Volatile.Read(ref _enabled))
        {
            return;
        }

        RunningApplicationTargetSnapshot? running;
        long runningGeneration;
        CancellationTokenSource writeCancellation;
        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            running = _running;
            runningGeneration = running?.Generation ?? -1;
            writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _applicationWrites.Token);
        }

        // One clock for the tick, shared by the controller and the trace, so a replayed file feeds
        // the controller exactly the elapsed time the live one saw.
        var elapsedMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        var row = _trace?.Begin(AutoTdpTraceEvent.Tick, elapsedMs);
        using (writeCancellation)
        {
            try
            {
                await TickForApplicationAsync(
                    running,
                    runningGeneration,
                    writeCancellation,
                    cancellationToken,
                    elapsedMs,
                    row).ConfigureAwait(false);
            }
            finally
            {
                if (row is not null)
                {
                    var status = Status;
                    row.Status = AutoTdpTraceStatus(status.State);
                    row.Detail = status.Detail;
                    _trace!.Commit(row);
                }
            }
        }
    }

    /// <summary>Runs one tick against a captured application and its cancellation lifetime.</summary>
    /// <param name="running">The application snapshot captured for this tick.</param>
    /// <param name="runningGeneration">The snapshot generation that may publish its result.</param>
    /// <param name="writeCancellation">Cancels the write when this application is retired.</param>
    /// <param name="cancellationToken">Cancels the AutoTDP worker or caller.</param>
    /// <param name="elapsedMs">This tick's reading of the control clock.</param>
    /// <param name="trace">The trace row this tick fills, or null when the trace is off.</param>
    private async Task TickForApplicationAsync(
        RunningApplicationTargetSnapshot? running,
        long runningGeneration,
        CancellationTokenSource writeCancellation,
        CancellationToken cancellationToken,
        double elapsedMs,
        AutoTdpTraceRow? trace)
    {
        if (trace is not null)
        {
            trace.ApplicationId = running?.ApplicationId;
            trace.Executable = running?.ExecutablePath;
            trace.RunningGeneration = runningGeneration;
        }

        if (FindPowerCapability() is not { } power || !Availability.Available)
        {
            Publish(
                AutoTdpState.Unavailable,
                null,
                null,
                null,
                running?.ApplicationId,
                Availability.Detail,
                expectedRunningGeneration: runningGeneration);
            return;
        }

        AutoTdpLimits limits = new(
            power.Descriptor.Minimum ?? 0,
            power.Descriptor.Maximum ?? 0,
            power.Descriptor.Step ?? 0);
        if (trace is not null)
        {
            TracePower(trace, power, limits);
        }

        // Firmware without readback starts control from the ceiling until the first write lands.
        var current = CurrentWatts(power) ?? limits.Maximum;
        if (!limits.IsUsable)
        {
            Publish(
                AutoTdpState.Unavailable,
                null,
                null,
                null,
                running?.ApplicationId,
                "The power limit reports no usable range.",
                expectedRunningGeneration: runningGeneration);
            return;
        }

        var frametime = SelectSample(running, trace);
        var target = _targetFrametimeMs();
        if (trace is not null)
        {
            trace.TargetFrametimeMs = target;
        }

        if (!double.IsFinite(target) || target <= 0)
        {
            Apply(false);
            return;
        }

        // Read once, before the decision, and hand the same sample to the trace. The controller
        // consumes GPU load, so a value read after the write would describe the wrong window.
        var metrics = ReadMetrics();
        if (trace is not null)
        {
            trace.Metrics = metrics;
        }

        if (frametime is null && !_controllerStarted)
        {
            // Nothing has rendered yet, so there is no context to control and nothing to preserve.
            Publish(
                AutoTdpState.Idle,
                current,
                null,
                null,
                running?.ApplicationId,
                "No application is rendering.",
                expectedRunningGeneration: runningGeneration);
            return;
        }

        var context = ContextKey(running, frametime, target);
        AutoTdpDecision decision;
        bool rebased;
        bool started;
        lock (_gate)
        {
            if (!_enabled || (_running?.Generation ?? -1) != runningGeneration)
            {
                return;
            }

            rebased = _resync && _controllerStarted;
            started = (!_controllerStarted || _resync) && context is not null;
            if (started)
            {
                // Either the first window of this generation, or the window after a write that
                // never reached hardware. Re-basing on the value just observed is the only honest
                // way back from the second: continuing would judge frames against a limit the
                // device never took. The captured restore value is deliberately not moved — the
                // user's own limit is still what a stop has to return to.
                _controllerStarted = true;
                _resync = false;
                _controller.Start(current, limits, context!);
            }

            var previousWatts = _controller.Watts;
            decision = _controller.Observe(
                new AutoTdpObservation(
                    elapsedMs,
                    context,
                    target,
                    ToWindow(frametime),
                    metrics.GpuLoadPercent,
                    metrics.CpuLoadPercent),
                limits);
            if (trace is not null)
            {
                trace.ContextKey = context;
                trace.ControllerStarted = started;
                trace.StartWatts = started ? current : null;
                trace.Rebased = rebased;
                trace.PreviousWatts = previousWatts;
                trace.Decision = decision;
                trace.Controller = _controller.Snapshot();
            }
        }

        if (rebased && started)
        {
            Log.Info($"AutoTDP re-based on the observed limit of {current} W after an unapplied write.");
        }

        if (decision.RequiresWrite)
        {
            bool applied;
            try
            {
                applied = await WriteAsync(
                    power,
                    decision,
                    writeCancellation.Token,
                    runningGeneration,
                    current,
                    trace).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                writeCancellation.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                if (trace is not null)
                {
                    trace.WriteNote = "cancelled";
                }

                MarkWriteUnapplied();
                return;
            }
            finally
            {
                if (trace is not null)
                {
                    TracePostWrite(trace);
                }
            }

            if (!applied)
            {
                Publish(
                    AutoTdpState.Unavailable,
                    current,
                    frametime?.MeanFrametimeMs,
                    target,
                    running?.ApplicationId,
                    "The power limit did not accept the last write; control holds for one window.",
                    expectedRunningGeneration: runningGeneration);
                return;
            }
        }

        Publish(
            _controller.IsPaused ? AutoTdpState.Paused : AutoTdpState.Controlling,
            decision.Watts,
            frametime?.MeanFrametimeMs,
            target,
            running?.ApplicationId,
            decision.Reason,
            decision.Action,
            runningGeneration);
    }

    /// <summary>Writes one power limit and reports whether it reached the hardware.</summary>
    /// <param name="power">The primary power-limit capability.</param>
    /// <param name="decision">The decision being applied.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <param name="expectedRunningGeneration">The application generation allowed to dispatch.</param>
    /// <param name="restoreFrom">The user's prior limit to capture when the write is admitted.</param>
    /// <param name="trace">The trace row that records the write, or null.</param>
    /// <returns><see langword="true" /> when the device accepted the value.</returns>
    /// <remarks>
    ///     The outcome is acted on, not merely logged. <see cref="AutoTdpController" /> has already moved
    ///     its believed wattage by the time this runs, so a refused, timed-out or indeterminate write
    ///     leaves every later decision resting on a limit the device may never have taken; the resync
    ///     flag makes the next window re-base on what the hardware actually reports.
    /// </remarks>
    private async Task<bool> WriteAsync(
        DeviceCapabilityView power,
        AutoTdpDecision decision,
        CancellationToken cancellationToken,
        long? expectedRunningGeneration = null,
        int? restoreFrom = null,
        AutoTdpTraceRow? trace = null)
    {
        // One power command at a time. An overlapping write would leave the controller unable to say
        // which value the hardware actually ended up with, and an uncertain hardware write is never
        // retried behind the user's back.
        if (!await _write.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            if (trace is not null)
            {
                trace.WriteNote = "write-in-flight";
            }

            Log.Warn("AutoTDP skipped a power write: an earlier write is still in flight.");
            MarkWriteUnapplied();
            return false;
        }

        try
        {
            Task<CapabilityCommandResult> command;
            lock (_gate)
            {
                if (expectedRunningGeneration is { } expected
                    && (!_enabled || (_running?.Generation ?? -1) != expected))
                {
                    if (trace is not null)
                    {
                        trace.WriteNote = "application-changed";
                    }

                    _resync = true;
                    return false;
                }

                // Application changes take this same gate before retiring their write token. The
                // final generation check and admission into the capability router are therefore
                // one operation: an old application can be cancelled before or after dispatch,
                // but never in the gap between them.
                cancellationToken.ThrowIfCancellationRequested();
                if (restoreFrom is { } watts)
                {
                    if (_restoreTo is null)
                    {
                        _restorePair = FindPairedPower(power);
                        if (power.Descriptor.PairedPowerLimitId is not null && !IsObserved(_restorePair))
                        {
                            if (trace is not null)
                            {
                                trace.WriteNote = "paired-unobserved";
                            }

                            _resync = true;
                            return false;
                        }

                        _restoreCycle = power.Projection.State.CycleGeneration;
                        _restoreCapability =
                            new DeviceCapabilityKey(power.Descriptor.CapabilityId, power.Descriptor.InstanceId);
                    }

                    _restoreTo ??= watts;
                }

                command = _writeAsync(
                    power,
                    new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Integer,
                        IntegerValue = decision.Watts
                    },
                    power.Descriptor.PairedPowerLimitId is not null,
                    cancellationToken);
            }

            var dispatched = Stopwatch.GetTimestamp();
            if (trace is not null)
            {
                trace.WriteDispatched = true;
            }

            var result = await command.ConfigureAwait(false);
            if (trace is not null)
            {
                trace.WriteMs = Stopwatch.GetElapsedTime(dispatched).TotalMilliseconds;
                trace.WriteOutcome = result.Outcome.ToString();
                trace.WriteReadbackWatts = result.ReadbackValue?.IntegerValue;
            }

            var applied = power.Descriptor.PairedPowerLimitId is not null
                ? result.Applied(decision.Watts)
                : result.Outcome.IsApplied();
            lock (_gate)
            {
                if (result.Outcome == CommandOutcome.Rejected && !_powerMayDiffer)
                {
                    // Rejected is the one outcome that proves nothing reached hardware. If this
                    // was the generation's first write, there is consequently nothing to restore.
                    _restoreTo = null;
                    _restorePair = null;
                    _restoreCycle = null;
                    _restoreCapability = null;
                }
                else if (result.Outcome != CommandOutcome.Rejected)
                {
                    _powerMayDiffer = true;
                }
            }

            if (trace is not null)
            {
                trace.WriteApplied = applied;
            }

            Log.Info(
                $"AutoTDP {decision.Action}: {decision.Watts} W ({decision.Reason}), "
                + $"outcome={result.Outcome}, applied={applied}.");
            if (!applied)
            {
                MarkWriteUnapplied();
            }

            return applied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (trace is not null)
            {
                trace.WriteNote = ex.GetType().Name;
            }

            Log.Warn($"AutoTDP power write failed: {ex.Message}");
            lock (_gate)
            {
                // The call may have failed after dispatch; preserve the restore obligation.
                _powerMayDiffer = true;
            }

            MarkWriteUnapplied();
            return false;
        }
        finally
        {
            _write.Release();
        }
    }

    private void MarkWriteUnapplied()
    {
        lock (_gate)
        {
            _resync = true;
        }
    }

    private async Task<bool> StopAsync(CancellationToken cancellationToken, AutoTdpTraceRow? trace = null)
    {
        int? restoreTo;
        var power = FindPowerCapability();
        lock (_gate)
        {
            restoreTo = _restoreTo;
        }

        if (restoreTo is not { } watts || power is null)
        {
            if (restoreTo is not null && power is null)
            {
                // The capability went away before the limit could be handed back — during a device
                // fault, or a shutdown that retired the coordinator first. Nothing can be done about
                // it here, but a handheld left on AutoTDP's last value must not be silent.
                Log.Warn(
                    $"AutoTDP could not restore {restoreTo} W: the primary power limit is no "
                    + "longer available.");
            }

            Publish(AutoTdpState.Off, null, null, null, "AutoTDP is off.");
            return restoreTo is null;
        }

        if (!IsObserved(power) || power.Projection.State.CycleGeneration != _restoreCycle
                               || _restoreCapability != new DeviceCapabilityKey(power.Descriptor.CapabilityId,
                                   power.Descriptor.InstanceId)
                               || (_restorePair is not null && !IsObserved(FindPairedPower(power))))
        {
            Publish(AutoTdpState.Off, null, null, null,
                "AutoTDP is off; restoration requires current power readback in the original device cycle.");
            return false;
        }

        if (trace is not null)
        {
            trace.PreviousWatts = _controller.Watts;
            TracePower(trace, power, null);
        }

        var decision = _controller.Stop(watts);
        if (trace is not null)
        {
            trace.Decision = decision;
        }

        // Reported from the write's own outcome. Saying "restored" for a value that was refused,
        // timed out, or skipped is the one message that makes the handheld's real state
        // undiagnosable from a log.
        var restored = await WriteAsync(power, decision, cancellationToken, trace: trace).ConfigureAwait(false);
        if (restored && _restorePair is { } pair)
        {
            var live = FindPairedPower(power);
            if (!IsObserved(live) || live!.Projection.State.CycleGeneration != _restoreCycle
                                  || live.Descriptor.CapabilityId != pair.Descriptor.CapabilityId
                                  || pair.Projection.State.ObservedValue is null)
            {
                restored = false;
            }
            else
            {
                try
                {
                    var result = await _writeAsync(live,
                        pair.Projection.State.ObservedValue!, false, cancellationToken).ConfigureAwait(false);
                    restored = pair.Projection.State.ObservedValue?.IntegerValue is { } pairWatts
                               && result.Applied(pairWatts);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    restored = false;
                    Log.Warn($"AutoTDP paired-limit restoration failed: {ex.Message}");
                }
            }
        }

        if (trace is not null)
        {
            TracePostWrite(trace);
        }

        lock (_gate)
        {
            switch (restored)
            {
                case true when _restoreTo == watts:
                    _restoreTo = null;
                    _restorePair = null;
                    _restoreCycle = null;
                    _restoreCapability = null;
                    _powerMayDiffer = false;
                    _controllerStarted = false;
                    break;
                case false:
                    _resync = true;
                    break;
            }
        }

        Publish(
            AutoTdpState.Off,
            watts,
            null,
            null,
            restored
                ? "AutoTDP is off; the previous limit was restored."
                : $"AutoTDP is off; restoring {watts} W was not confirmed.");
        return restored;
    }

    private DeviceCapabilityView? FindPowerCapability()
    {
        return _capabilities()
            .FirstOrDefault(view =>
                view.Descriptor is
                {
                    Role: CapabilityRole.PowerSustainedLimit,
                    SupportsWrite: true,
                    ValueKind: CapabilityValueKind.Integer
                });
    }

    private DeviceCapabilityView? FindPairedPower(DeviceCapabilityView primary)
    {
        return primary.Descriptor.PairedPowerLimitId is { } id
            ? _capabilities().FirstOrDefault(view => view.Descriptor.CapabilityId == id &&
                                                     view.Descriptor.InstanceId is null
                                                     && view.Projection.State.CycleGeneration ==
                                                     primary.Projection.State.CycleGeneration
                                                     && view.Projection.State.DescriptorGeneration ==
                                                     primary.Projection.State.DescriptorGeneration)
            : null;
    }

    /// <summary>Whether the limit can be commanded now. Readback is not required.</summary>
    private static bool IsObserved(DeviceCapabilityView? view)
    {
        return view?.Projection is { Progress: not CommandProgress.Pending }
               && DeviceCapabilityRouter.CanCommand(view.Projection.State)
               && (view.Projection.Progress != CommandProgress.Uncertain
                   || view.Projection.State.ObservedAt > view.LastResult?.CompletedAt);
    }

    /// <summary>The limit as last read or written, else the value the profile asks for.</summary>
    private static int? CurrentWatts(DeviceCapabilityView? view)
    {
        return view?.Projection.State.ObservedValue?.IntegerValue ?? view?.Projection.DesiredValue?.IntegerValue;
    }

    private RtssFrametimeSample? SelectSample(RunningApplicationTargetSnapshot? running, AutoTdpTraceRow? trace)
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

    private static string AutoTdpTraceStatus(AutoTdpState state)
    {
        return state switch
        {
            AutoTdpState.Off => "off",
            AutoTdpState.Unavailable => "unavailable",
            AutoTdpState.Idle => "idle",
            AutoTdpState.Controlling => "controlling",
            AutoTdpState.Paused => "paused",
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
    }

    private void TracePower(AutoTdpTraceRow trace, DeviceCapabilityView power, AutoTdpLimits? limits)
    {
        trace.Limits = limits;
        trace.PowerCapability = power.Descriptor.CapabilityId;
        trace.PairedCapability = power.Descriptor.PairedPowerLimitId;
        trace.ObservedWatts = power.Projection.State.ObservedValue?.IntegerValue;
        trace.ObservedQuality = power.Projection.State.Quality.ToString();
        trace.CycleGeneration = power.Projection.State.CycleGeneration;
        trace.PairedObservedWatts = FindPairedPower(power)?.Projection.State.ObservedValue?.IntegerValue;
    }

    private void TracePostWrite(AutoTdpTraceRow trace)
    {
        if (FindPowerCapability() is not { } power)
        {
            return;
        }

        trace.PostObservedWatts = power.Projection.State.ObservedValue?.IntegerValue;
        trace.PostPairedObservedWatts = FindPairedPower(power)?.Projection.State.ObservedValue?.IntegerValue;
    }

    /// <summary>Samples the sensors, never letting a failing provider stop a control decision.</summary>
    /// <returns>What the sensor provider published, or an empty sample.</returns>
    /// <remarks>
    ///     Utilization is advisory: every rule that consults it is skipped when the value is absent, so
    ///     an unavailable provider makes the controller frametime-only rather than wrong.
    /// </remarks>
    private RtssOsdMetrics ReadMetrics()
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

    /// <summary>The RTSS window in the shape the controller judges, or null when nothing rendered.</summary>
    private static AutoTdpWindow? ToWindow(RtssFrametimeSample? sample)
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
    /// <returns>The context key.</returns>
    /// <remarks>
    ///     The deadline is part of the identity. A session that moved a game from a 120 FPS cap to a
    ///     60 FPS one kept one key across both, so evidence gathered for the harder problem went on
    ///     constraining the easier one (Claw, 2026-09-26).
    /// </remarks>
    private static string? ContextKey(
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

    private void Publish(
        AutoTdpState state,
        int? watts,
        double? frametimeMs,
        double? targetFrametimeMs,
        string detail,
        AutoTdpAction? action = null)
    {
        string? applicationId;
        lock (_gate)
        {
            applicationId = Status.ApplicationId;
        }

        Publish(state, watts, frametimeMs, targetFrametimeMs, applicationId, detail, action);
    }

    private void Publish(
        AutoTdpState state,
        int? watts,
        double? frametimeMs,
        double? targetFrametimeMs,
        string? applicationId,
        string detail,
        AutoTdpAction? action = null,
        long? expectedRunningGeneration = null)
    {
        AutoTdpStatus status = new(
            state,
            watts,
            frametimeMs,
            targetFrametimeMs,
            applicationId,
            detail,
            action);
        lock (_gate)
        {
            var stateMatchesEnabled = state is AutoTdpState.Off ? !_enabled : _enabled;
            if (!stateMatchesEnabled
                || (expectedRunningGeneration is { } expected
                    && (_running?.Generation ?? -1) != expected))
            {
                return;
            }

            if (status == Status)
            {
                return;
            }

            Status = status;
        }

        StatusChanged?.Invoke(status);
    }
}
