using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WSGM.Core;
using HapticOutputFrame = LibHandheld.Contracts.HapticOutputFrame;
using HapticCapabilities = LibHandheld.Contracts.HapticCapabilities;
using OutputChannelSupport = LibHandheld.Contracts.OutputChannelSupport;

namespace WSGM.Input;

/// <summary>Carries the virtual target's rumble back to the physical pad.</summary>
/// <remarks>
///     Clamps to device capabilities, paces to the declared output rate and stops bounded pulses.
///     Epoch checks reject queued frames after stop or target replacement. Sink failures are logged;
///     later frames remain eligible. The backend and physical sink are borrowed, not disposed.
/// </remarks>
internal sealed class ControllerOutputRouter : IAsyncDisposable
{
    private readonly IControllerTargetBackend _backend;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Holds a frame back to the plugin's declared rate; idle unless a frame arrived early.</summary>
    private readonly PeriodicTimer _pace;

    private readonly ITimer _pulseStop;

    private readonly Channel<ControllerTargetOutput> _queue = Channel.CreateBounded<ControllerTargetOutput>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly IPhysicalHapticSink _sink;
    private readonly SemaphoreSlim _sinkGate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly Task _worker;
    private RumbleCalibrationConfig _calibration = new();
    private long _dispatchSequence;
    private bool _disposed;

    private int _droppedFrames;
    private long _epoch;
    private long _lastDispatchTimestamp;
    private bool _outputObserved;
    private bool _previewing;
    private DateTimeOffset _pulseDeadline;
    private long _pulseSequence = -1;
    private ControllerTargetHandle? _target;

    /// <summary>Subscribes to backend feedback and starts the serialized physical-output worker.</summary>
    /// <param name="backend">Borrowed virtual target backend; callbacks may arrive on a native thread.</param>
    /// <param name="sink">Borrowed physical output owner.</param>
    /// <param name="timeProvider">Pulse and pacing clock; null uses system time.</param>
    internal ControllerOutputRouter(
        IControllerTargetBackend backend,
        IPhysicalHapticSink sink,
        TimeProvider? timeProvider = null)
    {
        _backend = backend;
        _sink = sink;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pace = new PeriodicTimer(Timeout.InfiniteTimeSpan, _timeProvider);
        _pulseStop = _timeProvider.CreateTimer(_ => _ = StopPulseAsync(), null, Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _backend.OutputReceived += OnOutputReceived;
        _worker = RunAsync();
    }

    /// <summary>Feedback rejected while detached, disposed or previewing, or when enqueueing fails; not a hardware-loss count.</summary>
    internal int DroppedFrames => Volatile.Read(ref _droppedFrames);

    /// <summary>Whether the current owned sink and target support the bounded physical-motor preview.</summary>
    internal bool CanPreview
    {
        get
        {
            lock (_gate)
            {
                var capabilities = _sink.Capabilities;
                return !_disposed && _target is not null && _sink.IsOwned
                       && capabilities.MinimumPulse <= TimeSpan.FromMilliseconds(500)
                       && (capabilities.LowFrequency == OutputChannelSupport.Native
                           || capabilities.HighFrequency == OutputChannelSupport.Native);
            }
        }
    }

    /// <summary>Detaches feedback, cancels timers and joins the output worker; repeated disposal is harmless.</summary>
    /// <returns>Completion of worker cleanup, including an explicit stop for an active calibration preview.</returns>
    /// <remarks>The target owner must stop ordinary output before disposal; borrowed backend and sink remain alive.</remarks>
    public async ValueTask DisposeAsync()
    {
        bool stopPreview;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _backend.OutputReceived -= OnOutputReceived;
            stopPreview = _previewing;
        }

        if (stopPreview)
        {
            await StopAsync("calibration-owner-disposed", CancellationToken.None).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _target = null;
            ResetUnderGate();
        }

        _queue.Writer.TryComplete();
        await _pulseStop.DisposeAsync().ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _pace.Dispose();
        await _worker.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _lifetime.Dispose();
    }

