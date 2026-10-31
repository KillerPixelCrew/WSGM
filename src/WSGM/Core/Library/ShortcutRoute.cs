using System;
using System.IO;

namespace WSGM.Core;

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
