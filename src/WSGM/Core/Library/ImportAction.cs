namespace WSGM.Core;

/// <summary>What a sync would do to one title.</summary>
public enum ImportAction
{
    /// <summary>Create a new non-Steam shortcut.</summary>
    Add,

    /// <summary>Rewrite an existing entry WSGM created, because its command line changed.</summary>
    Update,

    /// <summary>Record an entry that already launches this title but WSGM did not record.</summary>
    Adopt,

    /// <summary>Nothing to do.</summary>
    Skip,

    /// <summary>Remove an entry WSGM created for a title that is no longer installed.</summary>
    Remove,

    /// <summary>The entry has been changed by hand. Never touched without confirmation.</summary>
    Conflict
}
