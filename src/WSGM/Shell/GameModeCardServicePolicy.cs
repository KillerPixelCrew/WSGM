namespace WSGM.Shell;

/// <summary>Which card services belong to the current shell mode.</summary>
internal readonly record struct GameModeCardServiceState(
    bool WatchAppManifests,
    bool ReconcileSteamLibraries);

/// <summary>Decides which card services should be alive without touching a device or Steam.</summary>
internal static class GameModeCardServicePolicy
{
    /// <summary>Returns the card services required by the current runtime gates.</summary>
    /// <param name="gameModeActive">Whether WSGM currently owns game mode.</param>
    /// <param name="overlayTestOnly">Whether the safe overlay-only mode is running.</param>
    /// <param name="cefMasterEnabled">Whether Steam CEF integration may be driven.</param>
    /// <remarks>
    /// Manifest watching stays a game-mode service: it holds directory handles on the cards for the
    /// sake of the library tabs, which are a game-mode surface. Reconciling Steam's library list is
    /// not tied to the mode any more. Steam's own storage pages are revived whenever the CEF bridge
    /// is on, in either mode, and a card pulled from the reader has to leave Steam's list whichever
    /// mode the shell is in -- otherwise Big Picture on the desktop goes on showing a library that
    /// is no longer in the machine, which is exactly what happened. The monitor itself still waits
    /// for the Big Picture window before it changes anything, so widening the gate here changes
    /// when it starts watching, not when it may act.
    /// </remarks>
    internal static GameModeCardServiceState Decide(
        bool gameModeActive, bool overlayTestOnly, bool cefMasterEnabled)
    {
        var watchCards = gameModeActive && !overlayTestOnly;
        return new GameModeCardServiceState(
            watchCards,
            !overlayTestOnly && cefMasterEnabled);
    }
}
