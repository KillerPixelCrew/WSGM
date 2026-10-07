using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Counts observation leases and wakes a poll loop when the first one arrives.</summary>
/// <remarks>
///     A loop that only works while someone watches waits on <see cref="WaitAsync" /> whenever
///     <see cref="Count" /> is zero. Taking the first lease, or <see cref="Signal" />, wakes it; extra
///     signals collapse into one.
/// </remarks>
internal sealed class ObservationGate : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private int _count;

    /// <summary>How many leases are held now.</summary>
    internal int Count => Volatile.Read(ref _count);

    /// <summary>Releases the wake semaphore after the owner has canceled and joined its poll loop.</summary>
    /// <remarks>Outstanding observation leases must no longer be used to start work.</remarks>
    public void Dispose()
    {
        _signal.Dispose();
    }

    /// <summary>Takes a lease, waking the loop when it is the first.</summary>
    /// <returns>A lease that releases exactly once when disposed.</returns>
    internal IDisposable Acquire()
    {
        if (Interlocked.Increment(ref _count) == 1)
        {
            Signal();
        }

        return new Lease(this);
    }

    /// <summary>Wakes the loop if it is waiting.</summary>
    internal void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // A racing observation release during disposal has no work left to wake.
        }
    }

    /// <summary>Waits for the next wake.</summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>A task that consumes one pending or future wake; a wake does not guarantee a lease is still held.</returns>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    internal Task WaitAsync(CancellationToken cancellationToken)
    {
        return _signal.WaitAsync(cancellationToken);
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _count) < 0)
        {
            Interlocked.Exchange(ref _count, 0);
        }
    }

    private sealed class Lease(ObservationGate owner) : IDisposable
    {
        private ObservationGate? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
