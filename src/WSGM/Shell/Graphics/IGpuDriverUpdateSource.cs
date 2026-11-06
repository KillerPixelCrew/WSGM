using System.Collections.Generic;
using LibGPUDriverInteract;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The update workflow both surfaces borrow; views own no network, installer or hardware lifetime.</summary>
internal interface IGpuDriverUpdateSource : IChangeSource
{
    bool Automatic { get; }
    GpuDriverInstallHistory? History { get; }
    string? DiscoveryDetail => null;
    IReadOnlyList<GpuDriverUpdateState> Snapshot();
    string? InstallRefusal(GpuInstalledDriver? adapter = null);
    void SetAutomatic(bool enabled);
    bool Check();
    bool Install(string instanceId);
    void Cancel();
}
