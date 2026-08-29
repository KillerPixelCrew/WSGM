using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

internal enum HidBackendHealthState
{
    Unavailable,
    Incompatible,
    Ready,
    Faulted,
}

internal enum ManagedTargetState
{
    Absent,
    Creating,
    Neutral,
    Active,
    Replacing,
    Faulted,
    Removing,
}

internal sealed record HidBackendCapabilities(
    Version ProtocolVersion,
    IReadOnlyList<VirtualTargetKind> SupportedTargets,
    bool SupportsOutput);

internal sealed record HidBackendHealth(
    HidBackendHealthState State,
    string Detail,
    HidBackendCapabilities? Capabilities = null);

internal sealed record HidTargetHandle(
    VirtualTargetKind Kind,
    long Generation,
    string InstanceId);

internal sealed record HidTargetOutput(
    HapticOutputFrame Frame,
    VirtualTargetKind SourceKind);

internal interface IHidBackend : IAsyncDisposable
{
    event EventHandler<HidTargetOutput>? OutputReceived;

    event EventHandler<long>? TargetLost;

    Task<HidBackendHealth> DiscoverAsync(CancellationToken cancellationToken);

    Task<HidTargetHandle> CreateTargetAsync(
        VirtualTargetKind kind,
        CanonicalControllerSample initialNeutralState,
        CancellationToken cancellationToken);

    Task<bool> WaitForEnumerationAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken);

    ValueTask<bool> PublishAsync(
        HidTargetHandle target,
        CanonicalControllerSample sample,
        CancellationToken cancellationToken);

    Task NeutralizeAsync(
        HidTargetHandle target,
        CanonicalControllerSample neutralState,
        CancellationToken cancellationToken);

    Task RemoveTargetAsync(HidTargetHandle target, CancellationToken cancellationToken);

    Task<bool> WaitForRemovalAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> GetDiagnosticsAsync(
        CancellationToken cancellationToken);
}

internal sealed class DeterministicFakeHidBackend : IHidBackend
{
    private readonly object _gate = new();
    private readonly List<string> _operations = [];
    private readonly Dictionary<long, TaskCompletionSource<bool>> _enumeration = [];
    private readonly Dictionary<long, TaskCompletionSource<bool>> _removal = [];
    private readonly Queue<HidTargetOutput> _delayedOutput = [];
    private long _nextGeneration;
    private HidTargetHandle? _target;
    private bool _disposed;

    internal DeterministicFakeHidBackend(params VirtualTargetKind[] supportedTargets)
    {
        IReadOnlyList<VirtualTargetKind> targets = supportedTargets.Length == 0
            ? Enum.GetValues<VirtualTargetKind>()
            : supportedTargets.ToArray();
        Health = new(
            HidBackendHealthState.Ready,
            "Deterministic fake backend is ready.",
            new(new Version(1, 0), targets, SupportsOutput: true));
    }

    public event EventHandler<HidTargetOutput>? OutputReceived;

    public event EventHandler<long>? TargetLost;

    internal HidBackendHealth Health { get; set; }

    internal bool AutoEnumerate { get; set; } = true;

    internal bool AutoRemove { get; set; } = true;

    internal bool DelayOutput { get; set; }

    internal Exception? NextCreateFailure { get; set; }

    internal Exception? NextPublishFailure { get; set; }

    internal Exception? NextRemoveFailure { get; set; }

    internal HidTargetHandle? CurrentTarget
    {
        get
        {
            lock (_gate)
            {
                return _target;
            }
        }
    }

    internal IReadOnlyList<string> Operations
    {
        get
        {
            lock (_gate)
            {
                return _operations.ToArray();
            }
        }
    }

