namespace WSGM.Shell;

/// <summary>
///     What a fresh look at the target's disk number says about the media
///     sitting there now, compared with the identity the run started from.
/// </summary>
internal enum FormatTargetIdentity
{
    /// <summary>Everything that could be read still matches the picked card.</summary>
    Same,

    /// <summary>
    ///     The disk did not answer its identity queries. NOT a mismatch: a
    ///     reader whose media is momentarily not ready reports exactly this, and the
    ///     run must carry on so the existing waits and retries can still rescue it.
    /// </summary>
    Unreadable,

    /// <summary>
    ///     The disk number now belongs to something else — a different
    ///     capacity or bus, no longer removable media, or a system disk.
    /// </summary>
    Changed
}
