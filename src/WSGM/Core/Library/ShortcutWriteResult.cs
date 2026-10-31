namespace WSGM.Core;

/// <summary>What one apply attempt did.</summary>
/// <param name="AppId">The shortcut's id, or zero when nothing is known to exist.</param>
/// <param name="Confirmed">Whether the id is known to be the entry this write made or changed.</param>
/// <param name="Error">Why not, when it was not.</param>
/// <param name="Mismatch">
///     For a confirmed add, the fields Steam did not keep as written, or null when it kept them all.
///     The shortcut exists either way.
/// </param>
/// <param name="CreationMayExist">False only when an add was definitely not sent or explicitly rejected.</param>
public sealed record ShortcutWriteResult(uint AppId, bool Confirmed, string? Error, string? Mismatch = null,
    bool CreationMayExist = true);
