// The test project links this file and compiles it with ImplicitUsings (where
// this using is redundant); the service project has no ImplicitUsings and
// requires it. The pragma keeps both compilations warning-clean.

using WSGM.Core;
#pragma warning disable IDE0005
// ReSharper disable once RedundantUsingDirective
using System;

#pragma warning restore IDE0005

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

    /// <summary>
    ///     Setup staged a controller-driver update, and this boot belongs to it. Starting WSGM
    ///     would attach the virtual pad and wedge the upgrade, which is the whole reason the
    ///     <c>DriverUpdateGate</c> exists.
    /// </summary>
    SkipDriverUpdate,

    /// <summary>This session already got its one launch — never double-launch.</summary>
    SkipAlreadyLaunched,

    /// <summary>
    ///     Not a fresh logon (inactive session, or the startup catch-up found a
    ///     session logged on longer ago than the catch-up window) — covering an
    ///     established desktop unasked would be hostile.
    /// </summary>
    SkipStale
}

/// <summary>
///     Pure decision core for the logon service — everything observable is a
///     parameter so the whole table is unit-testable from the test project (which
///     links this file).
/// </summary>
internal static class LogonDecision
{
    /// <summary>Decides the action for one session.</summary>
    /// <param name="manifest">Parsed boot manifest, or null when absent/unusable.</param>
    /// <param name="sessionActive">The session is WTSActive.</param>
    /// <param name="alreadyLaunched">This service instance already launched into the session.</param>
    /// <param name="logonAge">Time since logon (startup catch-up), or null for a live logon event.</param>
    /// <param name="staleAfter">Catch-up window; older logons are stale.</param>
    /// <param name="driverUpdateStaged">Setup is waiting for a boot with nothing attached.</param>
    internal static LogonAction Decide(
        BootManifest? manifest, bool sessionActive, bool alreadyLaunched,
        TimeSpan? logonAge, TimeSpan staleAfter, bool driverUpdateStaged = false)
    {
        if (driverUpdateStaged)
        {
            // Ahead of every other test, including the already-launched one: a second session
            // logging on must not start WSGM either, or the boot setup asked for is spent.
            return LogonAction.SkipDriverUpdate;
        }

        if (alreadyLaunched)
        {
            return LogonAction.SkipAlreadyLaunched;
        }

        if (!sessionActive || (logonAge is { } age && age > staleAfter))
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
