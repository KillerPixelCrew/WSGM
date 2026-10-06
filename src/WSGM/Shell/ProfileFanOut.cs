using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One consumer of profile changes, applied in the order it was registered.</summary>
/// <param name="Name">Name for the log.</param>
/// <param name="ApplyAsync">Applies the resolved values of a snapshot.</param>
internal sealed record ProfileConsumer(string Name, Func<ProfileSnapshot, CancellationToken, Task> ApplyAsync);

/// <summary>
///     Carries every profile change to its consumers in one fixed order, latest snapshot wins.
/// </summary>
/// <remarks>
///     One queue for every change, so RTSS, the device, the power and refresh preferences and the
///     controller target always apply the same snapshot in the same order. A different running
///     application cancels the pass in progress, because its values are already obsolete. A value change
///     does not: it waits behind the current pass, so a restore that is halfway through a set of lighting
///     zones is not abandoned because the user moved a slider.
/// </remarks>
internal sealed class ProfileFanOut : IDisposable
{
    private readonly IReadOnlyList<ProfileConsumer> _consumers;
    private readonly Lock _gate = new();
    private readonly ProfileService _service;
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _active;
    private bool _disposed;
    private (ProfileSnapshot Snapshot, ProfileChangeKind Kind)? _pending;
    private Task _worker = Task.CompletedTask;
    private bool _workerRunning;

    /// <summary>Subscribes to changes; the caller queues the initial snapshot.</summary>
    /// <param name="service">The profile owner.</param>
    /// <param name="consumers">Consumers, in application order.</param>
    internal ProfileFanOut(ProfileService service, IReadOnlyList<ProfileConsumer> consumers)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _consumers = consumers ?? throw new ArgumentNullException(nameof(consumers));
        _service.Changed += Queue;
    }

    /// <summary>Completes when the current queued work has finished.</summary>
    internal Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _worker;
            }
        }
    }

    public void Dispose()
    {
        Close();
    }

    /// <summary>Stops accepting work and cancels the pass without waiting for its consumer.</summary>
    internal void Close()
    {
        CancellationTokenSource? active;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending = null;
            active = _active;
        }

        _service.Changed -= Queue;
        TryCancel(_shutdown);
        TryCancel(active);
    }

    /// <summary>Queues a snapshot for every consumer.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="kind">Why it changed.</param>
    internal void Queue(ProfileSnapshot snapshot, ProfileChangeKind kind)
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // A value change queued behind an application change keeps the application kind, so
            // the newer application still cancels whatever pass it lands behind.
            var merged = _pending is { Kind: ProfileChangeKind.Application } ? ProfileChangeKind.Application : kind;
            _pending = (snapshot, merged);
            if (merged is ProfileChangeKind.Application)
            {
                cancel = _active;
            }

            if (!_workerRunning)
            {
                _workerRunning = true;
                _worker = Task.Run(RunAsync, CancellationToken.None);
            }
        }

        TryCancel(cancel);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            ProfileSnapshot snapshot;
            CancellationTokenSource pass;
            lock (_gate)
            {
                if (_pending is not { } pending || _disposed)
                {
                    _pending = null;
                    _workerRunning = false;
                    return;
                }

                _pending = null;
                snapshot = pending.Snapshot;
                pass = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _active = pass;
            }

            try
            {
                foreach (var consumer in _consumers)
                {
                    if (pass.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        await consumer.ApplyAsync(snapshot, pass.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (pass.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // One consumer failing must not keep the rest on the previous snapshot.
                        Log.Warn(
                            $"Profile apply for {consumer.Name} failed at generation {snapshot.Generation}: "
                            + ex.Message);
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_active, pass))
                    {
                        _active = null;
                    }
                }

                pass.Dispose();
            }
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            if (cancellation is not null)
            {
                Log.Observe(cancellation.CancelAsync(), "Profile fan-out cancellation");
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
