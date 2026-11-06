namespace WSGM.Shell;

/// <summary>The updater's semantic phase, independent of either surface's presentation.</summary>
internal enum GpuDriverUpdateStage
{
    NotChecked,
    Checking,
    Available,
    Downloading,
    Verifying,
    Installing,
    UpToDate,
    Unavailable,
    Failed,
    Installed,
    RestartRequired
}
