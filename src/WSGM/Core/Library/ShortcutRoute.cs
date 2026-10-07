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
/// <param name="StartDirectory">
///     Its working directory, unquoted, or empty for the program's own folder. A route that follows its
///     game must leave it empty or name the program's own folder: the follow launcher starts the program
///     there, and <see cref="CommandShortcut.TryCompose" /> refuses a route that asks for anything else
///     rather than dropping it.
/// </param>
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
/// <param name="ManagedId">The durable content record referenced by the shared managed launch helper.</param>
public sealed record ShortcutRoute(
    string Id,
    string Label,
    string Target,
    string StartDirectory,
    string LaunchOptions,
    string Evidence,
    string FollowDirectory = "",
    string FollowMarker = "",
    string ManagedId = "")
{
    /// <summary>The evidence every route that starts the game's own executable shares.</summary>
    public const string DirectEvidence =
        "Starts the game's own executable, so Steam's overlay and controller support reach it.";

    /// <summary>Whether the shortcut runs through the follow launcher rather than the program itself.</summary>
    public bool Follows => FollowDirectory.Length > 0 || FollowMarker.Length > 0;

    /// <summary>Whether this one route resolves an authoritative managed launch plan.</summary>
    public bool IsManaged => Id == "managed" && ManagedId.Length > 0;

    /// <summary>The evidence every route that starts a game through its launcher and follows it shares.</summary>
    /// <param name="launcher">What to call the launcher.</param>
    /// <returns>One sentence the review shows.</returns>
    public static string FollowedLauncherEvidence(string launcher)
    {
        return $"Starts through {launcher}. WSGM follows the game, so Steam shows it running and keeps its "
               + "controller layout for as long as it runs; Steam's overlay may not reach it.";
    }

    /// <summary>A route that runs a launcher program, which starts the game.</summary>
    /// <param name="program">The launcher's program, unquoted.</param>
    /// <param name="arguments">Its arguments, exactly as Steam should store them.</param>
    /// <param name="label">What to call the route.</param>
    /// <param name="evidence">Why it works and what it cannot do.</param>
    /// <param name="followDirectory">The game's install folder to follow, or empty to track the launcher.</param>
    /// <returns>The <c>launcher</c> route, started in the program's own folder.</returns>
    public static ShortcutRoute ThroughLauncher(
        string program, string arguments, string label, string evidence, string followDirectory)
    {
        ArgumentNullException.ThrowIfNull(program);
        return new ShortcutRoute(
            "launcher", label, program, Path.GetDirectoryName(program) ?? string.Empty, arguments, evidence,
            followDirectory);
    }

    /// <summary>A route that opens a launcher's URI through the program the scheme is registered to.</summary>
    /// <param name="command">The resolved URI command.</param>
    /// <param name="label">What to call the route.</param>
    /// <param name="evidence">Why it works and what it cannot do.</param>
    /// <param name="followDirectory">The game's install folder to follow, or empty to track the launcher.</param>
    /// <returns>The <c>launcher</c> route.</returns>
    public static ShortcutRoute ThroughLauncher(
        ProtocolCommand command, string label, string evidence, string followDirectory)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ThroughLauncher(command.Program, command.Arguments, label, evidence, followDirectory);
    }
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
    /// <summary>Composes the values Steam stores for a route, or says why it cannot be.</summary>
    /// <param name="route">The route.</param>
    /// <param name="launcherTarget">
    ///     The follow launcher, <c>WSGM.PackagedLaunch.exe</c>, for a route that follows its game; empty
    ///     when this install has none.
    /// </param>
    /// <param name="fields">The fields on success, all empty otherwise.</param>
    /// <param name="refusal">Empty on success, otherwise one sentence naming the condition.</param>
    /// <returns>Whether the route could be composed.</returns>
    /// <remarks>
    ///     Planning composes every title's every route to recognise the shortcuts already in Steam, so
    ///     one route a launcher registered oddly, such as a drive-root install folder, must refuse
    ///     itself here rather than throw and fail the whole scan.
    /// </remarks>
    public static bool TryCompose(
        ShortcutRoute route, string launcherTarget, out ShortcutFields fields, out string refusal)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(launcherTarget);
        fields = new ShortcutFields("", "", "");
        if (route.IsManaged)
        {
            if (launcherTarget.Length == 0)
            {
                refusal = "The WSGM content launcher is missing from this installation.";
                return false;
            }

            try
            {
                fields = new ShortcutFields(PackagedLaunchCommand.ComposeManagedTarget(launcherTarget, route.ManagedId),
                    Quote(Path.GetDirectoryName(launcherTarget) ?? ""), "");
            }
            catch (ArgumentException ex)
            {
                refusal = ex.Message;
                return false;
            }

            refusal = "";
            return true;
        }

        if (!route.Follows)
        {
            var directory = route.StartDirectory.Length > 0
                ? route.StartDirectory
                : Path.GetDirectoryName(route.Target) ?? string.Empty;
            fields = new ShortcutFields(Quote(route.Target), Quote(directory), route.LaunchOptions);
            if (PackagedLaunchCommand.CommandLineRefusal(fields.Target, fields.LaunchOptions) is { } directLimitFailure)
            {
                refusal = directLimitFailure;
                return false;
            }

            refusal = string.Empty;
            return true;
        }

        if (launcherTarget.Length == 0)
        {
            refusal = "This route follows its game through WSGM.PackagedLaunch, which is missing.";
            return false;
        }

        if (route.StartDirectory.Length > 0
            && !SameFolder(route.StartDirectory, Path.GetDirectoryName(route.Target) ?? string.Empty))
        {
            refusal = "This route follows its game, and the follow launcher starts "
                      + $"{Path.GetFileName(route.Target)} in its own folder rather than in {route.StartDirectory}.";
            return false;
        }

        PackagedFollowRequest request = new(
            route.Target, route.LaunchOptions, route.FollowDirectory, route.FollowMarker);
        if (PackagedLaunchCommand.FollowRefusal(request) is { } refused)
        {
            refusal = refused;
            return false;
        }

        string options;
        try
        {
            options = PackagedLaunchCommand.ComposeFollow(request);
        }
        catch (ArgumentException ex)
        {
            refusal = ex.Message;
            return false;
        }

        fields = new ShortcutFields(
            Quote(launcherTarget), Quote(Path.GetDirectoryName(launcherTarget) ?? string.Empty), options);
        if (PackagedLaunchCommand.CommandLineRefusal(fields.Target, fields.LaunchOptions) is { } followLimitFailure)
        {
            refusal = followLimitFailure;
            return false;
        }

        refusal = string.Empty;
        return true;
    }

    /// <summary>Whether a live shortcut runs exactly this route.</summary>
    /// <param name="shortcut">The shortcut Steam reports.</param>
    /// <param name="route">The route.</param>
    /// <param name="launcher">The follow launcher, or empty when this install has none.</param>
    /// <returns>
    ///     True when the program and the arguments agree with what the route composes; false for a route
    ///     that cannot be composed at all.
    /// </returns>
    /// <remarks>
    ///     A route that follows its game is compared by what its launch options ask the follow launcher
    ///     to do rather than by their spelling, so a shortcut an older release wrote, with a trailing
    ///     separator on a folder the composer now trims, is still recognised as the same route.
    /// </remarks>
    public static bool Runs(ExistingShortcut shortcut, ShortcutRoute route, string launcher)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        ArgumentNullException.ThrowIfNull(route);
        if (!TryCompose(route, launcher, out var fields, out _))
        {
            return false;
        }

        if (route.IsManaged || !route.Follows)
        {
            return Same(shortcut, fields.Target, fields.LaunchOptions)
                   && SameFolder(shortcut.StartDirectory, fields.StartDirectory);
        }

        return SameProgram(shortcut.Target, fields.Target)
               && SameFolder(shortcut.StartDirectory, fields.StartDirectory)
               && PackagedLaunchCommand.TryParseFollow(shortcut.LaunchOptions.Trim(), out var live, out _)
               && SameProgram(live.Program, route.Target)
               && string.Equals(live.Arguments.Trim(), route.LaunchOptions.Trim(), StringComparison.Ordinal)
               && SameFolder(live.Directory, route.FollowDirectory)
               && SameFolder(live.Marker, route.FollowMarker);
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

    /// <summary>Compares directories after unquoting and trimming only a trailing separator.</summary>
    public static bool SameFolder(string left, string right)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(left.Trim().Trim('"')),
            Path.TrimEndingDirectorySeparator(right.Trim().Trim('"')),
            StringComparison.OrdinalIgnoreCase);
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
