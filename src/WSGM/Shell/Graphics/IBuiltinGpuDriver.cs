using System;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;

namespace WSGM.Shell;

/// <summary>The native owner's lifecycle boundary; admission closes before any owned call is joined.</summary>
internal interface IBuiltinGpuDriver : IAsyncDisposable
{
    ValueTask<GpuHealth> StartAsync(CancellationToken token);
    ValueTask RefreshTopologyAsync(CancellationToken token);
    void CloseAdmission();
    void SetSuspended(bool suspended);
}
