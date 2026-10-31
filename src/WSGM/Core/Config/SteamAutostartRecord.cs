namespace WSGM.Core;

/// <summary>
///     One Steam startup entry WSGM turned off, and how to put it back.
///     An install-lifecycle recovery snapshot like the registry ones beside it: feature code never
///     clobbers these, and the uninstaller reads them to undo exactly what WSGM changed.
/// </summary>
public sealed class SteamAutostartRecord
{
    /// <summary>What kind of startup source this was.</summary>
    public SteamAutostartKind Kind { get; set; }

    /// <summary>Whether the source is per-user or machine-wide.</summary>
    public SteamAutostartScope Scope { get; set; }

    /// <summary>Registry key, folder or task path the source lived in.</summary>
    public string Location { get; set; } = "";

    /// <summary>Value, file or task name inside that location.</summary>
    public string Name { get; set; } = "";

    /// <summary>Whether a run value lives in the 32-bit registry view.</summary>
    public bool Wow64 { get; set; }

    /// <summary>Whether Windows had stored an approval state before WSGM wrote one.</summary>
    public bool PreviousApprovalExists { get; set; }

    /// <summary>The approval bytes as they were, base64-encoded; null when there were none.</summary>
    public string? PreviousApproval { get; set; }

    /// <summary>
    ///     The approval bytes WSGM wrote, base64-encoded. Restore only undoes a state that
    ///     still carries these, so a decision the user made afterwards is never overwritten.
    /// </summary>
    public string? WrittenApproval { get; set; }

    /// <summary>Whether disable intent was recorded but an accepted write was not durably acknowledged.</summary>
    public bool Pending { get; set; }
}
