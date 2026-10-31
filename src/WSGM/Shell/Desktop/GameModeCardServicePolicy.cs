namespace WSGM.Shell;

/// <summary>Which card services belong to the current shell mode.</summary>
/// <param name="WatchAppManifests">Whether card manifest directory watches should be active.</param>
/// <param name="ReconcileSteamLibraries">Whether volume changes should be watched for live Steam library reconciliation.</param>
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
    ///     Manifest watches hold directory handles and belong to Game Mode. Library reconciliation
    ///     follows the CEF master switch in either mode; its own readiness gate controls when it acts.
    /// </remarks>
    /// <returns>Independent manifest-watch and library-reconciliation gates; both are false in overlay preview mode.</returns>
    internal static GameModeCardServiceState Decide(
        bool gameModeActive, bool overlayTestOnly, bool cefMasterEnabled)
    {
        var watchCards = gameModeActive && !overlayTestOnly;
        return new GameModeCardServiceState(
            watchCards,
            !overlayTestOnly && cefMasterEnabled);
    }
}
