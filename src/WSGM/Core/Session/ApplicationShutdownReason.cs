namespace WSGM.Core;

/// <summary>The one process-level shutdown policy selected before Avalonia teardown begins.</summary>
internal enum ApplicationShutdownReason
{
    /// <summary>Ordinary resident exit with the normal cleanup budget.</summary>
    Normal,

    /// <summary>Installer replacement; stop within the update handoff budget.</summary>
    Update,

    /// <summary>Windows session termination with the shortest cleanup budget.</summary>
    SessionEnd,

    /// <summary>Installer removal with the longer restoration budget.</summary>
    Uninstall
}
