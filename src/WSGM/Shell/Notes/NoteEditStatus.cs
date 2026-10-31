namespace WSGM.Shell;

/// <summary>How a Notes edit ended.</summary>
internal enum NoteEditStatus
{
    /// <summary>The edit was saved.</summary>
    Applied,

    /// <summary>The page changed since the editor read it; nothing was saved.</summary>
    Conflict,

    /// <summary>The page or element no longer exists.</summary>
    Missing,

    /// <summary>The edit was malformed, the page is read-only, or the store failed.</summary>
    Refused
}
