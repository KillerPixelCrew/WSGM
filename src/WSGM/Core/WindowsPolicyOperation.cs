using System;
using System.Threading;

namespace WSGM.Core;

/// <summary>Serializes an elevated policy operation separately from configuration persistence.</summary>
internal sealed class WindowsPolicyOperation : IDisposable
{
    private readonly Mutex _mutex;

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

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
