using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>Owns one logical virtual-target lifetime and serializes create, neutralize, replace and removal transitions.</summary>
/// <remarks>
///     The caller owns the backend and physical sink. Sample forwarding uses the current generation without the
///     transition gate.
/// </remarks>
internal sealed class ManagedControllerRouter : IAsyncDisposable
{
    private readonly IControllerTargetBackend _backend;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private bool _disposed;
    private bool _neutral = true;

    /// <summary>Creates the output router and subscribes to target loss without creating a virtual device.</summary>
    /// <param name="backend">Borrowed backend responsible for native device ownership.</param>
    /// <param name="hapticSink">Borrowed physical output owner.</param>
    /// <param name="timeProvider">Clock for neutral samples and output timing; null uses system time.</param>
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

    /// <summary>Current logical target, or null after removal, loss or failed creation.</summary>
    internal ControllerTargetHandle? Target { get; private set; }

    /// <summary>Owned output route; disposed with this router and stopped before target removal.</summary>
    internal ControllerOutputRouter Output { get; }

    /// <summary>Attempts neutralization/removal with a two-second cleanup token, then disposes the output worker.</summary>
    /// <returns>Completion of cleanup; native removal failures are logged and do not dispose the borrowed backend.</returns>
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
                Log.Error("Managed controller cleanup was not verified", ex);
            }
        }
        finally
        {
            _transition.Release();
        }

        await Output.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Raised when the backend lost the target and this router faulted.</summary>
    /// <remarks>
    ///     Raised on the backend callback thread after detaching the logical route and starting output stop.
    ///     The session must mark management unavailable; the physical stop may still be completing.
    /// </remarks>
    internal event Action<string>? TargetFaulted;

    /// <summary>Creates a target with a neutral first report and attaches its output route.</summary>
    /// <param name="kind">Supported virtual controller protocol.</param>
    /// <param name="cancellationToken">Cancels transition waits and backend work.</param>
    /// <returns>The newly current target; failures attempt bounded cleanup and propagate.</returns>
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

            throw;
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>Forwards a valid sample to the current target; invalid input neutralizes any previously held state.</summary>
    /// <param name="sample">Full canonical state; range validity is checked without imposing an age/sequence policy.</param>
    /// <param name="cancellationToken">Cancels backend waiting and any required neutralization.</param>
    /// <returns>True when accepted; false for absent targets, invalid samples or rejected submissions.</returns>
    internal async ValueTask<bool> RouteAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        var target = Target;
        if (target is null)
        {
            return false;
        }

        if (!ManagedControllerSampleValidator.IsValid(sample))
        {
            // Keyed and without the per-sample numbers, so a burst of refused samples (every sample
            // queued while a target was being created arrives stale) is one line, not hundreds.
            Log.Change("managed-controller-neutralized",
                "Managed controller input was neutralized: reason=out-of-range-sample.", LogLevel.Warn);
            if (!_neutral)
            {
                await NeutralizeAsync("source-invalid:out-of-range-sample", cancellationToken).ConfigureAwait(false);
            }

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

    /// <summary>Stops physical output and publishes neutral input when a current target has held state.</summary>
    /// <param name="reason">Diagnostic reason for dropping held state.</param>
    /// <param name="cancellationToken">Cancels transition/backend waits; a cancelled or failed stop is not verified cleanup.</param>
    /// <returns>Completion of the neutralization attempt; absent targets are a no-op.</returns>
    internal Task NeutralizeAsync(string reason, CancellationToken cancellationToken)
    {
        return UnderGateAsync(() => NeutralizeUnderGateAsync(reason, cancellationToken), cancellationToken);
    }

    /// <summary>Neutralizes and removes the current target, clearing the logical route even if removal fails.</summary>
    /// <param name="reason">Diagnostic reason for ending the target lifetime.</param>
    /// <param name="cancellationToken">Cancels waits and backend work.</param>
    /// <returns>Completion of removal; unconfirmed native removal throws.</returns>
    internal Task RemoveAsync(string reason, CancellationToken cancellationToken)
    {
        return UnderGateAsync(() => RemoveUnderGateAsync(reason, cancellationToken), cancellationToken);
    }

    /// <summary>Removes the current target before creating its replacement under one transition gate.</summary>
    /// <param name="kind">Replacement virtual protocol.</param>
    /// <param name="cancellationToken">Cancels transition/backend waits.</param>
    /// <returns>The replacement target; failure does not restore the old device.</returns>
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
            if (!await _backend.PublishAsync(target, NewNeutral(), cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The controller backend could not write a neutral report to the virtual target.");
            }

            _neutral = true;
        }
    }

    private async Task RemoveUnderGateAsync(string reason, CancellationToken cancellationToken)
    {
        if (Target is not { } target)
        {
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
