using System;
using System.Threading;

namespace WSGM.Shell;

/// <summary>One held entry of the transition gate.</summary>
internal readonly struct DeviceTransitionLease(SemaphoreSlim gate) : IDisposable
{
    public void Dispose()
    {
        gate.Release();
    }
}
