using System;

namespace WSGM.Core;

/// <summary>
///     Bitfield category flags (values match TabMaster's so the concepts
///     line up): which app kinds are candidates before the predicate runs.
/// </summary>
[Flags]
public enum LibraryFilterCategories
{
    /// <summary>Games.</summary>
    Games = 1,

    /// <summary>Software / applications.</summary>
    Software = 2,

    /// <summary>Include hidden apps (allApps vs visibleApps).</summary>
    Hidden = 16,

    /// <summary>Soundtracks / music.</summary>
    Music = 8192
}
