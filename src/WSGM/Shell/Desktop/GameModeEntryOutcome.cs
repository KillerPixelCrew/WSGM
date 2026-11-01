namespace WSGM.Shell;

/// <summary>How an attempt to enter Game Mode ended.</summary>
internal enum GameModeEntryOutcome
{
    /// <summary>Game Mode is running.</summary>
    Entered,

    /// <summary>The user cancelled and desktop recovery was attempted; recovery may remain pending.</summary>
    Cancelled,

    /// <summary>Entry failed. The desktop either never changed or went through the desktop return.</summary>
    Failed,

    /// <summary>Explorer could not be removed, so the desktop was deliberately kept.</summary>
    DesktopPreserved
}
