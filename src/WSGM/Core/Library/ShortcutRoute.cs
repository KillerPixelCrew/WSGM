using System;
using System.Collections.Generic;
using System.IO;

namespace WSGM.Core;

/// <summary>What a generated non-Steam shortcut points at.</summary>
/// <param name="Target">The Target Steam stores, quoted when it needs to be.</param>
/// <param name="StartDirectory">The working directory Steam stores.</param>
/// <param name="LaunchOptions">The Launch Arguments Steam stores.</param>
public sealed record ShortcutFields(
    string Target,
    string StartDirectory,
    string LaunchOptions);

/// <summary>One way a launcher's title can be started, as an exact command.</summary>
/// <param name="Id">
///     Stable identity of the route within its title, such as <c>direct</c> or <c>launcher</c>. Stored
///     with the user's choice and the record, so it never changes meaning.
/// </param>
/// <param name="Label">What to call it on screen.</param>
/// <param name="Target">The program to run, unquoted.</param>
/// <param name="StartDirectory">Its working directory, unquoted.</param>
/// <param name="LaunchOptions">Its arguments, exactly as Steam should store them.</param>
/// <param name="Evidence">Why this route works, and what it cannot do, in one sentence.</param>
/// <param name="FollowDirectory">
///     The game's install folder when the program only hands the request to a launcher: the shortcut
///     then runs <c>WSGM.PackagedLaunch --follow</c>, which starts the program and stays alive while a
///     process runs from this folder. Empty for a program that is the game.
/// </param>
/// <param name="FollowMarker">
///     A path the game's own command line carries, for a game that runs from a shared runtime such as
///     Java: a Minecraft instance's folder. Empty otherwise.
/// </param>
public sealed record ShortcutRoute(
    string Id,
    string Label,
    string Target,
    string StartDirectory,
    string LaunchOptions,
    string Evidence,
    string FollowDirectory = "",
    string FollowMarker = "")
{
    /// <summary>Whether the shortcut runs through the follow launcher rather than the program itself.</summary>
    public bool Follows => FollowDirectory.Length > 0 || FollowMarker.Length > 0;
}

/// <summary>The command route family: shortcuts that run a program with fixed arguments.</summary>
/// <remarks>
///     <para>
///         Every source but Xbox launches this way. A command shortcut has no WSGM marker in it, so
///         ownership is exact equality with what was written: the record's fields for an imported
///         title, and one of the title's own routes for adopting an entry nobody recorded.
///     </para>
///     <para>
///         Steam quotes a Target and a start directory itself when it shows them, and some entries
///         carry the quotes in the stored value while others do not, so the program is compared
///         without them. The arguments are compared exactly, apart from surrounding whitespace.
///     </para>
/// </remarks>
public static class CommandShortcut
{
    /// <summary>Composes the values Steam stores for a route.</summary>
    /// <param name="route">The route.</param>
    /// <param name="launcher">
    ///     The follow launcher, <c>WSGM.PackagedLaunch.exe</c>, for a route that follows its game; empty
    ///     when this install has none.
    /// </param>
    /// <returns>The fields, the program and directory quoted when they contain a space.</returns>
    /// <exception cref="ArgumentException">The route follows its game and there is no launcher.</exception>
    /// <remarks>
    ///     A route that follows its game runs the launcher, which starts the route's program and stays
    ///     alive while the game runs. Steam then keeps the title running, with its Steam Input layout,
    ///     for as long as the game is up rather than for the second the handoff takes.
    /// </remarks>
    public static ShortcutFields Compose(ShortcutRoute route, string launcher)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(launcher);
        if (route.Follows)
        {
            if (launcher.Length == 0)
            {
                throw new ArgumentException(
                    "This route follows its game through WSGM.PackagedLaunch, which is missing.", nameof(launcher));
            }

            var options = PackagedLaunchCommand.ComposeFollow(new PackagedFollowRequest(
                route.Target, route.LaunchOptions, route.FollowDirectory, route.FollowMarker));
            return new ShortcutFields(
                Quote(launcher), Quote(Path.GetDirectoryName(launcher) ?? string.Empty), options);
        }

        var directory = route.StartDirectory.Length > 0
            ? route.StartDirectory
            : Path.GetDirectoryName(route.Target) ?? string.Empty;
        return new ShortcutFields(Quote(route.Target), Quote(directory), route.LaunchOptions);
    }

    /// <summary>Whether a live shortcut runs exactly this route.</summary>
    /// <param name="shortcut">The shortcut Steam reports.</param>
    /// <param name="route">The route.</param>
    /// <param name="launcher">The follow launcher, or empty when this install has none.</param>
    /// <returns>True when the program and the arguments agree with what the route composes.</returns>
    public static bool Runs(ExistingShortcut shortcut, ShortcutRoute route, string launcher)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        ArgumentNullException.ThrowIfNull(route);
        if (route.Follows && launcher.Length == 0)
        {
            return false;
        }

        var fields = Compose(route, launcher);
        return Same(shortcut, fields.Target, fields.LaunchOptions);
    }

    /// <summary>Which of a title's routes a live shortcut runs.</summary>
    /// <param name="shortcut">The shortcut Steam reports.</param>
    /// <param name="routes">The title's routes.</param>
    /// <param name="launcher">The follow launcher, or empty when this install has none.</param>
    /// <returns>The matching route, or null.</returns>
    public static ShortcutRoute? RouteOf(
        ExistingShortcut shortcut, IReadOnlyList<ShortcutRoute> routes, string launcher)
    {
        ArgumentNullException.ThrowIfNull(routes);
        foreach (var route in routes)
        {
            if (Runs(shortcut, route, launcher))
            {
                return route;
            }
        }

        return null;
    }

    /// <summary>Whether a live shortcut still runs what a record says was written.</summary>
    /// <param name="shortcut">The shortcut Steam reports.</param>
    /// <param name="target">The Target that was written.</param>
    /// <param name="launchOptions">The arguments that were written.</param>
    /// <returns>True when the program and the arguments agree.</returns>
    public static bool Same(ExistingShortcut shortcut, string target, string launchOptions)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        return SameProgram(shortcut.Target, target)
               && string.Equals(shortcut.LaunchOptions.Trim(), launchOptions.Trim(), StringComparison.Ordinal);
    }

    /// <summary>Whether two Target values name the same program, ignoring the quoting.</summary>
    /// <param name="left">One Target.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when they name the same file.</returns>
    public static bool SameProgram(string left, string right)
    {
        return string.Equals(
            left.Trim().Trim('"'), right.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Quotes a path Steam stores verbatim, and only when it needs it.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The path, quoted when it contains a space and is not quoted already.</returns>
    public static string Quote(string path)
    {
        return path.Length > 0 && path.Contains(' ') && !path.StartsWith('"')
            ? $"\"{path}\""
            : path;
    }
}
