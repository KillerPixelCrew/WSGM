namespace WSGM.Overlay;

/// <summary>Stable top-level destinations in the quick access sheet, in strip order.</summary>
internal enum OverlayDestination
{
    /// <summary>The pinned rows — the sheet's home and the Back target of every other root.</summary>
    QuickAccess,
    Steam,
    Device,

    /// <summary>System and storage tools (labelled "Tools").</summary>
    System,

    /// <summary>Wake, idle timeouts and the session-ending actions.</summary>
    Power
}
