using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Pure projection that never carries a previous application's identity forward.</summary>
/// <remarks>
///     One Steam identity takes precedence. Foreground evidence can supply its validated executable,
///     or supply an independent identity when Steam names no application. Multiple Steam identities
///     remain ambiguous so policy cannot target the wrong game.
/// </remarks>
internal static class RunningApplicationTargetProjection
{
    /// <summary>The identity a running Steam app, and every per-game profile for it, is known by.</summary>
    /// <param name="appId">The app id, a shortcut's included.</param>
    /// <returns><c>steam:</c> and the unsigned id.</returns>
    /// <remarks>
    ///     One spelling for both sides: a profile written under another would never match the running
    ///     title it was written for.
    /// </remarks>
    internal static string SteamIdentity(uint appId)
    {
        return "steam:" + appId.ToString(CultureInfo.InvariantCulture);
    }

    /// <param name="current">The snapshot in force.</param>
    /// <param name="observation">What Steam reports.</param>
    /// <param name="profile">Steam's executable/install-folder resolution for the named AppID.</param>
    /// <param name="foreground">What the user has in front of them.</param>
    /// <param name="rendering">
    ///     The applications RTSS has hooked and is currently drawing frames for. The second, independent
    ///     proof that a foreground process is the game — see <see cref="ValidatedGameExecutable" />.
    /// </param>
    /// <summary>Combines Steam and validated foreground evidence, preserving the revision when projected content is unchanged.</summary>
    /// <returns>The existing snapshot when equivalent, otherwise a snapshot with its local generation incremented.</returns>
    internal static RunningApplicationTargetSnapshot Apply(
        RunningApplicationTargetSnapshot current,
        SteamRunningAppsObservation observation,
        SteamRunningAppProfile? profile,
        ForegroundApplicationObservation? foreground = null,
        IReadOnlyList<RtssFrametimeSample>? rendering = null)
    {
        var candidate = Project(observation, profile);
        candidate = ApplyForeground(current, candidate, profile, foreground, rendering);
        if (Equivalent(current, candidate))
        {
            return current;
        }

        return candidate with { Generation = current.Generation + 1 };
    }

