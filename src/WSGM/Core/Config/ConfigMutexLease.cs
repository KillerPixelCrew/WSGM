using System;
using System.Threading;

namespace WSGM.Core;

/// <summary>Owns an already-acquired configuration mutex and its native handle.</summary>
/// <param name="mutex">Mutex whose ownership transfers to this same-thread lease.</param>
internal sealed class ConfigMutexLease(Mutex mutex) : IDisposable
{
    /// <summary>Releases the acquired mutex and closes it; release failures are logged.</summary>
    /// <remarks>Call once on the acquiring thread.</remarks>
    public void Dispose()
    {
        try
        {
            mutex.ReleaseMutex();
        }
        catch (Exception ex)
        {
            Log.Warn($"Config mutex release failed: {ex.Message}");
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
