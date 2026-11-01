using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One source in the sidebar.</summary>
/// <param name="Id">Its stable identity.</param>
/// <param name="Name">What to call it.</param>
/// <param name="Kind"><c>launcher</c>, <c>folder</c>, <c>rom</c> or <c>manual</c>.</param>
/// <param name="Installed">Whether it was found on this machine.</param>
/// <param name="Enabled">Whether the user has it ticked.</param>
/// <param name="Detail">What was found, or why not.</param>
/// <param name="Count">How many titles the last scan found in it, or -1 when it was not scanned.</param>
/// <param name="Checked">The host-selected checkbox state, including unavailable configured sources.</param>
/// <param name="CanToggle">Whether the host permits changing the source's enabled intent.</param>
public sealed record GameLibrarySource(
    string Id,
    string Name,
    LibrarySourceKind Kind,
    bool Installed,
    bool Enabled,
    string Detail,
    int Count,
    bool Checked = false,
    bool CanToggle = false);
