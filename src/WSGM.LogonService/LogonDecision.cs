// The test project links this file and compiles it with ImplicitUsings (where
// this using is redundant); the service project has no ImplicitUsings and
// requires it. The pragma keeps both compilations warning-clean.
#pragma warning disable IDE0005
using System;
#pragma warning restore IDE0005
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

    /// <summary>Not a fresh logon (inactive session, or the startup catch-up found a
    /// session logged on longer ago than the catch-up window) — covering an
    /// established desktop unasked would be hostile.</summary>
    SkipStale,
}

/// <summary>Pure decision core for the logon service — everything observable is a
/// parameter so the whole table is unit-testable from the test project (which
/// links this file).</summary>
internal static class LogonDecision
{
    /// <summary>Decides the action for one session.</summary>
    /// <param name="manifest">Parsed boot manifest, or null when absent/unusable.</param>
    /// <param name="sessionActive">The session is WTSActive.</param>
    /// <param name="alreadyLaunched">This service instance already launched into the session.</param>
    /// <param name="logonAge">Time since logon (startup catch-up), or null for a live logon event.</param>
    /// <param name="staleAfter">Catch-up window; older logons are stale.</param>
    internal static LogonAction Decide(
        BootManifest? manifest, bool sessionActive, bool alreadyLaunched,
        TimeSpan? logonAge, TimeSpan staleAfter)
    {
        if (alreadyLaunched)
        {
            return LogonAction.SkipAlreadyLaunched;
        }
        if (!sessionActive || (logonAge is { } age && age > staleAfter))
        {
            return LogonAction.SkipStale;
        }
        if (manifest is null)
        {
            return LogonAction.SkipNoManifest;
        }
        if (!manifest.GameModeBoot && !manifest.DesktopResident)
        {
            return LogonAction.SkipDisabled;
        }
        return manifest.Elevate ? LogonAction.LaunchElevated : LogonAction.Launch;
    }

    internal static string ArgumentsFor(BootManifest manifest) =>
        manifest.GameModeBoot ? "--boot" : "--shell --desktop-resident";
}
