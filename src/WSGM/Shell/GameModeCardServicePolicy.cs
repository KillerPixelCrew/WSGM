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
    internal static GameModeCardServiceState Decide(
        bool gameModeActive, bool overlayTestOnly, bool cefMasterEnabled)
    {
        var watchCards = gameModeActive && !overlayTestOnly;
        return new GameModeCardServiceState(
            watchCards,
            watchCards && cefMasterEnabled);
    }
}
