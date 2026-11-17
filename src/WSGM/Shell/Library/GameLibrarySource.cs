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
/// <param name="ParentId">
///     For one system of a ROM library that detects its system folders, the library's id; empty for a source.
///     The row's own id is <see cref="SystemRowId" />, which the source switch command takes like any other.
/// </param>
public sealed record GameLibrarySource(
    string Id,
    string Name,
    LibrarySourceKind Kind,
    bool Installed,
    bool Enabled,
    string Detail,
    int Count,
    bool Checked = false,
    bool CanToggle = false,
    string ParentId = "")
{
    /// <summary>The sidebar id of one system of a ROM library.</summary>
    /// <param name="sourceId">The ROM library.</param>
    /// <param name="systemId">The canonical system.</param>
    /// <returns><c>library/system</c>; a ROM library's id never holds a slash.</returns>
    public static string SystemRowId(string sourceId, string systemId)
    {
        return sourceId + "/" + systemId;
    }

    /// <summary>Splits a system row's id into its library and system, when it is one.</summary>
    /// <param name="id">A sidebar id.</param>
    /// <param name="sourceId">The library, when this returns true.</param>
    /// <param name="systemId">The system, when this returns true.</param>
    /// <returns>Whether the id names one system of a library.</returns>
    public static bool TrySplitSystemRow(string id, out string sourceId, out string systemId)
    {
        var slash = id.IndexOf('/');
        sourceId = slash > 0 ? id[..slash] : "";
        systemId = slash > 0 ? id[(slash + 1)..] : "";
        return systemId.Length > 0;
    }
}
