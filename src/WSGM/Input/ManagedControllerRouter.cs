using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Contracts.Input;

namespace WSGM.Input;

internal enum ControllerOutputState
{
    Stopped,
    Active,
    Faulted,
    Indeterminate,
}

internal interface IPhysicalHapticSink
{
    long SourceGeneration { get; }

    bool IsOwned { get; }

    HapticCapabilities Capabilities { get; }

    Task ApplyAsync(HapticOutputFrame frame, CancellationToken cancellationToken);

    Task StopAsync(long targetGeneration, string reason, CancellationToken cancellationToken);
}

internal sealed class DeterministicFakeHapticSink : IPhysicalHapticSink
{
    private readonly object _gate = new();
    private readonly List<HapticOutputFrame> _frames = [];
    private readonly List<string> _stopReasons = [];

    internal DeterministicFakeHapticSink(long sourceGeneration, HapticCapabilities? capabilities = null)
    {
        SourceGeneration = sourceGeneration;
        Capabilities = capabilities ?? new()
        {
            LowFrequency = OutputChannelSupport.Native,
            HighFrequency = OutputChannelSupport.Native,
            MaxFramesPerSecond = 60,
        };
    }

    public long SourceGeneration { get; set; }

    public bool IsOwned { get; set; } = true;

    public HapticCapabilities Capabilities { get; set; }

    internal Exception? NextFailure { get; set; }

    internal IReadOnlyList<HapticOutputFrame> Frames
    {
        get
        {
            lock (_gate)
            {
                return _frames.ToArray();
            }
        }
    }

    internal IReadOnlyList<string> StopReasons
    {
        get
        {
            lock (_gate)
            {
                return _stopReasons.ToArray();
            }
        }
    }

    public Task ApplyAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfFaultRequested();
            _frames.Add(frame);
            return Task.CompletedTask;
        }
    }

    public Task StopAsync(
        long targetGeneration,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfFaultRequested();
            _stopReasons.Add(reason);
            _frames.Add(HapticOutputFrame.Stop(targetGeneration, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }
    }

    private void ThrowIfFaultRequested()
    {
        if (NextFailure is not { } failure)
        {
            return;
        }

        NextFailure = null;
        throw failure;
    }
}

internal sealed class ControllerOutputRouter : IAsyncDisposable
{
    private static readonly TimeSpan MaxOutputAge = TimeSpan.FromMilliseconds(250);
    private readonly object _gate = new();
    private readonly IHidBackend _backend;
    private readonly IPhysicalHapticSink _sink;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<HidTargetOutput> _queue = Channel.CreateBounded<HidTargetOutput>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sinkGate = new(1, 1);
    private readonly Task _worker;
    private HidTargetHandle? _target;
    private long _sourceGeneration;
    private long _routeGeneration;
    private long _lastDispatchTimestamp;
    private bool _outputQuarantined;
    private bool _disposed;

    internal ControllerOutputRouter(
        IHidBackend backend,
        IPhysicalHapticSink sink,
        TimeProvider? timeProvider = null)
    {
        _backend = backend;
        _sink = sink;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _backend.OutputReceived += OnOutputReceived;
        _worker = RunAsync();
    }

    internal ControllerOutputState State { get; private set; } = ControllerOutputState.Stopped;

    internal int DroppedFrames { get; private set; }

    internal int DeliveredFrames { get; private set; }

