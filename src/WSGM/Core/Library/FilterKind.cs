namespace WSGM.Core;

/// <summary>
///     The kind of a <see cref="FilterNode" />. Mirrors TabMaster's filter set,
///     trimmed to the core the user selected. <see cref="Merge" /> is a group node that
///     nests children under its own <see cref="FilterNode.Mode" />, giving arbitrary
///     AND/OR trees.
/// </summary>
public enum FilterKind
{
    /// <summary>Membership in a Steam collection.</summary>
    Collection,

    /// <summary>Installed / not installed.</summary>
    Installed,

    /// <summary>Has one of (any/all) the given store tags (genres).</summary>
    Tag,

    /// <summary>Title matches a regular expression.</summary>
    Regex,

    /// <summary>App id is in an explicit include list.</summary>
    Whitelist,

    /// <summary>App id is NOT in an explicit exclude list.</summary>
    Blacklist,

    /// <summary>Steam app vs non-Steam shortcut.</summary>
    Platform,

    /// <summary>Metacritic / Steam review score above or below a threshold.</summary>
    ReviewScore,

    /// <summary>Playtime above or below a threshold.</summary>
    TimePlayed,

    /// <summary>Install size above or below a threshold.</summary>
    SizeOnDisk,

    /// <summary>Original release date on/after or before a point.</summary>
    ReleaseDate,

    /// <summary>Last-played date on/after or before a point.</summary>
    LastPlayed,

    /// <summary>Installed on a MicroSD/removable card WSGM tracks.</summary>
    SdCard,

    /// <summary>A nested group of child filters combined by <see cref="FilterNode.Mode" />.</summary>
    Merge
}
