namespace WSGM.Shell;

/// <summary>Truthful availability of the canonical running-application target.</summary>
internal enum RunningApplicationTargetState
{
    Global,
    Active,
    IdentityOnly,
    Ambiguous,
    Unavailable
}

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

/// <summary>Optional executable/profile resolution for one known Steam AppID.</summary>
/// <param name="ExecutablePath">The shortcut target, when Steam exposes one.</param>
/// <param name="RtssProfileName">The executable file name RTSS keys its profile on.</param>
/// <param name="Diagnostic">Why the resolution is partial, for the log.</param>
/// <param name="InstallFolder">
///     A store title's install folder. Steam never exposes a store title's executable, so the folder is
///     what the foreground pairing is validated against: only a process running from inside it may
///     become the game's RTSS profile.
/// </param>
internal sealed record SteamRunningAppProfile(
    string? ExecutablePath,
    string? RtssProfileName,
    string? Diagnostic,
    string? InstallFolder = null);

/// <summary>
///     The application the user currently has in front of them, independent of Steam.
/// </summary>
/// <param name="ExecutableName">
///     File name of the foreground process with its extension, or <see langword="null" /> when nothing
///     usable is in front.
/// </param>
/// <param name="ExecutablePath">
///     Full image path of the same process, when it could be read. The projection needs it to prove a
///     candidate actually runs from a Steam title's install folder before pairing the two.
/// </param>
/// <param name="ProcessId">
///     The same process's identifier, or zero when it could not be read. It is what the RTSS rendering
///     proof matches on: comparing identifiers is exact, where comparing an image path against whatever
///     RTSS recorded in its own table is a guess about that table's format.
/// </param>
/// <remarks>
///     This is the second identity source, and it exists so per-application policy works outside a
///     Steam game: on the desktop, for a title launched from another launcher, or for anything the user
///     picks a profile for from the overlay. It never competes with Steam — see
///     <see cref="RunningApplicationTargetProjection" /> for the precedence rule.
/// </remarks>
internal sealed record ForegroundApplicationObservation(
    string? ExecutableName,
    string? ExecutablePath = null,
    uint ProcessId = 0)
{
    /// <summary>Nothing usable in the foreground.</summary>
    internal static ForegroundApplicationObservation None { get; } = new((string?)null);
}
