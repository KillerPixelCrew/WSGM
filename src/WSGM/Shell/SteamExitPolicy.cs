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
    RelaunchDesktop,
}

/// <summary>Decides what a Steam exit means, without touching Steam or the UI.
///
/// Game mode needs Steam on screen, so an exit either brings it back or shows the overlay, which is
/// the only surface left once Big Picture is gone. A desktop session has Explorer, so an exit is
/// never a reason to interrupt the user: it either quietly starts the client again or does
/// nothing.</summary>
public static class SteamExitPolicy
{
    /// <summary>Chooses the reaction to one observed Steam exit.</summary>
    /// <param name="inGameMode">Whether the session currently runs game mode.</param>
    /// <param name="autoRelaunch">Whether the user wants Steam kept running.</param>
    /// <param name="monitorPaused">Whether a transition is in flight.</param>
    /// <param name="closedByUser">Whether the user closed Steam deliberately.</param>
    /// <returns>The reaction to apply.</returns>
    public static SteamExitReaction Decide(
        bool inGameMode, bool autoRelaunch, bool monitorPaused, bool closedByUser)
    {
        if (monitorPaused || closedByUser)
        {
            return SteamExitReaction.Ignore;
        }
        if (!autoRelaunch)
        {
            return inGameMode ? SteamExitReaction.ShowOverlay : SteamExitReaction.Ignore;
        }
        return inGameMode ? SteamExitReaction.RelaunchBigPicture : SteamExitReaction.RelaunchDesktop;
    }
}
