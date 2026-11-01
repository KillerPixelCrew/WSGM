namespace WSGM.Shell;

/// <summary>
///     Canonical running-application identity shared by controller and performance policy clients.
/// </summary>
/// <param name="Generation">
///     Local content revision; increments when identity, source generation, availability or
///     diagnostic evidence changes.
/// </param>
/// <param name="SourceGeneration">Generation supplied by the latest Steam observation.</param>
/// <param name="State">Availability and confidence of the projected identity.</param>
/// <param name="ApplicationId">Canonical profile identity, or null when no unambiguous application is known.</param>
/// <param name="SteamAppId">Steam identity when available; null for foreground-only applications.</param>
/// <param name="ExecutablePath">Validated executable path, or null when unresolved.</param>
/// <param name="RtssProfileName">Executable file name used for the RTSS profile, or null when unresolved.</param>
/// <param name="Diagnostic">Reason for partial or unavailable resolution, or null.</param>
internal sealed record RunningApplicationTargetSnapshot(
    long Generation,
    long SourceGeneration,
    RunningApplicationTargetState State,
    string? ApplicationId,
    uint? SteamAppId,
    string? ExecutablePath,
    string? RtssProfileName,
    string? Diagnostic)
{
    /// <summary>Creates the unavailable snapshot used before observation starts.</summary>
    /// <returns>A snapshot with zero generations and no application identity.</returns>
    internal static RunningApplicationTargetSnapshot Initial()
    {
        return new RunningApplicationTargetSnapshot(
            0,
            0,
            RunningApplicationTargetState.Unavailable,
            null,
            null,
            null,
            null,
            "Running-application observation has not started.");
    }
}
