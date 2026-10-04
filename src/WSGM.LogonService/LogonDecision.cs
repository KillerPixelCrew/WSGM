using WSGM.Core;

namespace WSGM.LogonService;

/// <summary>What the service should do about one session logon.</summary>
internal enum LogonAction
{
    /// <summary>Launch the configured WSGM mode with the plain user token.</summary>
    Launch,

    /// <summary>Launch the configured WSGM mode with the elevated linked token.</summary>
    LaunchElevated,

    /// <summary>Both Game Mode boot and Desktop residency are disabled; leave the desktop alone.</summary>
    SkipDisabled,

    /// <summary>No usable manifest for this user — leave the desktop alone.</summary>
    SkipNoManifest,

    /// <summary>This session already got its one launch — never double-launch.</summary>
    SkipAlreadyLaunched,

    /// <summary>
    ///     Not a fresh logon: the startup catch-up found a session logged on longer ago
    ///     than the catch-up window, and covering an established desktop unasked would
    ///     be hostile.
    /// </summary>
    SkipStale
}

/// <summary>
///     Pure decision core for the logon service — everything observable is a
///     parameter so the whole table is unit-testable through the service assembly reference.
/// </summary>
internal static class LogonDecision
{
    /// <summary>Decides the action for one session.</summary>
    /// <param name="manifest">Parsed boot manifest, or null when absent/unusable.</param>
    /// <param name="alreadyLaunched">This service instance already launched into the session.</param>
    /// <param name="stale">
    ///     The startup catch-up found the session logged on longer ago than its window; always false
    ///     for a live logon event.
    /// </param>
    internal static LogonAction Decide(BootManifest? manifest, bool alreadyLaunched, bool stale)
    {
        if (alreadyLaunched)
        {
            return LogonAction.SkipAlreadyLaunched;
        }

        if (stale)
        {
            return LogonAction.SkipStale;
        }

        return manifest switch
        {
            null => LogonAction.SkipNoManifest,
            { GameModeBoot: false, DesktopResident: false } => LogonAction.SkipDisabled,
            { Elevate: true } => LogonAction.LaunchElevated,
            _ => LogonAction.Launch
        };
    }

    internal static string ArgumentsFor(BootManifest manifest)
    {
        return manifest.GameModeBoot ? "--boot" : "--shell --desktop-resident";
    }
}
