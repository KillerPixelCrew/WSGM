using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>Carries the virtual target's rumble back to the physical pad.</summary>
/// <remarks>
///     HC hands each vibration straight to the controller, and so does this, with the three things the
///     motors need: clamping and the motor floor the plugin declared, pacing to its frame rate, and the
///     end of a bounded pulse. The motors latch the last value, so the one guard kept is the epoch: a frame
///     that was already on its way when output stopped is dropped rather than landing after the stop. A
///     sink failure is logged and the next frame is tried; it never silences rumble for the session.
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
    private long _dispatchSequence;
    private bool _disposed;

    private int _droppedFrames;
    private long _epoch;
    private long _lastDispatchTimestamp;
    private bool _outputObserved;
    private long _pulseSequence = -1;
    private ControllerTargetHandle? _target;

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

    internal int DroppedFrames => Volatile.Read(ref _droppedFrames);

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _backend.OutputReceived -= OnOutputReceived;
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
    /// <returns>A task completing once the stop frame was handed to the plugin.</returns>
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
            if (_disposed || _target is null || !_queue.Writer.TryWrite(output))
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
            var frame = capabilities.Clamp(output.Frame);
            var stopAfter = output.StopAfter;
            if (stopAfter is not null && !frame.IsSilent)
            {
                // Bounded haptic events carry protocol intent (an LRA-grade click can be one
                // millisecond at one percent); the plugin's declared motor physics decide how
                // that renders. Continuous rumble envelopes pass through untouched: flooring
                // them would make every quiet scene buzz.
                frame = FloorForMotors(frame, capabilities.MinimumStartIntensity);
                if (capabilities.MinimumPulse > stopAfter)
                {
                    stopAfter = capabilities.MinimumPulse;
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