    /// <summary>
    ///     Supplies the executable profile Steam omitted without replacing Steam's canonical identity.
    /// </summary>
    private static RunningApplicationTargetSnapshot ApplyForeground(
        RunningApplicationTargetSnapshot current,
        RunningApplicationTargetSnapshot steam,
        SteamRunningAppProfile? profile,
        ForegroundApplicationObservation? foreground,
        IReadOnlyList<RtssFrametimeSample>? rendering)
    {
        if (steam.State is RunningApplicationTargetState.IdentityOnly
            && current.State is RunningApplicationTargetState.Active
            && current.SteamAppId == steam.SteamAppId
            && string.Equals(current.ApplicationId, steam.ApplicationId, StringComparison.Ordinal)
            && current.RtssProfileName is { Length: > 0 })
        {
            // Steam does not expose a launch executable for ordinary store applications. Once one
            // has been supplied, keep the pairing across focus changes — an alt-tab changes focus,
            // not the game whose per-application policy is active. A DIFFERENT executable proven to
            // run from the game's own install folder may still take the pairing over: that is a
            // launcher handing off to the real game process.
            if (ValidatedGameExecutable(profile, foreground, rendering) is { } handoff
                && !string.Equals(
                    handoff.Name,
                    current.RtssProfileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return steam with
                {
                    State = RunningApplicationTargetState.Active,
                    ExecutablePath = handoff.Path,
                    RtssProfileName = handoff.Name,
                    Diagnostic = null
                };
            }

            return steam with
            {
                State = RunningApplicationTargetState.Active,
                ExecutablePath = current.ExecutablePath,
                RtssProfileName = current.RtssProfileName,
                Diagnostic = null
            };
        }

        if (steam.State is not (RunningApplicationTargetState.Global
                or RunningApplicationTargetState.IdentityOnly)
            || foreground?.ExecutableName is not { Length: > 0 } executable)
        {
            return steam;
        }

        // Ambiguous must stay ambiguous, and Unavailable means the Steam observation itself failed,
        // where publishing an identity would claim knowledge WSGM does not have.
        var profileName = executable.Trim();
        if (ForegroundApplicationFilter.Classify(profileName)
                is not ForegroundApplicationKind.Application
            || !profileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return steam;
        }

        if (steam.State is not RunningApplicationTargetState.IdentityOnly)
        {
            return steam with
            {
                State = RunningApplicationTargetState.Active,
                ApplicationId = $"process:{profileName.ToLowerInvariant()}",
                SteamAppId = null,
                ExecutablePath = null,
                RtssProfileName = profileName,
                Diagnostic = null
            };
        }

        // Steam's own game. The foreground is whatever the user happens to have in focus — a
        // terminal, a browser — so a bare name must never become the game's profile: that
        // pairing is sticky for the whole run, and WindowsTerminal.exe captured HITMAN 3's
        // frame limit exactly this way (device-observed 2026-09-02). A store title's install
        // folder is known, so only an executable proven to run from it may pair. A shortcut has
        // no folder to check and its target resolution already names the executable, so its
        // rare unresolved case keeps the name-based fill.
        if (steam.SteamAppId is not { } appId
            || SteamApps.IsShortcutAppId(appId))
        {
            return steam with
            {
                State = RunningApplicationTargetState.Active,
                ExecutablePath = null,
                RtssProfileName = profileName,
                Diagnostic = null
            };
        }

        if (ValidatedGameExecutable(profile, foreground, rendering) is not { } game)
        {
            return steam;
        }

        return steam with
        {
            State = RunningApplicationTargetState.Active,
            ExecutablePath = game.Path,
            RtssProfileName = game.Name,
            Diagnostic = null
        };
    }

    /// <summary>
    ///     The foreground executable, if and only if something proves it is the game.
    /// </summary>
    /// <param name="profile">Steam's resolution for the running AppID.</param>
    /// <param name="foreground">The process the user has in front of them.</param>
    /// <param name="rendering">Applications RTSS is currently drawing frames for.</param>
    /// <returns>The proven executable, or null when nothing proves one.</returns>
    /// <remarks>
    ///     Two independent proofs, either of which is enough, because a bare foreground NAME is not one:
    ///     that pairing is sticky for the whole run, and <c>WindowsTerminal.exe</c> captured HITMAN 3's
    ///     frame limit exactly that way (Claw, 2026-09-02).
    ///     <para>
    ///         The first is Steam's own install folder — see <see cref="SteamRunningAppProfile.InstallFolder" />.
    ///         It covers every title Steam installed and is checked first because it costs nothing.
    ///     </para>
    ///     <para>
    ///         The second is RTSS: the process is one the limiter has hooked and is currently drawing frames
    ///         for. That is the only evidence available for a title Steam names but does not manage — Skyrim
    ///         SE launched through Mod Organizer reports an empty install folder, empty launch options and no
    ///         local content, so the folder proof can never be satisfied and its per-application profile
    ///         could never be written (Claw, 2026-09-04). It is also the more meaningful of the two here: an
    ///         RTSS profile for a process RTSS is not rendering would do nothing whatever, so this proof
    ///         admits exactly the processes the feature can act on. Waterfox, Mod Organizer, GameBar and
    ///         RustDesk all held focus during that run and none of them is hooked.
    ///     </para>
    ///     <para>
    ///         Matched on process id. RTSS records its own name for an entry and this must not depend on
    ///         what format that is.
    ///     </para>
    /// </remarks>
    private static (string Name, string Path)? ValidatedGameExecutable(
        SteamRunningAppProfile? profile,
        ForegroundApplicationObservation? foreground,
        IReadOnlyList<RtssFrametimeSample>? rendering)
    {
        if (foreground?.ExecutablePath is not { Length: > 0 } path)
        {
            return null;
        }

        var name = (foreground.ExecutableName ?? string.Empty).Trim();
        if (ForegroundApplicationFilter.Classify(name) is not ForegroundApplicationKind.Application
            || name.Length is 0
            || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            // An unparsable path proves nothing; without proof there is no pairing.
            return null;
        }

        return RunsFromInstallFolder(profile, fullPath) || IsRenderingUnderRtss(foreground, rendering)
            ? (name, fullPath)
            : null;
    }

    /// <summary>Whether the candidate runs from inside the install folder Steam named.</summary>
    private static bool RunsFromInstallFolder(SteamRunningAppProfile? profile, string fullPath)
    {
        if (profile?.InstallFolder is not { Length: > 0 } folder)
        {
            return false;
        }

        try
        {
            var fullFolder = Path.GetFullPath(folder);
            var prefix = fullFolder.EndsWith(Path.DirectorySeparatorChar)
                ? fullFolder
                : fullFolder + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Whether RTSS is currently drawing frames for the candidate process.</summary>
    private static bool IsRenderingUnderRtss(
        ForegroundApplicationObservation foreground,
        IReadOnlyList<RtssFrametimeSample>? rendering)
    {
        return foreground.ProcessId != 0
               && rendering is { Count: > 0 }
               && rendering.Any(sample => sample.ProcessId == foreground.ProcessId);
    }

    private static RunningApplicationTargetSnapshot Project(
        SteamRunningAppsObservation observation,
        SteamRunningAppProfile? profile)
    {
        if (!observation.Reachable)
        {
            return new RunningApplicationTargetSnapshot(
                0,
                observation.SourceGeneration,
                RunningApplicationTargetState.Unavailable,
                null,
                null,
                null,
                null,
                observation.Diagnostic ?? "Steam running-app state is unavailable.");
        }

        var appIds = observation.AppIds.Distinct().Take(2).ToArray();
        if (appIds.Length == 0)
        {
            return new RunningApplicationTargetSnapshot(
                0,
                observation.SourceGeneration,
                RunningApplicationTargetState.Global,
                null,
                null,
                null,
                null,
                null);
        }

        if (appIds.Length != 1)
        {
            return new RunningApplicationTargetSnapshot(
                0,
                observation.SourceGeneration,
                RunningApplicationTargetState.Ambiguous,
                null,
                null,
                null,
                null,
                "Steam reports more than one running AppID; global policy remains active.");
        }

        var appId = appIds[0];
        var profileResolved = !string.IsNullOrWhiteSpace(profile?.RtssProfileName);
        return new RunningApplicationTargetSnapshot(
            0,
            observation.SourceGeneration,
            profileResolved
                ? RunningApplicationTargetState.Active
                : RunningApplicationTargetState.IdentityOnly,
            SteamIdentity(appId),
            appId,
            profile?.ExecutablePath,
            profile?.RtssProfileName,
            profile?.Diagnostic);
    }

    private static bool Equivalent(
        RunningApplicationTargetSnapshot left,
        RunningApplicationTargetSnapshot right)
    {
        return left.State == right.State
               && left.SourceGeneration == right.SourceGeneration
               && string.Equals(left.ApplicationId, right.ApplicationId, StringComparison.Ordinal)
               && left.SteamAppId == right.SteamAppId
               && string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.RtssProfileName, right.RtssProfileName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.Diagnostic, right.Diagnostic, StringComparison.Ordinal);
    }
}