    public Task<HidBackendHealth> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            _operations.Add("discover");
            return Task.FromResult(Health);
        }
    }

    public Task<HidTargetHandle> CreateTargetAsync(
        VirtualTargetKind kind,
        CanonicalControllerSample initialNeutralState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialNeutralState);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_target is not null)
            {
                throw new InvalidOperationException("The fake backend already owns a target.");
            }

            if (NextCreateFailure is { } failure)
            {
                NextCreateFailure = null;
                throw failure;
            }

            if (Health.State is not HidBackendHealthState.Ready
                || Health.Capabilities is null
                || !Health.Capabilities.SupportedTargets.Contains(kind))
            {
                throw new InvalidOperationException($"Target {kind} is unavailable.");
            }

            if (!ManagedControllerSampleValidator.IsNeutral(initialNeutralState))
            {
                throw new ArgumentException("The first target state must be neutral.",
                    nameof(initialNeutralState));
            }

            long generation = ++_nextGeneration;
            _target = new(kind, generation, $"fake-target-{generation}");
            _operations.Add($"create:{generation}:neutral");
            _enumeration.Add(generation, NewCompletionSource(AutoEnumerate));
            _removal.Add(generation, NewCompletionSource(completed: false));
            return Task.FromResult(_target);
        }
    }

    public Task<bool> WaitForEnumerationAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken)
    {
        Task<bool> completion;
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateTarget(target);
            _operations.Add($"wait-enumeration:{target.Generation}");
            completion = _enumeration[target.Generation].Task;
        }

        return completion.WaitAsync(cancellationToken);
    }

    public ValueTask<bool> PublishAsync(
        HidTargetHandle target,
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateTarget(target);
            if (NextPublishFailure is { } failure)
            {
                NextPublishFailure = null;
                throw failure;
            }

            _operations.Add(ManagedControllerSampleValidator.IsNeutral(sample)
                ? $"publish:{target.Generation}:neutral"
                : $"publish:{target.Generation}:live");
            return ValueTask.FromResult(true);
        }
    }

    public Task NeutralizeAsync(
        HidTargetHandle target,
        CanonicalControllerSample neutralState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(neutralState);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateTarget(target);
            if (!ManagedControllerSampleValidator.IsNeutral(neutralState))
            {
                throw new ArgumentException("Neutralization requires a neutral sample.",
                    nameof(neutralState));
            }

            _operations.Add($"neutralize:{target.Generation}");
            return Task.CompletedTask;
        }
    }

    public Task RemoveTargetAsync(HidTargetHandle target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateTarget(target);
            if (NextRemoveFailure is { } failure)
            {
                NextRemoveFailure = null;
                throw failure;
            }

            _operations.Add($"remove:{target.Generation}");
            if (AutoRemove)
            {
                CompleteRemovalUnderGate(target.Generation);
            }

            return Task.CompletedTask;
        }
    }

    public Task<bool> WaitForRemovalAsync(
        HidTargetHandle target,
        CancellationToken cancellationToken)
    {
        Task<bool> completion;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_removal.TryGetValue(target.Generation, out TaskCompletionSource<bool>? source))
            {
                throw new InvalidOperationException("The target generation is unknown.");
            }

            _operations.Add($"wait-removal:{target.Generation}");
            completion = source.Task;
        }

        return completion.WaitAsync(cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, string>> GetDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            IReadOnlyDictionary<string, string> diagnostics = new Dictionary<string, string>
            {
                ["health"] = Health.State.ToString(),
                ["target"] = _target?.InstanceId ?? "absent",
                ["generation"] = (_target?.Generation ?? 0).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            };
            return Task.FromResult(diagnostics);
        }
    }

    internal void CompleteEnumeration(long generation, bool enumerated = true)
    {
        lock (_gate)
        {
            if (!_enumeration.TryGetValue(generation, out TaskCompletionSource<bool>? source))
            {
                throw new InvalidOperationException("The target generation is unknown.");
            }

            source.TrySetResult(enumerated);
        }
    }

    internal void CompleteRemoval(long generation, bool removed = true)
    {
        lock (_gate)
        {
            if (!removed)
            {
                _removal[generation].TrySetResult(false);
                return;
            }

            CompleteRemovalUnderGate(generation);
        }
    }

    internal void EmitOutput(HapticOutputFrame frame, VirtualTargetKind? sourceKind = null)
    {
        HidTargetOutput output;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_target is null)
            {
                throw new InvalidOperationException("No target exists.");
            }

            output = new(frame, sourceKind ?? _target.Kind);
            _operations.Add($"output:{frame.TargetGeneration}");
            if (DelayOutput)
            {
                _delayedOutput.Enqueue(output);
                return;
            }
        }

        OutputReceived?.Invoke(this, output);
    }

    internal void ReleaseDelayedOutput()
    {
        while (true)
        {
            HidTargetOutput? output;
            lock (_gate)
            {
                if (!_delayedOutput.TryDequeue(out output))
                {
                    return;
                }
            }

            OutputReceived?.Invoke(this, output);
        }
    }

    internal void LoseTarget()
    {
        long generation;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_target is null)
            {
                return;
            }

            generation = _target.Generation;
            _target = null;
            _removal[generation].TrySetResult(true);
            _operations.Add($"lost:{generation}");
        }

        TargetLost?.Invoke(this, generation);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _target = null;
            _delayedOutput.Clear();
            foreach (TaskCompletionSource<bool> completion in _enumeration.Values)
            {
                completion.TrySetCanceled();
            }

            foreach (TaskCompletionSource<bool> completion in _removal.Values)
            {
                completion.TrySetCanceled();
            }

            _operations.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }

    private static TaskCompletionSource<bool> NewCompletionSource(bool completed)
    {
        TaskCompletionSource<bool> source = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
        {
            source.SetResult(true);
        }

        return source;
    }

    private void CompleteRemovalUnderGate(long generation)
    {
        _removal[generation].TrySetResult(true);
        if (_target?.Generation == generation)
        {
            _target = null;
        }
    }

    private void ValidateTarget(HidTargetHandle target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_target is null || _target != target)
        {
            throw new InvalidOperationException("The target generation is stale.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal static class ManagedControllerSampleValidator
{
    internal static bool TryValidate(
        CanonicalControllerSample sample,
        long sourceGeneration,
        long previousSequence,
        DateTimeOffset now,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.CycleGeneration != sourceGeneration)
        {
            reason = "stale-source-generation";
            return false;
        }

        if (sample.Sequence <= previousSequence)
        {
            reason = "non-monotonic-sequence";
            return false;
        }

        if (sample.Timestamp > now.AddSeconds(1) || now - sample.Timestamp > TimeSpan.FromSeconds(1))
        {
            reason = "stale-or-future-timestamp";
            return false;
        }

        if (sample.Quality is not SampleQuality.Good
            || !Axis(sample.LeftStickX)
            || !Axis(sample.LeftStickY)
            || !Axis(sample.RightStickX)
            || !Axis(sample.RightStickY)
            || !Trigger(sample.LeftTrigger)
            || !Trigger(sample.RightTrigger)
            || !Motion(sample.Motion))
        {
            reason = "invalid-or-discontinuous-sample";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal static bool IsNeutral(CanonicalControllerSample sample) =>
        sample.Buttons == CanonicalButtons.None
        && sample.LeftStickX == 0
        && sample.LeftStickY == 0
        && sample.RightStickX == 0
        && sample.RightStickY == 0
        && sample.LeftTrigger == 0
        && sample.RightTrigger == 0
        && sample.Motion is null;

    private static bool Axis(float value) => float.IsFinite(value) && value is >= -1 and <= 1;

    private static bool Trigger(float value) => float.IsFinite(value) && value is >= 0 and <= 1;

    private static bool Motion(MotionSample? motion) => motion is null
        || ((!motion.HasGyro
            || (float.IsFinite(motion.GyroX)
                && float.IsFinite(motion.GyroY)
                && float.IsFinite(motion.GyroZ)))
            && (!motion.HasAccelerometer
                || (float.IsFinite(motion.AccelX)
                    && float.IsFinite(motion.AccelY)
                    && float.IsFinite(motion.AccelZ))));
}
