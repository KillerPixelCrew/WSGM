namespace WSGM.Core;

/// <summary>How a tray request changed the icon table.</summary>
public enum TrayChange
{
    /// <summary>
    ///     The request was rejected (unknown icon, duplicate add, no hwnd) —
    ///     return 0 from WM_COPYDATA so shell32 reports failure and well-behaved apps
    ///     re-add.
    /// </summary>
    Rejected,

    /// <summary>A new icon appeared.</summary>
    Added,

    /// <summary>An existing icon changed.</summary>
    Updated,

    /// <summary>An icon was removed.</summary>
    Removed
}
