namespace WSGM.Shell;

/// <summary>What the registrations at one card path need.</summary>
internal enum LibraryTransition
{
    /// <summary>Steam's view already matches the card that is in the reader.</summary>
    None,

    /// <summary>
    ///     Registrations exist for a library that is not on this volume any
    ///     more; remove them and add nothing.
    /// </summary>
    Purge,

    /// <summary>
    ///     Registrations exist for a DIFFERENT card, and the card now in the
    ///     reader carries its own library; replace them.
    /// </summary>
    Replace,

    /// <summary>The card carries a library Steam does not know about; add it.</summary>
    Add
}
