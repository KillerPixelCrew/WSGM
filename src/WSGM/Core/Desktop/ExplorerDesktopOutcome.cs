namespace WSGM.Core;

/// <summary>Quality of the restored desktop shell.</summary>
internal enum ExplorerDesktopOutcome
{
    /// <summary>The taskbar owner is canonical, medium-integrity, and jobless.</summary>
    Normal,

    /// <summary>A canonical current-session medium Explorer is usable through recovery only.</summary>
    Degraded,

    /// <summary>No verified usable taskbar was produced.</summary>
    Failed
}
