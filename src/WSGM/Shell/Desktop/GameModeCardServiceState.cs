namespace WSGM.Shell;

/// <summary>Which card services belong to the current shell mode.</summary>
/// <param name="WatchAppManifests">Whether card manifest directory watches should be active.</param>
/// <param name="ReconcileSteamLibraries">Whether volume changes should be watched for live Steam library reconciliation.</param>
internal readonly record struct GameModeCardServiceState(
    bool WatchAppManifests,
    bool ReconcileSteamLibraries);