    /// <summary>Replaces the streaming calibration snapshot with a detached, bounded copy.</summary>
    /// <param name="config">Strength/floor percentages and pulse duration; later caller edits do not affect this router.</param>
    internal void ApplyCalibration(RumbleCalibrationConfig config)
    {
        // Detached once per settings change. The streaming path only reads this immutable snapshot.
        Volatile.Write(ref _calibration, new RumbleCalibrationConfig
        {
            StrengthPercent = Math.Clamp(config.StrengthPercent, 0, 100),
            MinimumStrengthPercent = Math.Clamp(config.MinimumStrengthPercent, 0, 100),
            MinimumPulseMilliseconds = Math.Clamp(config.MinimumPulseMilliseconds, 0, 500)
        });
    }

    /// <summary>Applies gain, device channel clamping and the optional bounded-pulse motor floor.</summary>
    /// <param name="frame">Canonical output before calibration.</param>
    /// <param name="capabilities">Physical output limits and supported channels.</param>
    /// <param name="calibration">Normalized calibration snapshot.</param>
    /// <param name="bounded">Whether nonzero channels need the motor-start floor for a bounded pulse.</param>
    /// <returns>A calibrated frame; silent channels remain silent.</returns>
    internal static HapticOutputFrame Calibrate(HapticOutputFrame frame, HapticCapabilities capabilities,
        RumbleCalibrationConfig calibration, bool bounded)
    {
        var gain = calibration.StrengthPercent / 100f;
        frame = frame with
        {
            LowFrequency = frame.LowFrequency * gain,
            HighFrequency = frame.HighFrequency * gain,
            LeftTrigger = frame.LeftTrigger * gain,
            RightTrigger = frame.RightTrigger * gain
        };
        frame = capabilities.Clamp(frame);
        return bounded && !frame.IsSilent
            ? FloorForMotors(frame, Math.Max(capabilities.MinimumStartIntensity,
                calibration.MinimumStrengthPercent / 100f))
            : frame;
    }

