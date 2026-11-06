using LibGPUDriverInteract;

namespace WSGM.Shell;

/// <summary>Detached update state shared by Overlay and Steam; every write belongs to the session service.</summary>
internal sealed record GpuDriverUpdateState(GpuInstalledDriver Adapter, string Stage, string Detail,
    GpuDriverPackage? Package = null, int Progress = 0)
{
    internal bool Busy => Stage is "Checking" or "Downloading" or "Verifying" or "Installing";
    internal bool CanInstall => Package is not null && Stage is "Available" or "Failed";
}
