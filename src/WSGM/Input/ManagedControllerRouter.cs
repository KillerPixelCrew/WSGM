using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

internal enum ManagedTargetState
{
    Absent,
    Neutral,
    Active,
    Faulted
}

internal sealed class ManagedControllerRouter : IAsyncDisposable
{
    private readonly IControllerTargetBackend _backend;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private bool _disposed;
    private bool _neutral = true;

    internal ManagedControllerRouter(
        IControllerTargetBackend backend,
        IPhysicalHapticSink hapticSink,
        TimeProvider? timeProvider = null)
    {
        _backend = backend;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Output = new ControllerOutputRouter(backend, hapticSink, _timeProvider);
        _backend.TargetLost += OnTargetLost;
    }

    internal ManagedTargetState State { get; private set; } = ManagedTargetState.Absent;

    internal ControllerTargetHandle? Target { get; private set; }

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

    internal async Task<ControllerTargetHandle> CreateAsync(
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

    internal async Task<ControllerTargetHandle> ReplaceAsync(
        ManagedControllerTarget kind,
        CancellationToken cancellationToken)
    {
        ControllerTargetHandle? target = null;
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

    private async Task<ControllerTargetHandle> CreateUnderGateAsync(
        ManagedControllerTarget kind,
        CancellationToken cancellationToken)
    {
        var neutral = NewNeutral();
        var target = await _backend.CreateTargetAsync(kind, neutral, cancellationToken)
            .ConfigureAwait(false);
        Target = target;
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

        try
        {
            await NeutralizeUnderGateAsync(reason, cancellationToken).ConfigureAwait(false);
            // Close the managed route before native plugout: a host feedback packet already in flight
            // during removal must see no route to the physical controller or the replacement target.
            Output.Detach(target.Generation);
            if (!await _backend.RemoveTargetAsync(target, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Virtual target removal was not observed.");
            }
        }
        finally
        {
            Output.Detach(target.Generation);
            Target = null;
            _neutral = true;
            State = ManagedTargetState.Absent;
        }
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
