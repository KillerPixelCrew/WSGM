using System;
using System.IO;

namespace WSGM.Core;

/// <summary>What a generated non-Steam shortcut points at.</summary>
/// <param name="Target">The Target Steam stores, quoted when it needs to be.</param>
/// <param name="StartDirectory">The working directory Steam stores.</param>
/// <param name="LaunchOptions">The Launch Arguments Steam stores.</param>
public sealed record PackagedLauncherShortcutFields(
    string Target,
    string StartDirectory,
    string LaunchOptions);

/// <summary>The packaged launch route: the only code that knows how its shortcut is written.</summary>
/// <remarks>
///     <para>
///         A non-Steam shortcut ignores an exe-replacing launch option and runs its Target anyway —
///         the device-verified rule in <c>docs\elevation.md</c> that <see cref="SteamLaunchConfig" />
///         already relies on. So the wrapper goes in the Target and the real identity in the Launch
///         Arguments, exactly as the launch wrapper does for an ordinary game.
///     </para>
///     <para>
///         The working directory is the launcher's own, never the package's. <c>WindowsApps</c> is
///         ACL'd, and a working directory a process cannot read is a plausible way to fail a launch
///         for no benefit — the launcher does not read the game's files anyway.
///     </para>
/// </remarks>
public static class PackagedLauncherShortcut
{
    /// <summary>The launcher's file name.</summary>
    public const string ExecutableName = "WSGM.PackagedLaunch.exe";

    /// <summary>Resolves the launcher beside the running WSGM.</summary>
    /// <returns>Its absolute path, or null when this deployment has no launcher.</returns>
    /// <remarks>
    ///     Null is a refusal to import, not something to work around: a shortcut pointing at a
    ///     program that is not there fails when the user presses play, which is the worst moment to
    ///     find out.
    /// </remarks>
    public static string? ResolveLauncher()
    {
        try
        {
            var directory = Path.GetDirectoryName(Environment.ProcessPath);
            var path = Path.Combine(
                string.IsNullOrEmpty(directory) ? Installer.InstallDir : directory, ExecutableName);
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Composes the three values Steam stores for one title.</summary>
    /// <param name="launcherPath">The launcher's absolute path.</param>
    /// <param name="aumid">The packaged application to launch.</param>
    /// <param name="mode">Which route it launches with.</param>
    /// <param name="multiplayer">Whether the title is known to have multiplayer.</param>
    /// <param name="acknowledged">Whether the user accepted the ban risk.</param>
    /// <returns>The Target, working directory and arguments, all stored verbatim by Steam.</returns>
    /// <exception cref="ArgumentException">The request could not be composed.</exception>
    public static PackagedLauncherShortcutFields Compose(
        string launcherPath,
        string aumid,
        ImportMode mode,
        bool multiplayer,
        bool acknowledged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);
        var arguments = PackagedLaunchCommand.Compose(new PackagedLaunchRequest(
            aumid,
            mode is ImportMode.SteamIntegration
                ? PackagedLaunchMode.SteamOverlay
                : PackagedLaunchMode.ControllerOnly,
            multiplayer,
            acknowledged));

        var directory = Path.GetDirectoryName(launcherPath) ?? string.Empty;
        return new PackagedLauncherShortcutFields(Quote(launcherPath), Quote(directory), arguments);
    }

    /// <summary>Whether a live shortcut is one WSGM created for this title.</summary>
    /// <param name="shortcut">The shortcut Steam reports.</param>
    /// <param name="launcherTarget">The Target a generated entry carries.</param>
    /// <param name="key">The title's identity.</param>
    /// <returns>True only when both the Target and the key in the arguments agree.</returns>
    /// <remarks>
    ///     Both halves must agree. A Target alone would match every title WSGM imported, and a key
    ///     alone would match an entry somebody wrote by hand. The key is read as the launch
    ///     request's own argument rather than searched for in the string, so a title whose key
    ///     contains another as a prefix cannot claim it.
    /// </remarks>
    public static bool Owns(ExistingShortcut shortcut, string launcherTarget, string key)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        return SameProgram(shortcut.Target, launcherTarget)
               && TryReadKey(shortcut.LaunchOptions, out var owned)
               && string.Equals(owned, key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the title's key back out of a generated shortcut's arguments.</summary>
    /// <param name="launchOptions">The Launch Arguments Steam stores.</param>
    /// <param name="key">The key, when this returns true.</param>
    /// <returns>Whether the arguments are a launch request this route composed.</returns>
    public static bool TryReadKey(string launchOptions, out string key)
    {
        key = PackagedLaunchCommand.TryDescribe(launchOptions, out var request) ? request.Aumid : string.Empty;
        return key.Length > 0;
    }

    /// <summary>Reads the input mode back out of a generated shortcut's arguments.</summary>
    /// <param name="launchOptions">The Launch Arguments Steam stores.</param>
    /// <param name="mode">The mode, when this returns true.</param>
    /// <returns>Whether the arguments are a launch request this route composed.</returns>
    public static bool TryReadMode(string launchOptions, out ImportMode mode)
    {
        mode = ImportMode.ControllerOnly;
        if (!PackagedLaunchCommand.TryDescribe(launchOptions, out var request))
        {
            return false;
        }

        mode = request.Mode is PackagedLaunchMode.SteamOverlay
            ? ImportMode.SteamIntegration
            : ImportMode.ControllerOnly;
        return true;
    }

    /// <summary>Whether two Target values name the same program, ignoring the quoting.</summary>
    private static bool SameProgram(string left, string right)
    {
        return string.Equals(
            left.Trim().Trim('"'), right.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Quotes a path Steam stores verbatim, and only when it needs it.</summary>
    private static string Quote(string path)
    {
        return path.Length > 0 && path.Contains(' ') && !path.StartsWith('"')
            ? $"\"{path}\""
            : path;
    }
}
