using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Outcome of a launch-configuration change.</summary>
/// <param name="Ok">Whether the change was applied.</param>
/// <param name="Detail">A user-facing note (why it failed, or what was done).</param>
public readonly record struct LaunchConfigResult(bool Ok, string Detail);

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
    /// <param name="appId">The Steam app id, or a non-Steam shortcut's generated id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    ///     The details, or <see langword="null" /> when Steam is unreachable or
    ///     does not know the id.
    /// </returns>
    public static async Task<SteamAppDetails?> ReadAsync(
        long appId, CancellationToken cancellationToken = default)
    {
        var result = await SteamApps.ReadDetailsAsync(
                SteamApps.NormalizeAppId(appId), cancellationToken)
            .ConfigureAwait(false);
        if (result.Details is not { } details)
        {
            Log.Warn($"Could not read launch configuration for {appId}: "
                     + $"{result.Error ?? "unknown error"}.");
            return null;
        }

        return details;
    }

    /// <summary>Points a game at the launch wrapper.</summary>
    /// <param name="appId">The Steam app id, or a non-Steam shortcut's generated id.</param>
    /// <param name="isShortcut">Whether the id is a non-Steam shortcut.</param>
    /// <param name="mode">Which wrapper behaviours to enable.</param>
    /// <param name="current">The game's current configuration, from <see cref="ReadAsync" />.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether Steam accepted the change.</returns>
    public static async Task<LaunchConfigResult> ApplyAsync(
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
            var original = LaunchWrapperCommand.TargetsHelper(current.ShortcutExe)
                ? OriginalFromWrappedArguments(current.ShortcutLaunchOptions)
                : (current.ShortcutExe, current.ShortcutLaunchOptions);
            if (string.IsNullOrWhiteSpace(original.Item1))
            {
                return new LaunchConfigResult(
                    false, "Steam did not report what this shortcut points at.");
            }

            write = SteamApps.SetShortcutLaunchAsync(
                SteamApps.NormalizeAppId(appId),
                LaunchWrapperCommand.ShortcutTarget(helper),
                LaunchWrapperCommand.ShortcutArguments(mode, original.Item1, original.Item2),
                cancellationToken);
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

            write = SteamApps.SetLaunchOptionsAsync(
                SteamApps.NormalizeAppId(appId),
                LaunchWrapperCommand.SteamLaunchOptions(helper, mode, originals),
                cancellationToken);
        }

        return Interpret(await write.ConfigureAwait(false), "Applied. Launch the game from Steam as usual.");
    }

    /// <summary>Replaces a game's launch action using Steam's native fields.</summary>
    /// <param name="appId">The Steam app id or generated shortcut id.</param>
    /// <param name="isShortcut">Whether the app is a non-Steam shortcut.</param>
    /// <param name="path">The selected executable or script.</param>
    /// <param name="arguments">Verbatim custom arguments for the selected action.</param>
    /// <param name="cancellationToken">Cancels the Steam operation.</param>
    /// <returns>Whether Steam accepted the native launch fields.</returns>
    public static async Task<LaunchConfigResult> ApplyCustomAsync(
        long appId, bool isShortcut, string path, string arguments,
        CancellationToken cancellationToken = default)
    {
        var fields = SteamCustomLaunchCommand.Build(path, arguments);
        var app = SteamApps.NormalizeAppId(appId);
        var write = isShortcut
            ? SteamApps.SetShortcutLaunchAsync(
                app, fields.ShortcutTarget, fields.ShortcutArguments, cancellationToken)
            : SteamApps.SetLaunchOptionsAsync(app, fields.LaunchOptions, cancellationToken);
        return Interpret(await write.ConfigureAwait(false), "Applied. Launch the game from Steam as usual.");
    }

    /// <summary>Restores the launch configuration a game had before WSGM changed it.</summary>
    /// <param name="snapshot">What was recorded when the wrapper was applied.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether Steam accepted the change.</returns>
    public static async Task<LaunchConfigResult> RestoreAsync(
        LaunchWrapperConfig snapshot, CancellationToken cancellationToken = default)
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
            write = SteamApps.SetShortcutLaunchAsync(
                app, snapshot.OriginalTarget, snapshot.OriginalLaunchOptions, cancellationToken);
        }
        else
        {
            write = SteamApps.SetLaunchOptionsAsync(
                app, snapshot.OriginalLaunchOptions, cancellationToken);
        }

        return Interpret(
            await write.ConfigureAwait(false), "Removed. The game launches the way it did before.");
    }

    /// <summary>Reports which wrapper behaviours a game is currently configured with.</summary>
    /// <param name="isShortcut">Whether the id is a non-Steam shortcut.</param>
    /// <param name="details">The game's current configuration.</param>
    /// <returns>The active behaviours, or <see cref="LaunchWrapperMode.None" />.</returns>
    public static LaunchWrapperMode ModeFor(bool isShortcut, SteamAppDetails details)
    {
        if (!isShortcut)
        {
            return LaunchWrapperCommand.ModeFor(details.LaunchOptions);
        }

        // A shortcut splits the two halves WSGM wrote: the wrapper path sits in the
        // Target and the behaviour flags in the arguments, which never repeat the
        // path. Read them as one string so the mode is recognised.
        return LaunchWrapperCommand.TargetsHelper(details.ShortcutExe)
            ? LaunchWrapperCommand.ModeFor(
                details.ShortcutExe + " " + details.ShortcutLaunchOptions)
            : LaunchWrapperMode.None;
    }

    /// <summary>
    ///     Derives what a game's launch configuration looked like before any
    ///     wrapper was written into it, so a snapshot taken for an already-wrapped game
    ///     records the real program rather than WSGM's own values. Needed whenever a
    ///     game is wrapped but WSGM holds no snapshot: the command was pasted by hand
    ///     from the clipboard fallback, or the configuration was restored/reset.
    /// </summary>
    /// <param name="isShortcut">Whether the entry is a non-Steam shortcut.</param>
    /// <param name="details">The game's current configuration, from <see cref="ReadAsync" />.</param>
    /// <returns>
    ///     The pre-wrapper target, launch options/arguments and start directory.
    ///     An unwrapped game's values are returned unchanged.
    /// </returns>
    public static (string Target, string LaunchOptions, string StartDir) OriginalsFrom(
        bool isShortcut, SteamAppDetails details)
    {
        if (ModeFor(isShortcut, details) == LaunchWrapperMode.None)
        {
            return (details.ShortcutExe, isShortcut
                ? details.ShortcutLaunchOptions
                : details.LaunchOptions, details.ShortcutStartDir);
        }

        if (!isShortcut)
        {
            return (details.ShortcutExe,
                LaunchWrapperCommand.OriginalLaunchOptions(details.LaunchOptions),
                details.ShortcutStartDir);
        }

        var (target, arguments) = OriginalFromWrappedArguments(details.ShortcutLaunchOptions);
        return (target, arguments, details.ShortcutStartDir);
    }

    /// <summary>Recovers the program a wrapped shortcut actually runs.</summary>
    /// <param name="arguments">The shortcut's current Launch Arguments.</param>
    /// <returns>
    ///     The original target and its own arguments, both empty when the value
    ///     does not look like something WSGM wrote.
    /// </returns>
    internal static (string, string) OriginalFromWrappedArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return ("", "");
        }

        // Everything after the separator is what the shortcut used to launch: a
        // (possibly quoted) program path followed by its own arguments.
        var separator = arguments.IndexOf(" -- ", StringComparison.Ordinal);
        var rest = separator < 0
            ? arguments.StartsWith("-- ", StringComparison.Ordinal) ? arguments[3..] : ""
            : arguments[(separator + 4)..];
        rest = rest.Trim();
        if (rest.Length == 0)
        {
            return ("", "");
        }

        if (rest[0] == '"')
        {
            var end = rest.IndexOf('"', 1);
            return end < 0
                ? (rest, "")
                : (rest[..(end + 1)], rest[(end + 1)..].Trim());
        }

        var space = rest.IndexOf(' ');
        return space < 0 ? (rest, "") : (rest[..space], rest[(space + 1)..].Trim());
    }

    private static LaunchConfigResult Interpret(SteamClientWriteResult result, string okMessage)
    {
        // An unreachable Steam is not a rejected change: the caller keeps the request and can
        // retry, so it must never be reported as a failure to apply.
        if (!result.Reachable)
        {
            return new LaunchConfigResult(false, "Steam isn't reachable — is it running?");
        }

        if (result.Accepted)
        {
            return new LaunchConfigResult(true, okMessage);
        }

        Log.Warn($"Launch configuration change failed: {result.Error}.");
        return new LaunchConfigResult(false, result.Error ?? "Steam rejected the change.");
    }
}
