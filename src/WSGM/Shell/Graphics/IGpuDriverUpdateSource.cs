using System.Collections.Generic;
using LibGPUDriverInteract;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The update workflow both surfaces borrow; views own no network, installer or hardware lifetime.</summary>
internal interface IGpuDriverUpdateSource : IChangeSource
{
    bool Automatic { get; }
    GpuDriverInstallHistory? History { get; }
    string? DiscoveryDetail { get; }
    event System.Action? ProgressChanged;
    string? TakeAnnouncement();
    IReadOnlyList<GpuDriverUpdateState> Snapshot();
    string? InstallRefusal(GpuInstalledDriver adapter);
    void SetAutomatic(bool enabled);
    bool Check();
    bool Install(string instanceId, string confirmedPackageIdentity);
    void Cancel();
}
