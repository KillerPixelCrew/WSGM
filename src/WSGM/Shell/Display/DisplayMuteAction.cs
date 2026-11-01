namespace WSGM.Shell;

/// <summary>What the display-off mute service should do after one policy input changes.</summary>
internal enum DisplayMuteAction
{
    /// <summary>The current state already satisfies the policy.</summary>
    NoChange,

    /// <summary>The screen went dark — mute if the user has not muted already.</summary>
    Mute,

    /// <summary>The screen is lit again — undo a mute this process applied.</summary>
    Restore,

    /// <summary>
    ///     The last download stopped while the screen remains dark — restore
    ///     after the completion grace period unless activity resumes.
    /// </summary>
    DelayRestore
}