    internal void Attach(HidTargetHandle target, long sourceGeneration)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _target = target;
            _sourceGeneration = sourceGeneration;
            _routeGeneration++;
            _lastDispatchTimestamp = 0;
            _outputQuarantined = false;
            State = ControllerOutputState.Stopped;
            DrainUnderGate();
        }
    }

    internal async Task StopAsync(string reason, CancellationToken cancellationToken)
    {
        HidTargetHandle? target;
        lock (_gate)
        {
            target = _target;
            _routeGeneration++;
            DrainUnderGate();
        }

        if (target is null)
        {
            State = ControllerOutputState.Stopped;
            return;
        }

        await _sinkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _sink.StopAsync(target.Generation, reason, cancellationToken).ConfigureAwait(false);
            State = ControllerOutputState.Stopped;
        }
        catch
        {
            State = ControllerOutputState.Indeterminate;
            throw;
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
            _sourceGeneration = 0;
            _routeGeneration++;
            State = ControllerOutputState.Stopped;
            DrainUnderGate();
        }
    }

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
            _routeGeneration++;
            DrainUnderGate();
        }

        _queue.Writer.TryComplete();
        _lifetime.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _sinkGate.Dispose();
        _lifetime.Dispose();
    }

    private void OnOutputReceived(object? sender, HidTargetOutput output)
    {
        lock (_gate)
        {
            if (_disposed || !CanQueueUnderGate(output))
            {
                DroppedFrames++;
                return;
            }

            if (!_queue.Writer.TryWrite(output))
            {
                DroppedFrames++;
            }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (HidTargetOutput output in _queue.Reader.ReadAllAsync(_lifetime.Token)
                .ConfigureAwait(false))
            {
                HidTargetHandle? target;
                long sourceGeneration;
                long routeGeneration;
                lock (_gate)
                {
                    if (!CanQueueUnderGate(output))
                    {
                        DroppedFrames++;
                        continue;
                    }

                    target = _target;
                    sourceGeneration = _sourceGeneration;
                    routeGeneration = _routeGeneration;
                }

                if (target is null || sourceGeneration != _sink.SourceGeneration || !_sink.IsOwned)
                {
                    DroppedFrames++;
                    continue;
                }

                HapticOutputFrame frame = _sink.Capabilities.Clamp(output.Frame);
                int framesPerSecond = Math.Clamp(_sink.Capabilities.MaxFramesPerSecond, 1, 1000);
                TimeSpan minimumInterval = TimeSpan.FromSeconds(1d / framesPerSecond);
                if (_lastDispatchTimestamp != 0)
                {
                    TimeSpan elapsed = _timeProvider.GetElapsedTime(_lastDispatchTimestamp);
                    if (elapsed < minimumInterval)
                    {
                        await Task.Delay(minimumInterval - elapsed, _timeProvider, _lifetime.Token)
                            .ConfigureAwait(false);
                    }
                }

                lock (_gate)
                {
                    if (_routeGeneration != routeGeneration || !MatchesRouteUnderGate(output))
                    {
                        DroppedFrames++;
                        continue;
                    }
                }

                await _sinkGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    await _sink.ApplyAsync(frame, _lifetime.Token).ConfigureAwait(false);
                    _lastDispatchTimestamp = _timeProvider.GetTimestamp();
                    State = frame.IsSilent ? ControllerOutputState.Stopped : ControllerOutputState.Active;
                    DeliveredFrames++;
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _outputQuarantined = true;
                        State = ControllerOutputState.Faulted;
                    }

                    Log.Error("Managed controller output sink faulted; input remains active", ex);
                }
                finally
                {
                    _sinkGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private bool CanQueueUnderGate(HidTargetOutput output)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        return !_outputQuarantined
            && MatchesRouteUnderGate(output)
            && output.Frame.Timestamp <= now.AddSeconds(1)
            && now - output.Frame.Timestamp <= MaxOutputAge
            && FiniteUnit(output.Frame.LowFrequency)
            && FiniteUnit(output.Frame.HighFrequency)
            && FiniteUnit(output.Frame.LeftTrigger)
            && FiniteUnit(output.Frame.RightTrigger);
    }

    private bool MatchesRouteUnderGate(HidTargetOutput output) =>
        _target is { } target
        && output.Frame.TargetGeneration == target.Generation
        && output.SourceKind == target.Kind;

    private void DrainUnderGate()
    {
        while (_queue.Reader.TryRead(out _))
        {
            DroppedFrames++;
        }
    }

    private static bool FiniteUnit(float value) => float.IsFinite(value) && value is >= 0 and <= 1;
}

internal sealed class ManagedControllerRouter : IAsyncDisposable
{
    private readonly IHidBackend _backend;
    private readonly ControllerOutputRouter _output;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private HidTargetHandle? _target;
    private long _sourceGeneration;
    private long _lastSequence = long.MinValue;
    private bool _neutral = true;
    private bool _disposed;

    internal ManagedControllerRouter(
        IHidBackend backend,
        IPhysicalHapticSink hapticSink,
        TimeProvider? timeProvider = null)
    {
        _backend = backend;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _output = new(backend, hapticSink, _timeProvider);
        _backend.TargetLost += OnTargetLost;
    }

    internal ManagedTargetState State { get; private set; } = ManagedTargetState.Absent;

    internal HidTargetHandle? Target => _target;

    internal ControllerOutputRouter Output => _output;

    internal async Task<HidTargetHandle> CreateAsync(
        VirtualTargetKind kind,
        long sourceGeneration,
        CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_target is not null)
            {
                throw new InvalidOperationException("A managed target already exists.");
            }

            HidBackendHealth health = await _backend.DiscoverAsync(cancellationToken)
                .ConfigureAwait(false);
            if (health.State is not HidBackendHealthState.Ready
                || health.Capabilities is null
                || !health.Capabilities.SupportedTargets.Contains(kind))
            {
                throw new InvalidOperationException($"Managed controller backend unavailable: {health.Detail}");
            }

            State = ManagedTargetState.Creating;
            _sourceGeneration = sourceGeneration;
            _lastSequence = long.MinValue;
            CanonicalControllerSample neutral = NewNeutral(sourceGeneration);
            HidTargetHandle target = await _backend.CreateTargetAsync(kind, neutral, cancellationToken)
                .ConfigureAwait(false);
            _target = target;

            bool enumerated = await _backend.WaitForEnumerationAsync(target, cancellationToken)
                .ConfigureAwait(false);
            if (!enumerated)
            {
                State = ManagedTargetState.Faulted;
                await RemoveUnderGateAsync("enumeration-failed", cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("The virtual target did not enumerate.");
            }

            _neutral = true;
            State = ManagedTargetState.Neutral;
            _output.Attach(target, sourceGeneration);
            return target;
        }
        catch (Exception)
        {
            State = ManagedTargetState.Faulted;
            if (_target is not null)
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

    internal void ActivateSource(long sourceGeneration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_target is null || State is not ManagedTargetState.Neutral)
        {
            throw new InvalidOperationException("A verified neutral target is required before routing.");
        }

        _sourceGeneration = sourceGeneration;
        _lastSequence = long.MinValue;
        _output.Attach(_target, sourceGeneration);
        State = ManagedTargetState.Active;
    }