    /// <summary>One explicit, bounded test. Game feedback cannot extend or replace the preview.</summary>
    /// <param name="testFloor">
    ///     True to preview the configured/plugin motor floor; false for the calibrated quarter-strength
    ///     sample.
    /// </param>
    /// <param name="cancellationToken">
    ///     Cancels sink acquisition or preview dispatch; cleanup invalidates the preview on
    ///     failure.
    /// </param>
    /// <returns>True when the bounded preview was accepted; false when unavailable or already previewing.</returns>
    internal async Task<bool> PreviewAsync(bool testFloor, CancellationToken cancellationToken)
    {
        await _sinkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long epoch;
            long sequence;
            HapticOutputFrame frame;
            TimeSpan duration;
            lock (_gate)
            {
                if (!CanPreview || _previewing)
                {
                    return false;
                }

                var calibration = Volatile.Read(ref _calibration);
                var capabilities = _sink.Capabilities;
                var intensity = testFloor
                    ? Math.Max(calibration.MinimumStrengthPercent / 100f, capabilities.MinimumStartIntensity)
                    : 0.25f;
                frame = capabilities.Clamp(new HapticOutputFrame
                {
                    Timestamp = _timeProvider.GetUtcNow(), LowFrequency = intensity, HighFrequency = intensity
                });
                if (!testFloor)
                {
                    frame = Calibrate(frame, capabilities, calibration, true);
                }

                duration = TimeSpan.FromMilliseconds(Math.Max(1, calibration.MinimumPulseMilliseconds));
                if (capabilities.MinimumPulse > duration)
                {
                    duration = capabilities.MinimumPulse;
                }

                ResetUnderGate();
                _previewing = true;
                epoch = _epoch;
                sequence = ++_dispatchSequence;
            }

            try
            {
                await _sink.ApplyAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate)
                {
                    ResetUnderGate();
                }

                await _sink.ApplyAsync(HapticOutputFrame.Stop(_timeProvider.GetUtcNow()), CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }

            lock (_gate)
            {
                if (_epoch == epoch && !_disposed)
                {
                    _pulseSequence = sequence;
                    _pulseDeadline = _timeProvider.GetUtcNow() + duration;
                    _pulseStop.Change(duration, Timeout.InfiniteTimeSpan);
                }
            }

            return true;
        }
        finally
        {
            _sinkGate.Release();
        }
    }

    /// <summary>Stops an active calibration preview; no-op when none is running.</summary>
    /// <returns>Completion of the best-effort silent-frame dispatch.</returns>
    internal Task StopPreviewAsync()
    {
        lock (_gate)
        {
            if (!_previewing)
            {
                return Task.CompletedTask;
            }
        }

        return StopAsync("calibration-preview", CancellationToken.None);
    }

    /// <summary>Changes the output route and invalidates pending frames and pulse state.</summary>
    /// <param name="target">Current backend target; the previous route must already have been stopped.</param>
    internal void Attach(ControllerTargetHandle target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _target = target;
            _lastDispatchTimestamp = 0;
            _outputObserved = false;
            ResetUnderGate();
        }
    }

    /// <summary>Stops the motors with an explicit silent frame and drops anything still queued.</summary>
    /// <param name="reason">Why output stopped, for the log.</param>
    /// <param name="cancellationToken">Cancels waiting for the sink.</param>
    /// <returns>Completion of the stop attempt; absent/unowned sinks are skipped and non-cancellation failures are logged.</returns>
    internal async Task StopAsync(string reason, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ResetUnderGate();
            if (_target is null)
            {
                return;
            }
        }

        await _sinkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sink.IsOwned)
            {
                await _sink.ApplyAsync(HapticOutputFrame.Stop(_timeProvider.GetUtcNow()), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Change("managed-controller-output-stop", $"Managed controller output stop failed ({reason}): "
                                                         + ex.Message, LogLevel.Warn);
        }
        finally
        {
            _sinkGate.Release();
        }
    }

    /// <summary>Invalidates one current route without issuing a physical stop; stale generations are ignored.</summary>
    /// <param name="targetGeneration">Generation previously attached; stop output before detaching an owned sink.</param>
    internal void Detach(long targetGeneration)
    {
        lock (_gate)
        {
            if (_target?.Generation != targetGeneration)
            {
                return;
            }

            _target = null;
            ResetUnderGate();
        }
    }

    /// <summary>Maps a bounded event's nonzero channels onto the range the motors can start at.</summary>
    /// <param name="frame">The event frame after channel clamping.</param>
    /// <param name="minimumStartIntensity">The plugin-declared motor floor; zero passes through.</param>
    /// <returns>The frame with each nonzero channel compressed onto floor..1.</returns>
    /// <remarks>Zero channels stay zero so stop events still stop.</remarks>
    internal static HapticOutputFrame FloorForMotors(
        HapticOutputFrame frame,
        float minimumStartIntensity)
    {
        if (minimumStartIntensity <= 0f)
        {
            return frame;
        }

        var floor = Math.Min(1f, minimumStartIntensity);
        return frame with
        {
            LowFrequency = Map(frame.LowFrequency),
            HighFrequency = Map(frame.HighFrequency),
            LeftTrigger = Map(frame.LeftTrigger),
            RightTrigger = Map(frame.RightTrigger)
        };

        float Map(float value)
        {
            return value <= 0f ? 0f : floor + (1f - floor) * Math.Min(1f, value);
        }
    }

    private void OnOutputReceived(object? sender, ControllerTargetOutput output)
    {
        lock (_gate)
        {
            if (_disposed || _previewing || _target is null || !_queue.Writer.TryWrite(output))
            {
                Interlocked.Increment(ref _droppedFrames);
            }
        }
    }

    private async Task RunAsync()
    {
        // Read without a token: the loop ends when the channel completes, and a cancellable read
        // allocates on every frame.
        await foreach (var output in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            ControllerTargetHandle? target;
            long epoch;
            lock (_gate)
            {
                target = _target;
                epoch = _epoch;
            }

            if (target is null || output.SourceKind != target.Kind || !_sink.IsOwned || !Valid(output))
            {
                Interlocked.Increment(ref _droppedFrames);
                continue;
            }

            var capabilities = _sink.Capabilities;
            var calibration = Volatile.Read(ref _calibration);
            var frame = Calibrate(output.Frame, capabilities, calibration, output.StopAfter is not null);
            var stopAfter = output.StopAfter;
            if (stopAfter is not null && !frame.IsSilent)
            {
                // Bounded haptic events carry protocol intent (an LRA-grade click can be one
                // millisecond at one percent); the plugin's declared motor physics decide how
                // that renders. Continuous rumble envelopes pass through untouched: flooring
                // them would make every quiet scene buzz.
                if (capabilities.MinimumPulse > stopAfter)
                {
                    stopAfter = capabilities.MinimumPulse;
                }

                var perceivedMinimum = TimeSpan.FromMilliseconds(calibration.MinimumPulseMilliseconds);
                if (perceivedMinimum > stopAfter)
                {
                    stopAfter = perceivedMinimum;
                }
            }

            try
            {
                // Paced to what the plugin can write, but a stop is never held back. The one timer is
                // armed for the remaining interval and disarmed once it fired, so a paced frame
                // allocates nothing and the timer stays quiet while frames arrive slowly enough.
                if (!frame.IsSilent && _lastDispatchTimestamp != 0)
                {
                    var minimumInterval =
                        TimeSpan.FromSeconds(1d / Math.Clamp(capabilities.MaxFramesPerSecond, 1, 1000));
                    var elapsed = _timeProvider.GetElapsedTime(_lastDispatchTimestamp);
                    if (elapsed < minimumInterval)
                    {
                        // Whole milliseconds, the timer's resolution; rounding up never sends early.
                        _pace.Period = TimeSpan.FromMilliseconds(
                            Math.Ceiling((minimumInterval - elapsed).TotalMilliseconds));
                        var ticked = await _pace.WaitForNextTickAsync().ConfigureAwait(false);
                        if (!ticked)
                        {
                            return;
                        }

                        _pace.Period = Timeout.InfiniteTimeSpan;
                    }
                }

                await _sinkGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    long sequence;
                    lock (_gate)
                    {
                        // A stopped or replaced target must never receive an older queued frame.
                        if (_epoch != epoch)
                        {
                            Interlocked.Increment(ref _droppedFrames);
                            continue;
                        }

                        sequence = ++_dispatchSequence;
                        _pulseStop.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    }

                    await _sink.ApplyAsync(frame, _lifetime.Token).ConfigureAwait(false);
                    _lastDispatchTimestamp = _timeProvider.GetTimestamp();
                    bool first;
                    lock (_gate)
                    {
                        if (stopAfter is { } pulse && _dispatchSequence == sequence && _epoch == epoch)
                        {
                            _pulseSequence = sequence;
                            _pulseDeadline = _timeProvider.GetUtcNow() + pulse;
                            _pulseStop.Change(pulse, Timeout.InfiniteTimeSpan);
                        }

                        first = !_outputObserved;
                        _outputObserved = true;
                    }

                    if (first)
                    {
                        Log.Info($"Managed controller output active: target={target.Kind}, "
                                 + $"generation={target.Generation}, timed={stopAfter is not null}.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    Log.Change("managed-controller-output-fault",
                        $"Managed controller output write failed; the next frame is tried: {ex.Message}",
                        LogLevel.Warn);
                }
                finally
                {
                    _sinkGate.Release();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                                       && _lifetime.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Ends a bounded pulse, unless another frame replaced it in the meantime.</summary>
    private async Task StopPulseAsync()
    {
        try
        {
            await _sinkGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (_disposed || _target is null || _pulseSequence != _dispatchSequence)
                    {
                        return;
                    }

                    // A callback queued for an older pulse must not shorten the current one.
                    var remaining = _pulseDeadline - _timeProvider.GetUtcNow();
                    if (remaining > TimeSpan.Zero)
                    {
                        _pulseStop.Change(remaining, Timeout.InfiniteTimeSpan);
                        return;
                    }

                    _previewing = false;
                    _pulseSequence = -1;
                }

                await _sink.ApplyAsync(HapticOutputFrame.Stop(_timeProvider.GetUtcNow()), _lifetime.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sinkGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Change("managed-controller-output-fault",
                $"Managed controller pulse stop failed: {ex.Message}", LogLevel.Warn);
        }
    }

    private static bool Valid(ControllerTargetOutput output)
    {
        var frame = output.Frame;
        return ManagedControllerSampleValidator.FiniteUnit(frame.LowFrequency)
               && ManagedControllerSampleValidator.FiniteUnit(frame.HighFrequency)
               && ManagedControllerSampleValidator.FiniteUnit(frame.LeftTrigger)
               && ManagedControllerSampleValidator.FiniteUnit(frame.RightTrigger)
               && (output.StopAfter is null || output.StopAfter > TimeSpan.Zero);
    }

    /// <summary>Starts a new epoch: queued frames are dropped and a pending pulse end is cancelled.</summary>
    private void ResetUnderGate()
    {
        _epoch++;
        _previewing = false;
        _pulseSequence = -1;
        if (!_disposed)
        {
            _pulseStop.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        while (_queue.Reader.TryRead(out _))
        {
            Interlocked.Increment(ref _droppedFrames);
        }
    }
}
