namespace WSGM.Shell;

/// <summary>What the session does when Steam exits.</summary>
public enum SteamExitReaction
{
    /// <summary>Leave Steam closed and show nothing.</summary>
    Ignore,

    /// <summary>Show the overlay so the user can start Steam again.</summary>
    ShowOverlay,

    /// <summary>Start Steam back into Big Picture.</summary>
    RelaunchBigPicture,

    /// <summary>Start the windowed Steam client again beside the desktop.</summary>
    RelaunchDesktop
}
