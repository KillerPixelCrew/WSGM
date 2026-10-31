using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Core;

/// <summary>
///     Points a game at <c>WSGM.Launch.exe</c> without the user editing anything by hand, by
///     writing the launch configuration of the <em>running</em> Steam client through
///     <see cref="SteamApps" />.
/// </summary>
/// <remarks>
///     <para>
///         Two different writes, because Steam treats the two kinds of entry differently. A real
///         title takes launch options, where <c>%command%</c> expands to the game's own command
///         line. A non-Steam shortcut ignores an exe-replacing launch option entirely and runs its
///         original Target anyway, so there the wrapper goes into the Target and the real program
///         moves into the Launch Arguments.
///     </para>
///     <para>
///         The start directory is deliberately never written: the game's own folder has to stay
///         the working directory.
///     </para>
/// </remarks>
public static class SteamLaunchConfig
{
    /// <summary>Reads a game's current launch configuration from the running client.</summary>
    /// <param name="steam">The session's Steam client.</param>
    /// <param name="appId">The Steam app id, or a non-Steam shortcut's generated id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    ///     The details, or <see langword="null" /> when Steam is unreachable or
    ///     does not know the id.
    /// </returns>
    public static async Task<SteamAppDetails?> ReadAsync(
        SteamClient steam, long appId, CancellationToken cancellationToken = default)
    {
        var result = await steam.Apps.ReadDetailsAsync(
                SteamApps.NormalizeAppId(appId), cancellationToken)
            .ConfigureAwait(false);
        if (result is not { Succeeded: true, Value: { } details })
        {
            Log.Warn($"Could not read launch configuration for {appId}: "
                     + $"{result.Error ?? "unknown error"}.");
            return null;
        }

        return details;
    }

    /// <summary>Points a game at the launch wrapper.</summary>
    /// <param name="steam">The session's Steam client.</param>
    /// <param name="appId">The Steam app id, or a non-Steam shortcut's generated id.</param>
    /// <param name="isShortcut">Whether the id is a non-Steam shortcut.</param>
    /// <param name="mode">Which wrapper behaviours to enable.</param>
    /// <param name="current">The game's current configuration, from <see cref="ReadAsync" />.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether Steam accepted the change.</returns>
    public static async Task<LaunchConfigResult> ApplyAsync(
        SteamClient steam,
        long appId,
        bool isShortcut,
        LaunchWrapperMode mode,
        SteamAppDetails current,
        CancellationToken cancellationToken = default)
    {
        var helper = LaunchWrapperCommand.HelperPathForCurrentDeployment();
        if (!File.Exists(helper))
        {
            return new LaunchConfigResult(false, "The launch wrapper is missing from this install.");
        }

        Task<SteamClientWriteResult> write;
        if (isShortcut)
        {
            // Re-applying to an already-wrapped shortcut must not wrap the wrapper:
            // the original program lives in the arguments by then, not the Target.
            var (target, arguments) = LaunchWrapperCommand.TargetsHelper(current.ShortcutExe)
                ? LaunchWrapperCommand.OriginalFromWrappedArguments(current.ShortcutLaunchOptions)
                : (current.ShortcutExe, current.ShortcutLaunchOptions);
            if (string.IsNullOrWhiteSpace(target))
            {
                return new LaunchConfigResult(
                    false, "Steam did not report what this shortcut points at.");
            }

            var wrapped = LaunchWrapperCommand.WrapShortcut(helper, mode, target, arguments);
            write = steam.Apps.SetShortcutLaunchAsync(
                SteamApps.NormalizeAppId(appId), wrapped.Target, wrapped.Arguments, cancellationToken);
        }
        else
        {
            // Real titles only (%command% is meaningless on a non-Steam shortcut —
            // see the shortcut branch above). Re-applying reads the user's own
            // options back out of the existing wrapper value instead of nesting it.
            var originals = LaunchWrapperCommand.OriginalLaunchOptions(current.LaunchOptions);
            // Diagnosability, not a warning: a profiler/RTSS shim ahead of %command%
            // is a supported configuration, so this is what a healthy apply looks
            // like. It is logged because the prefix keeps running at Steam's own
            // integrity level in front of the wrapper, and a pasted wsgm.log is the
            // only way to see that from here. Emitted BEFORE the evaluate, so the
            // wording claims nothing about the outcome. The prefix is bounded and
            // control-character stripped by PreservedPrefix; the value handed to
            // SteamLaunchOptions is untouched, because Steam stores it verbatim.
            var prefix = LaunchWrapperCommand.PreservedPrefix(originals);
            if (prefix.Length > 0)
            {
                Log.Info(
                    $"Applying launch options for {appId} with a user-placed prefix ahead of " +
                    $"%command%, preserved and run at Steam's integrity before the wrapper: {prefix}");
            }

            write = steam.Apps.SetLaunchOptionsAsync(
                SteamApps.NormalizeAppId(appId),
                LaunchWrapperCommand.SteamLaunchOptions(helper, mode, originals),
                cancellationToken);
        }

        return Interpret(await write.ConfigureAwait(false), "Applied. Launch the game from Steam as usual.");
    }

