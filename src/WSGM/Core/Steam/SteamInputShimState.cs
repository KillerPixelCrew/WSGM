namespace WSGM.Core;

/// <summary>What the shim deployment looks like on disk.</summary>
internal enum SteamInputShimState
{
    /// <summary>Steam is not installed, so there is nowhere to deploy.</summary>
    SteamNotInstalled,

    /// <summary>Steam Input Management is off; any deployed copy is parked aside.</summary>
    Disabled,

    /// <summary>A WSGM-owned shim is in place and matches this build.</summary>
    Deployed,

    /// <summary>
    ///     A WSGM-owned shim is in place but stale, and Steam has it mapped so
    ///     it could not be replaced. The next cold start replaces it.
    /// </summary>
    UpdatePending,

    /// <summary>Every candidate name in Steam's directory belongs to another program.</summary>
    Blocked,

    /// <summary>The deployment could not be carried out (access denied, I/O error).</summary>
    Failed
}
