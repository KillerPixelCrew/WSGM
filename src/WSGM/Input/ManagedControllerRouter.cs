using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

internal interface IPhysicalHapticSink
{
    bool IsOwned { get; }

    HapticCapabilities Capabilities { get; }

    Task ApplyAsync(HapticOutputFrame frame, CancellationToken cancellationToken);
}

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
    private readonly IHidBackend _backend;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ITimer _pulseStop;

    private readonly Channel<HidTargetOutput> _queue = Channel.CreateBounded<HidTargetOutput>(
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
    private long _epoch;
    private long _lastDispatchTimestamp;
    private bool _outputObserved;
    private long _pulseSequence = -1;
    private HidTargetHandle? _target;

    internal ControllerOutputRouter(
        IHidBackend backend,
        IPhysicalHapticSink sink,
        TimeProvider? timeProvider = null)
    {
        _backend = backend;
        _sink = sink;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pulseStop = _timeProvider.CreateTimer(_ => _ = StopPulseAsync(), null, Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _backend.OutputReceived += OnOutputReceived;
        _worker = RunAsync();
    }

    internal int DroppedFrames { get; private set; }

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
        await _worker.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _lifetime.Dispose();
    }

    internal void Attach(HidTargetHandle target)
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

    private void OnOutputReceived(object? sender, HidTargetOutput output)
    {
        lock (_gate)
        {
            if (_disposed || _target is null || !_queue.Writer.TryWrite(output))
            {
                DroppedFrames++;
            }
        }
    }

    private async Task RunAsync()
    {
        // Read without a token: the loop ends when the channel completes, and a cancellable read
        // allocates on every frame.
        await foreach (var output in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            HidTargetHandle? target;
            long epoch;
            lock (_gate)
            {
                target = _target;
                epoch = _epoch;
            }

            if (target is null || output.SourceKind != target.Kind || !_sink.IsOwned || !Valid(output))
            {
                DroppedFrames++;
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
                // Paced to what the plugin can write, but a stop is never held back.
                if (!frame.IsSilent && _lastDispatchTimestamp != 0)
                {
                    var minimumInterval =
                        TimeSpan.FromSeconds(1d / Math.Clamp(capabilities.MaxFramesPerSecond, 1, 1000));
                    var elapsed = _timeProvider.GetElapsedTime(_lastDispatchTimestamp);
                    if (elapsed < minimumInterval)
                    {
                        await Task.Delay(minimumInterval - elapsed, _timeProvider, _lifetime.Token)
                            .ConfigureAwait(false);
                    }
                }

                await DispatchAsync(frame, stopAfter, target, epoch).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task DispatchAsync(HapticOutputFrame frame, TimeSpan? stopAfter, HidTargetHandle target,
        long epoch)
    {
        await _sinkGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            long sequence;
            lock (_gate)
            {
                // Output stopped or moved to another target while this frame waited: it must not land
                // on top of the stop, since the motors would keep running.
                if (_epoch != epoch)
                {
                    DroppedFrames++;
                    return;
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
                $"Managed controller output write failed; the next frame is tried: {ex.Message}", LogLevel.Warn);
        }
        finally
        {
            _sinkGate.Release();
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

    private static bool Valid(HidTargetOutput output)
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
            DroppedFrames++;
        }
    }
}

internal sealed class ManagedControllerRouter : IAsyncDisposable
{
    private readonly IHidBackend _backend;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private bool _disposed;
    private bool _neutral = true;

    internal ManagedControllerRouter(
        IHidBackend backend,
        IPhysicalHapticSink hapticSink,
        TimeProvider? timeProvider = null)
    {
        _backend = backend;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Output = new ControllerOutputRouter(backend, hapticSink, _timeProvider);
        _backend.TargetLost += OnTargetLost;
    }

    internal ManagedTargetState State { get; private set; } = ManagedTargetState.Absent;

    internal HidTargetHandle? Target { get; private set; }

    internal ControllerOutputRouter Output { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _backend.TargetLost -= OnTargetLost;
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(2));
            try
            {
                await RemoveUnderGateAsync("router-dispose", cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                State = ManagedTargetState.Faulted;
                Log.Error("Managed controller cleanup was not verified", ex);
            }
        }
        finally
        {
            _transition.Release();
        }

        await Output.DisposeAsync().ConfigureAwait(false);
        await _backend.DisposeAsync().ConfigureAwait(false);
        _transition.Dispose();
    }

    /// <summary>Raised when the backend lost the target and this router faulted.</summary>
    /// <remarks>
    ///     The owner needs this to stop reporting controller management as active: the target is gone,
    ///     output has been stopped and the handle detached, so every further sample would be written
    ///     into nothing while WSGM's surfaces waited on a source that had stopped delivering.
    /// </remarks>
    internal event Action<string>? TargetFaulted;

    internal async Task<HidTargetHandle> CreateAsync(
        ManagedControllerTarget kind,
        CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Target is not null)
            {
                throw new InvalidOperationException("A managed target already exists.");
            }

            return await CreateUnderGateAsync(kind, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            State = ManagedTargetState.Faulted;
            if (Target is not null)
            {
                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(2));
                try
                {
                    await RemoveUnderGateAsync("create-failed", cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    Log.Error("Failed managed target creation also failed cleanup", cleanupException);
                }
            }

            State = ManagedTargetState.Faulted;
            throw;
        }
        finally
        {
            _transition.Release();
        }
    }

    internal void ActivateSource()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Target is null || State is not (ManagedTargetState.Neutral or ManagedTargetState.Active))
        {
            throw new InvalidOperationException("A target is required before routing.");
        }

        if (State is ManagedTargetState.Active)
        {
            return;
        }

        Output.Attach(Target);
        // Activation means a source may affect the target. Even before the first accepted sample,
        // an invalid frame must publish an explicit neutral report rather than relying on the
        // creation-time packet still being current.
        _neutral = false;
        State = ManagedTargetState.Active;
    }

    internal async ValueTask<bool> RouteAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        var target = Target;
        if (target is null || State is not ManagedTargetState.Active)
        {
            return false;
        }

        if (!ManagedControllerSampleValidator.TryValidate(
                sample,
                out var refusal))
        {
            // Keyed and without the per-sample numbers, so a burst of refused samples (every sample
            // queued while a target was being created arrives stale) is one line, not hundreds.
            Log.Change(
                "managed-controller-neutralized",
                $"Managed controller input was neutralized: reason={refusal}.",
                LogLevel.Warn);
            await NeutralizeAsync($"source-invalid:{refusal}", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        var delivered = await _backend.PublishAsync(target, sample, cancellationToken)
            .ConfigureAwait(false);
        if (!delivered)
        {
            return false;
        }

        _neutral = ManagedControllerSampleValidator.IsNeutral(sample);
        return true;
    }

    internal Task NeutralizeAsync(string reason, CancellationToken cancellationToken)
    {
        return UnderGateAsync(() => NeutralizeUnderGateAsync(reason, cancellationToken), cancellationToken);
    }

    internal Task RemoveAsync(string reason, CancellationToken cancellationToken)
    {
        return UnderGateAsync(() => RemoveUnderGateAsync(reason, cancellationToken), cancellationToken);
    }

    internal async Task<HidTargetHandle> ReplaceAsync(
        ManagedControllerTarget kind,
        CancellationToken cancellationToken)
    {
        HidTargetHandle? target = null;
        await UnderGateAsync(async () =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await RemoveUnderGateAsync("target-replacement", cancellationToken).ConfigureAwait(false);
            target = await CreateUnderGateAsync(kind, cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return target!;
    }

    /// <summary>Runs one target transition while holding the transition gate.</summary>
    private async Task UnderGateAsync(Func<Task> transition, CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await transition().ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<HidTargetHandle> CreateUnderGateAsync(
        ManagedControllerTarget kind,
        CancellationToken cancellationToken)
    {
        var neutral = NewNeutral();
        var target = await _backend.CreateTargetAsync(kind, neutral, cancellationToken)
            .ConfigureAwait(false);
        Target = target;
        if (!await _backend.WaitForEnumerationAsync(target, cancellationToken).ConfigureAwait(false))
        {
            State = ManagedTargetState.Faulted;
            await RemoveUnderGateAsync("enumeration-failed", cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The virtual target did not enumerate.");
        }

        _neutral = true;
        State = ManagedTargetState.Neutral;
        Output.Attach(target);
        return target;
    }

    private async Task NeutralizeUnderGateAsync(string reason, CancellationToken cancellationToken)
    {
        if (Target is not { } target)
        {
            return;
        }

        await Output.StopAsync(reason, cancellationToken).ConfigureAwait(false);
        if (!_neutral)
        {
            await _backend.NeutralizeAsync(target, NewNeutral(), cancellationToken)
                .ConfigureAwait(false);
            _neutral = true;
        }

        State = ManagedTargetState.Neutral;
    }

    private async Task RemoveUnderGateAsync(string reason, CancellationToken cancellationToken)
    {
        if (Target is not { } target)
        {
            State = ManagedTargetState.Absent;
            return;
        }

        await NeutralizeUnderGateAsync(reason, cancellationToken).ConfigureAwait(false);
        // Close the managed route before native plugout: a host feedback packet already in flight
        // during removal must see no route to the physical controller or the replacement target.
        Output.Detach(target.Generation);
        await _backend.RemoveTargetAsync(target, cancellationToken).ConfigureAwait(false);
        if (!await _backend.WaitForRemovalAsync(target, cancellationToken).ConfigureAwait(false))
        {
            State = ManagedTargetState.Faulted;
            throw new InvalidOperationException("Virtual target removal was not observed.");
        }

        Target = null;
        _neutral = true;
        State = ManagedTargetState.Absent;
    }

    private CanonicalControllerSample NewNeutral()
    {
        return CanonicalControllerSample.Neutral(_timeProvider.GetUtcNow());
    }

    private void OnTargetLost(object? sender, long generation)
    {
        if (Target?.Generation != generation)
        {
            return;
        }

        State = ManagedTargetState.Faulted;
        var stop = Output.StopAsync("target-lost", CancellationToken.None);
        Output.Detach(generation);
        Target = null;
        _neutral = true;
        _ = ObserveTargetLossStopAsync(stop);
        Log.Warn($"Managed controller target generation {generation} was lost; routing stopped.");
        TargetFaulted?.Invoke("The virtual controller target was lost.");
    }

    private static async Task ObserveTargetLossStopAsync(Task stop)
    {
        try
        {
            await stop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Managed target was lost and physical output stop was unverified", ex);
        }
    }
}