    internal async ValueTask<bool> RouteAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        HidTargetHandle? target = _target;
        if (target is null || State is not ManagedTargetState.Active)
        {
            return false;
        }

        if (!ManagedControllerSampleValidator.TryValidate(
            sample,
            _sourceGeneration,
            _lastSequence,
            _timeProvider.GetUtcNow(),
            out _))
        {
            await NeutralizeAsync("source-invalid", cancellationToken).ConfigureAwait(false);
            return false;
        }

        bool delivered = await _backend.PublishAsync(target, sample, cancellationToken)
            .ConfigureAwait(false);
        if (delivered)
        {
            _lastSequence = sample.Sequence;
            _neutral = ManagedControllerSampleValidator.IsNeutral(sample);
        }

        return delivered;
    }

    internal async Task NeutralizeAsync(string reason, CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await NeutralizeUnderGateAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    internal async Task RemoveAsync(string reason, CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RemoveUnderGateAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    internal async Task<HidTargetHandle> ReplaceAsync(
        VirtualTargetKind kind,
        long sourceGeneration,
        CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            State = ManagedTargetState.Replacing;
            await RemoveUnderGateAsync("target-replacement", cancellationToken).ConfigureAwait(false);

            HidBackendHealth health = await _backend.DiscoverAsync(cancellationToken)
                .ConfigureAwait(false);
            if (health.State is not HidBackendHealthState.Ready
                || health.Capabilities is null
                || !health.Capabilities.SupportedTargets.Contains(kind))
            {
                State = ManagedTargetState.Faulted;
                throw new InvalidOperationException($"Managed controller backend unavailable: {health.Detail}");
            }

            State = ManagedTargetState.Creating;
            _sourceGeneration = sourceGeneration;
            _lastSequence = long.MinValue;
            CanonicalControllerSample neutral = NewNeutral(sourceGeneration);
            HidTargetHandle target = await _backend.CreateTargetAsync(kind, neutral, cancellationToken)
                .ConfigureAwait(false);
            _target = target;
            if (!await _backend.WaitForEnumerationAsync(target, cancellationToken).ConfigureAwait(false))
            {
                State = ManagedTargetState.Faulted;
                await RemoveUnderGateAsync("replacement-enumeration-failed", cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidOperationException("The replacement target did not enumerate.");
            }

            _neutral = true;
            State = ManagedTargetState.Neutral;
            _output.Attach(target, sourceGeneration);
            return target;
        }
        finally
        {
            _transition.Release();
        }
    }

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

        await _output.DisposeAsync().ConfigureAwait(false);
        await _backend.DisposeAsync().ConfigureAwait(false);
        _transition.Dispose();
    }

    private async Task NeutralizeUnderGateAsync(string reason, CancellationToken cancellationToken)
    {
        if (_target is not { } target)
        {
            return;
        }

        await _output.StopAsync(reason, cancellationToken).ConfigureAwait(false);
        if (!_neutral)
        {
            await _backend.NeutralizeAsync(target, NewNeutral(_sourceGeneration), cancellationToken)
                .ConfigureAwait(false);
            _neutral = true;
        }

        State = ManagedTargetState.Neutral;
    }

    private async Task RemoveUnderGateAsync(string reason, CancellationToken cancellationToken)
    {
        if (_target is not { } target)
        {
            State = ManagedTargetState.Absent;
            return;
        }

        await NeutralizeUnderGateAsync(reason, cancellationToken).ConfigureAwait(false);
        State = ManagedTargetState.Removing;
        await _backend.RemoveTargetAsync(target, cancellationToken).ConfigureAwait(false);
        if (!await _backend.WaitForRemovalAsync(target, cancellationToken).ConfigureAwait(false))
        {
            State = ManagedTargetState.Faulted;
            throw new InvalidOperationException("Virtual target removal was not observed.");
        }

        _output.Detach(target.Generation);
        _target = null;
        _sourceGeneration = 0;
        _lastSequence = long.MinValue;
        _neutral = true;
        State = ManagedTargetState.Absent;
    }

    private CanonicalControllerSample NewNeutral(long sourceGeneration) =>
        CanonicalControllerSample.Neutral(
            _lastSequence == long.MaxValue ? long.MaxValue : Math.Max(0, _lastSequence + 1),
            sourceGeneration,
            _timeProvider.GetUtcNow());

    private void OnTargetLost(object? sender, long generation)
    {
        if (_target?.Generation != generation)
        {
            return;
        }

        State = ManagedTargetState.Faulted;
        Task stop = _output.StopAsync("target-lost", CancellationToken.None);
        _output.Detach(generation);
        _target = null;
        _neutral = true;
        _ = ObserveTargetLossStopAsync(stop);
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
