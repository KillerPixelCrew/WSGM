// Shared between WSGM and WSGM.PackagedLaunch (linked as a source file).

using System;
using System.Threading;

namespace WSGM.Core;

/// <summary>Owns a named mutex until disposed on its acquiring thread.</summary>
internal sealed class NamedMutexLease : IDisposable
{
    private Mutex? _mutex;

    private NamedMutexLease(Mutex mutex)
    {
        _mutex = mutex;
    }

    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        finally
        {
            mutex.Dispose();
        }
    }

    internal static IDisposable? TryAcquire(string name, TimeSpan timeout)
    {
        return AcquireCore(name, mutex => mutex.WaitOne(timeout));
    }

    internal static IDisposable Acquire(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lease = AcquireCore(name, mutex => WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle]) == 0);
        if (lease is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The storage mutex was not acquired.");
        }

        return lease;
    }

    private static IDisposable? AcquireCore(string name, Func<Mutex, bool> wait)
    {
        var mutex = new Mutex(false, name);
        try
        {
            bool acquired;
            try
            {
                acquired = wait(mutex);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (acquired)
            {
                return new NamedMutexLease(mutex);
            }

            mutex.Dispose();
            return null;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }
}
