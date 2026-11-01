namespace WSGM.Shell;

/// <summary>Outcome of the service-boot Explorer takeover phase.</summary>
internal enum BootTakeoverResult
{
    /// <summary>Explorer exited safely and game-mode shell resources were created.</summary>
    EnteredGameMode,

    /// <summary>The original desktop stayed intact and only the boot cover must be removed.</summary>
    DesktopPreserved,

    /// <summary>The exit boundary is uncertain and the verified desktop restoration must run.</summary>
    DesktopRestoreRequired
}
