using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Shell;

/// <summary>
///     The one AutoTDP session service.
/// </summary>
/// <remarks>
///     A thin binding around <see cref="AutoTdpController" />: it decides nothing itself, so the whole
///     control policy stays replayable from a recorded trace without a device. What lives here is the
///     plumbing the controller must not know about — which application is in front, the enable
///     generation and its stop order, and the rule that only one power write may be in flight.
///     <see cref="AutoTdpTelemetry" /> reads what the controller judges, <see cref="AutoTdpPowerCapability" />
///     finds the limits it writes, and <see cref="AutoTdpRestoreObligation" /> keeps the limit it must hand back.
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

    private readonly AutoTdpController _controller = new();
    private readonly Lock _gate = new();
    private readonly AutoTdpPowerCapability _power;
    private readonly AutoTdpRestoreObligation _restore = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private readonly Func<double> _targetFrametimeMs;
    private readonly AutoTdpTelemetry _telemetry;
    private readonly AutoTdpTraceRecorder? _trace;
    private readonly SemaphoreSlim _write = new(1, 1);

    private readonly Func<DeviceCapabilityView, CapabilityValue, bool, CancellationToken, Task<CapabilityCommandResult>>
        _writeAsync;

    private CancellationTokenSource _applicationWrites = new();
    private bool _controllerStarted;
    private Task? _disposeTask;
    private bool _disposed;
    private bool _enabled;
    private CancellationTokenSource? _generation;
    private Task<bool> _lastStop = Task.FromResult(true);

    // What the last prerequisite refresh reported. The refresh runs on every tick and every
    // performance poll, and its subscribers rebuild Steam and OSD state, so it raises only when
    // availability or the enabled flag moved.
    private AutoTdpAvailability? _reportedAvailability;
    private bool _reportedEnabled;
    private bool _resync;
    private RunningApplicationTargetSnapshot? _running;

    private Task _worker = Task.CompletedTask;

    /// <summary>Creates the service; control starts only when <see cref="Apply" /> enables it.</summary>
    /// <param name="frametimes">The RTSS frame-time source; its owner disposes it after <see cref="Completion" />.</param>
    /// <param name="capabilities">The device's current capability views.</param>
    /// <param name="writeAsync">Writes one capability value through the device's power lane.</param>
    /// <param name="targetFrametimeMs">The deadline control judges against.</param>
    /// <param name="trace">The trace recorder, or null for none; its owner disposes it.</param>
    /// <param name="metrics">The shared sensor sample, or null when no provider is published.</param>
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
        _telemetry = new AutoTdpTelemetry(frametimes, metrics);
        _power = new AutoTdpPowerCapability(capabilities);
        _writeAsync = writeAsync;
        _targetFrametimeMs = targetFrametimeMs;
        _trace = trace;
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

    /// <summary>Whether AutoTDP currently owns the power limit: enabled and not paused by a manual change.</summary>
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

    /// <summary>The shared admission state for the current deadline and power limits.</summary>
    internal AutoTdpAvailability Availability => _power.Availability(_targetFrametimeMs());

    /// <summary>The final stop, or a completed task before <see cref="StopAsync(Deadline)" /> was called.</summary>
    /// <remarks>
    ///     Its owner disposes the frame-time source and the trace only once this completed successfully;
    ///     until then a late restore may still use them.
    /// </remarks>
    internal Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _disposeTask ?? Task.CompletedTask;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync(Deadline.Never));
    }

    /// <summary>Ends control for good and restores the limit AutoTDP took over from, within a deadline.</summary>
    /// <param name="deadline">How long the caller waits; a restore still running afterwards is left to finish.</param>
    /// <returns>A task completing when the stop finished or the deadline passed.</returns>
    internal async Task StopAsync(Deadline deadline)
    {
        Task work;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _enabled = false;
                _disposeTask = Task.Run(() => DisposeCoreAsync(deadline));
                Log.Observe(_disposeTask, "AutoTDP shutdown", true);
            }

            work = _disposeTask;
        }

        using var bounded = deadline.CreateCancellationSource();
        try
        {
            await work.WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested)
        {
            int? watts;
            lock (_gate)
            {
                watts = _restore.Watts;
            }

            Log.Warn(
                $"AutoTDP shutdown reached its deadline; restore to {watts?.ToString() ?? "the original"} W remains unconfirmed.");
        }
    }

    private async Task DisposeCoreAsync(Deadline deadline)
    {
        Task worker;
        Task<bool> lastStop;
        CancellationTokenSource? generation;
        CancellationTokenSource applicationWrites;
        lock (_gate)
        {
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
        using var bounded = deadline.CreateCancellationSource();
        _ = await StopGenerationAsync(worker, generation, bounded.Token).ConfigureAwait(false);
        await _shutdown.CancelAsync().ConfigureAwait(false);

        int? unrestored;
        bool alreadyReported;
        lock (_gate)
        {
            unrestored = _restore.Watts;
            alreadyReported = _restore.UnavailableReported;
        }

        // The frame-time source and the trace belong to whoever created them, and are disposed by it
        // once this stop has finished.
        applicationWrites.Dispose();
        _write.Dispose();
        _shutdown.Dispose();
        if (unrestored is { } watts && !alreadyReported)
        {
            Log.Warn($"AutoTDP could not hand the {watts} W power limit back before shutdown.");
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

    /// <summary>Announces a change in availability or in the deadline to every projection.</summary>
    /// <remarks>
    ///     The deadline moves with the profile target and with a frame limit below it, neither of which
    ///     touches the setting or the controller's state, so the rows hear about it here.
    /// </remarks>
    internal void RefreshPrerequisites()
    {
        var availability = Availability;
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
    }

    /// <summary>The deadline AutoTDP judges frame delivery against.</summary>
    /// <param name="state">The RTSS state, or null without RTSS.</param>
    /// <param name="layers">The profile layers in force for the running application.</param>
    /// <returns>
    ///     The frame time of the profile's AutoTDP target, or of an active frame limit below it. An active
    ///     limit is the one RTSS reports while a cap is asked for; a desired cap alone is not holding the
    ///     game to anything.
    /// </returns>
    internal static double TargetFrametime(PerformanceState? state, ProfileLayers layers)
    {
        int? activeLimit = state is { Observed.FrameLimit: > 0 } && state.Desired.FrameLimit != 0
            ? state.Observed.FrameLimit
            : null;
        return 1000d / AutoTdpTarget.EffectiveFps(layers.Value(values => values.AutoTdpTargetFps).Value,
            activeLimit);
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

    /// <summary>Disables automatic control and waits until the limit it took over from was handed back.</summary>
    /// <returns>A task completing once the restore of the last enable generation finished, whatever its outcome.</returns>
    /// <remarks>
    ///     The device owner awaits this before it stops the device, so the restore write still finds a
    ///     writable power limit. Disabling an already disabled service waits for its last stop, which may
    ///     still be running.
    /// </remarks>
    internal Task DisableAsync()
    {
        Apply(false);
        lock (_gate)
        {
            return _lastStop;
        }
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
            StampContext(row);
        }

        row.Detail = "AutoTDP enabled.";
        _trace!.Commit(row);
    }

    // Called under _gate: the application and controller state an event row is judged against.
    private void StampContext(AutoTdpTraceRow? row)
    {
        if (row is null)
        {
            return;
        }

        row.ApplicationId = _running?.ApplicationId;
        row.Executable = _running?.ExecutablePath;
        row.RunningGeneration = _running?.Generation;
        row.Controller = _controller.Snapshot();
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
    /// <param name="watts">The limit that was just set, or null when the user chose a firmware performance profile.</param>
    /// <remarks>
    ///     Called by whoever writes the power capability from a user action. Control does not resume by
    ///     itself — only switching AutoTDP off and on does: the user has overridden the controller, and
    ///     quietly taking the limit back would make the manual control look broken.
    /// </remarks>
    internal void NoteManualChange(int? watts)
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
            StampContext(row);
            row?.Detail = watts is null ? "Firmware performance profile chosen." : $"Manual power change to {watts} W.";

            previousWrites = _applicationWrites;
            _applicationWrites = new CancellationTokenSource();
            _restore.FollowManualChange(watts, _power);
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
            StampContext(row);
            row?.Detail = "Automatic control resumed.";

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
    /// <param name="cancellationToken">Cancels the wait.</param>
    private async Task<bool> StopGenerationAsync(Task worker, CancellationTokenSource? generation,
        CancellationToken cancellationToken = default)
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
        cancellationToken.ThrowIfCancellationRequested();
        var row = _trace?.Begin(AutoTdpTraceEvent.Disabled);
        var restored = await StopAsync(cancellationToken, row).ConfigureAwait(false);
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

    /// <summary>Runs one control tick: reads the window, asks the controller, and applies its decision.</summary>
    /// <param name="cancellationToken">Cancels the AutoTDP worker or caller.</param>
    /// <returns>A task completing when the tick and any write it dispatched finished.</returns>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        RefreshPrerequisites();
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

        if (_power.FindPrimary() is not { } power || !Availability.Available)
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

        var limits = AutoTdpPowerCapability.Limits(power);
        if (trace is not null)
        {
            _power.TraceBefore(trace, power, limits);
        }

        // Firmware without readback starts control from the ceiling until the first write lands.
        var current = AutoTdpPowerCapability.CurrentWatts(power) ?? limits.Maximum;
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

        var frametime = _telemetry.SelectSample(running, trace);
        var target = _targetFrametimeMs();
        trace?.TargetFrametimeMs = target;

        // Read once, before the decision, and hand the same sample to the trace. The controller
        // consumes GPU load, so a value read after the write would describe the wrong window.
        var metrics = _telemetry.ReadMetrics();
        trace?.Metrics = metrics;

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

        var context = AutoTdpTelemetry.ContextKey(running, frametime, target);
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
                    AutoTdpTelemetry.ToWindow(frametime),
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
                trace?.WriteNote = "cancelled";

                MarkWriteUnapplied();
                return;
            }
            finally
            {
                if (trace is not null)
                {
                    _power.TraceAfter(trace);
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
            trace?.WriteNote = "write-in-flight";

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
                    trace?.WriteNote = "application-changed";

                    _resync = true;
                    return false;
                }

                // Application changes take this same gate before retiring their write token. The
                // final generation check and admission into the capability router are therefore
                // one operation: an old application can be cancelled before or after dispatch,
                // but never in the gap between them.
                cancellationToken.ThrowIfCancellationRequested();
                if (restoreFrom is { } watts && !_restore.TryCapture(watts, power, _power))
                {
                    trace?.WriteNote = "paired-unobserved";

                    _resync = true;
                    return false;
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
            trace?.WriteDispatched = true;

            var result = await command.ConfigureAwait(false);
            if (trace is not null)
            {
                trace.WriteMs = Stopwatch.GetElapsedTime(dispatched).TotalMilliseconds;
                trace.WriteOutcome = result.Outcome.ToString();
                trace.WriteReadbackWatts = result.ReadbackValue?.IntegerValue;
            }

            // Dispatched is applied, for a pair as for a single limit; a differing readback stays trace data.
            var applied = result.Outcome.IsApplied();
            lock (_gate)
            {
                _restore.NoteOutcome(result.Outcome);
            }

            trace?.WriteApplied = applied;

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
            trace?.WriteNote = ex.GetType().Name;

            Log.Warn($"AutoTDP power write failed: {ex.Message}");
            lock (_gate)
            {
                // The call may have failed after dispatch; preserve the restore obligation.
                _restore.NoteUncertain();
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
        var power = _power.FindPrimary();
        bool reportUnavailable;
        lock (_gate)
        {
            restoreTo = _restore.BeginRestore(power is not null, out reportUnavailable);
        }

        if (restoreTo is not { } watts || power is null)
        {
            if (reportUnavailable)
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

        if (!AutoTdpPowerCapability.CanWrite(power)
            || (_restore.Pair is not null && !AutoTdpPowerCapability.CanWrite(_power.FindPaired(power))))
        {
            Publish(AutoTdpState.Off, null, null, null,
                "AutoTDP is off; the power limit is unavailable for restoration.");
            return false;
        }

        if (trace is not null)
        {
            trace.PreviousWatts = _controller.Watts;
            _power.TraceBefore(trace, power, null);
        }

        var decision = _controller.Stop(watts);
        trace?.Decision = decision;

        // Reported from the write's own outcome. Saying "restored" for a value that was refused,
        // timed out, or skipped is the one message that makes the handheld's real state
        // undiagnosable from a log.
        var restored = await WriteAsync(power, decision, cancellationToken, trace: trace).ConfigureAwait(false);
        if (restored && _restore.Pair is { } pair)
        {
            var live = _power.FindPaired(power);
            // The pair goes back to what it was last read or written as, else to what the profile asks
            // for. With neither, there is nothing to hand back and the primary limit alone is restored.
            var previous = pair.Projection.State.ObservedValue ?? pair.Projection.DesiredValue;
            if (!AutoTdpPowerCapability.CanWrite(live))
            {
                restored = false;
            }
            else if (previous?.IntegerValue is not null)
            {
                try
                {
                    var result = await _writeAsync(live!, previous, false, cancellationToken).ConfigureAwait(false);
                    restored = result.Outcome.IsApplied();
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
            _power.TraceAfter(trace);
        }

        lock (_gate)
        {
            if (!restored)
            {
                _resync = true;
            }
            else if (_restore.Fulfil(watts))
            {
                _controllerStarted = false;
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