    /// <summary>Replaces a game's launch action using Steam's native fields.</summary>
    /// <param name="steam">The session's Steam client.</param>
    /// <param name="appId">The Steam app id or generated shortcut id.</param>
    /// <param name="isShortcut">Whether the app is a non-Steam shortcut.</param>
    /// <param name="path">The selected executable or script.</param>
    /// <param name="arguments">Verbatim custom arguments for the selected action.</param>
    /// <param name="cancellationToken">Cancels the Steam operation.</param>
    /// <returns>Whether Steam accepted the native launch fields.</returns>
    public static async Task<LaunchConfigResult> ApplyCustomAsync(
        SteamClient steam, long appId, bool isShortcut, string path, string arguments,
        CancellationToken cancellationToken = default)
    {
        var fields = SteamCustomLaunchCommand.Build(path, arguments);
        var app = SteamApps.NormalizeAppId(appId);
        var write = isShortcut
            ? steam.Apps.SetShortcutLaunchAsync(
                app, fields.ShortcutTarget, fields.ShortcutArguments, cancellationToken)
            : steam.Apps.SetLaunchOptionsAsync(app, fields.LaunchOptions, cancellationToken);
        return Interpret(await write.ConfigureAwait(false), "Applied. Launch the game from Steam as usual.");
    }

    /// <summary>Restores the launch configuration a game had before WSGM changed it.</summary>
    /// <param name="steam">The session's Steam client.</param>
    /// <param name="snapshot">What was recorded when the wrapper was applied.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether Steam accepted the change.</returns>
    public static async Task<LaunchConfigResult> RestoreAsync(
        SteamClient steam, LaunchWrapperConfig snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var app = SteamApps.NormalizeAppId(snapshot.AppId);
        Task<SteamClientWriteResult> write;
        if (snapshot.IsShortcut)
        {
            if (string.IsNullOrWhiteSpace(snapshot.OriginalTarget))
            {
                return new LaunchConfigResult(
                    false, "The original program for this shortcut was not recorded.");
            }

            // Written back exactly as Steam reported it, quotes included: Steam stores these
            // verbatim, so anything else changes the shortcut.
            write = steam.Apps.SetShortcutLaunchAsync(
                app, snapshot.OriginalTarget, snapshot.OriginalLaunchOptions, cancellationToken);
        }
        else
        {
            write = steam.Apps.SetLaunchOptionsAsync(
                app, snapshot.OriginalLaunchOptions, cancellationToken);
        }

        return Interpret(
            await write.ConfigureAwait(false), "Removed. The game launches the way it did before.");
    }

    private static LaunchConfigResult Interpret(SteamClientWriteResult result, string okMessage)
    {
        switch (result.Outcome)
        {
            case SteamClientWriteOutcome.Applied:
                return new LaunchConfigResult(true, okMessage, true);
            case SteamClientWriteOutcome.Unknown:
                // Sent: Steam may have applied it, so it is published as written. The snapshot that
                // can restore the game stays, and nothing writes it again on its own.
                Log.Warn($"Launch configuration change sent but Steam did not answer: {result.Error}.");
                return new LaunchConfigResult(true, okMessage);
            case SteamClientWriteOutcome.NotSent:
                // Nothing reached Steam, so nothing changed and the caller may offer it again.
                return new LaunchConfigResult(false, "Steam isn't reachable — is it running?");
            default:
                Log.Warn($"Launch configuration change failed: {result.Error}.");
                return new LaunchConfigResult(false, result.Error ?? "Steam rejected the change.");
        }
    }
}
