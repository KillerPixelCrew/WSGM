using System;
using System.Threading;

namespace WSGM.Core;

/// <summary>Serializes an elevated policy operation separately from configuration persistence.</summary>
internal sealed class WindowsPolicyOperation : IDisposable
{
    private readonly Mutex _mutex;

    /// <summary>Acquires the per-session, cross-process lock for one policy category.</summary>
    /// <param name="name">Stable category suffix shared by every writer of this policy.</param>
    /// <exception cref="TimeoutException">Another owner held the lock for 30 seconds.</exception>
    /// <remarks>Acquisition blocks. Dispose on the acquiring thread; do not carry this lease across an await.</remarks>
    internal WindowsPolicyOperation(string name)
    {
        _mutex = new Mutex(false, @"Local\WSGM.Policy." + name);
        try
        {
            if (!_mutex.WaitOne(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Another Windows policy change is still running.");
            }
        }
        catch (AbandonedMutexException)
        {
            // The prior helper exited; this thread now owns its operation lock.
        }
        catch
        {
            _mutex.Dispose();
            throw;
        }
    }

    /// <summary>Releases the policy lock and closes its handle.</summary>
    /// <remarks>Call once, on the thread that constructed this lease.</remarks>
    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
