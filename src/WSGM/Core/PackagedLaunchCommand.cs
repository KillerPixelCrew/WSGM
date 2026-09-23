using System;
using System.Collections.Generic;
using System.Text;

namespace WSGM.Core;

/// <summary>What a generated shortcut asks the packaged-game launcher to do about input.</summary>
internal enum PackagedLaunchMode
{
    /// <summary>Give the real game Steam's overlay and Steam Input.</summary>
    SteamOverlay,

    /// <summary>Switch the managed controller to Xbox 360 and inject nothing.</summary>
    ControllerOnly
}

/// <summary>One parsed packaged-game launch request.</summary>
/// <param name="Aumid">The application user model id to activate.</param>
/// <param name="Mode">Which input route the user chose.</param>
/// <param name="Multiplayer">Whether the title is known to carry a multiplayer tag.</param>
/// <param name="AcknowledgedBanRisk">Whether the user accepted the risk for a multiplayer title.</param>
/// <param name="GameArguments">Arguments handed to the activated application, or null.</param>
/// <param name="Diagnostics">Whether attended-only extra diagnostics are enabled.</param>
/// <param name="ReportPrivileges">Whether to report the access the game actually grants, once.</param>
internal sealed record PackagedLaunchRequest(
    string Aumid,
    PackagedLaunchMode Mode,
    bool Multiplayer = false,
    bool AcknowledgedBanRisk = false,
    string? GameArguments = null,
    bool Diagnostics = false,
    bool ReportPrivileges = false)
{
    /// <summary>The package family half of the AUMID, which identifies the game's processes.</summary>
    internal string PackageFamilyName
    {
        get
        {
            var separator = Aumid.IndexOf('!');
            return separator < 0 ? Aumid : Aumid[..separator];
        }
    }
}

/// <summary>What the launcher was asked to do, which is not always to launch something.</summary>
internal enum PackagedLaunchAction
{
    /// <summary>Activate and supervise a packaged title.</summary>
    Launch,

    /// <summary>Release package-lifetime exemptions left behind by a launcher that was killed.</summary>
    Recover,

    /// <summary>Print the usage text.</summary>
    Help
}

/// <summary>One parsed command line, or the reason it was refused.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Request">The launch request, when <see cref="Action" /> is a launch.</param>
internal sealed record PackagedLaunchCommandLine(
    PackagedLaunchAction Action,
    PackagedLaunchRequest? Request = null);

/// <summary>
///     The command line the library importer writes into a generated shortcut and the packaged-game
///     launcher parses back out of it.
/// </summary>
/// <remarks>
///     <para>
///         One file so the two cannot drift: composing and parsing are the same vocabulary, and a
///         round-trip test pins them together. It is compiled into WSGM and linked into
///         <c>WSGM.PackagedLaunch</c>, the way <c>ScheduledTaskXml</c> is shared with
///         <c>WSGM.Launch</c>.
///     </para>
///     <para>
///         There is deliberately no runtime argument. Which launch route a title needs is decided
///         from the activated process — its token and its package layout — rather than trusted from a
///         shortcut that may have been written before a package update. See
///         <c>docs\packaged-game-launcher.md</c>.
///     </para>
/// </remarks>
internal static class PackagedLaunchCommand
{
    private const string AumidFlag = "--aumid";
    private const string ModeFlag = "--mode";
    private const string ArgumentsFlag = "--args";
    private const string MultiplayerFlag = "--multiplayer";
    private const string AcknowledgeFlag = "--acknowledge-ban-risk";
    private const string DiagnosticsFlag = "--diagnostics";
    private const string ReportPrivilegesFlag = "--report-privileges";
    private const string RecoverFlag = "--recover";
    private const string SteamOverlayValue = "steam-overlay";
    private const string ControllerOnlyValue = "controller-only";

    /// <summary>The longest AUMID this accepts, which is well past any Windows produces.</summary>
    internal const int MaximumAumidLength = 512;

    /// <summary>The longest game argument string this accepts.</summary>
    internal const int MaximumArgumentsLength = 2048;

    /// <summary>The usage text, printed for <c>--help</c> and for a refused command line.</summary>
    internal const string Usage = """
                                  WSGM.PackagedLaunch - launches an Xbox/MSIX game and keeps Steam's session alive.

                                    --aumid <family!app>     The packaged application to activate. Required.
                                    --mode <m>               steam-overlay | controller-only. Required.
                                    --multiplayer            The title carries a multiplayer tag.
                                    --acknowledge-ban-risk   The user accepted the risk of the overlay route for a
                                                             multiplayer title. Required to combine --multiplayer
                                                             with --mode steam-overlay.
                                    --args <text>            Arguments handed to the game.
                                    --diagnostics            Attended-only extra diagnostics. Not for normal use.
                                    --report-privileges      Report the access the game grants, once, then continue.
                                    --recover                Release package-lifetime exemptions left by a killed
                                                             launcher, then exit. Takes no other option.
                                    --help                   Show this text.

                                  The launch route is decided from the activated process, not from this command line:
                                  a package can be updated after its shortcut was written.
                                  """;

    /// <summary>Builds the Launch Arguments for a generated non-Steam shortcut.</summary>
    /// <param name="request">The request to encode.</param>
    /// <returns>The argument string, quoted where a value needs it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    /// <exception cref="ArgumentException">The request could not be encoded.</exception>
    /// <remarks>
    ///     Steam stores the value verbatim, so this quotes what needs quoting and nothing else. A
    ///     request this refuses to compose is one the launcher would refuse to run.
    /// </remarks>
    internal static string Compose(PackagedLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidAumid(request.Aumid))
        {
            throw new ArgumentException("The AUMID must be PackageFamilyName!ApplicationId.", nameof(request));
        }

        if (request is { Multiplayer: true, Mode: PackagedLaunchMode.SteamOverlay, AcknowledgedBanRisk: false })
        {
            throw new ArgumentException(
                "A multiplayer title cannot take the overlay route without an acknowledged ban risk.",
                nameof(request));
        }

        if (request.GameArguments is { Length: > MaximumArgumentsLength })
        {
            throw new ArgumentException("The game arguments are too long.", nameof(request));
        }

        StringBuilder composed = new();
        Append(composed, AumidFlag, request.Aumid);
        Append(composed, ModeFlag, Value(request.Mode));
        if (request.Multiplayer)
        {
            Append(composed, MultiplayerFlag, null);
        }

        // Only meaningful beside --multiplayer, and only written there: a shortcut should say what
        // the user actually accepted rather than carry a standing acknowledgement.
        if (request is { Multiplayer: true, AcknowledgedBanRisk: true })
        {
            Append(composed, AcknowledgeFlag, null);
        }

        if (!string.IsNullOrEmpty(request.GameArguments))
        {
            Append(composed, ArgumentsFlag, request.GameArguments);
        }

        return composed.ToString();
    }

    /// <summary>Reads the AUMID out of a composed launch-options string.</summary>
    /// <param name="arguments">A shortcut's launch options, as Steam stores them.</param>
    /// <param name="aumid">The AUMID the options name, when this returns true.</param>
    /// <returns>Whether the options carry an AUMID.</returns>
    /// <remarks>
    ///     Ownership of a shortcut is decided on this value, so it is read as the flag's argument
    ///     rather than searched for anywhere in the string. A title whose AUMID merely contains
    ///     another as a prefix would otherwise be claimed by it, and the next sync would overwrite
    ///     or delete a shortcut the user had pointed somewhere else.
    /// </remarks>
    internal static bool TryReadAumid(string arguments, out string aumid)
    {
        aumid = TryDescribe(arguments, out var request) ? request.Aumid : string.Empty;
        return aumid.Length > 0;
    }

    /// <summary>Reads back a launch request this composed.</summary>
    /// <param name="arguments">A shortcut's launch options, as Steam stores them.</param>
    /// <param name="request">What those options ask for, when this returns true.</param>
    /// <returns>Whether the options are a launch request this understands.</returns>
    /// <remarks>
    ///     The same parser the launcher runs, so what a shortcut is read as here is exactly what it
    ///     will do. That matters for adoption: an entry already in Steam has to be taken over as
    ///     what it currently launches, not as what the current default would have written.
    /// </remarks>
    internal static bool TryDescribe(string arguments, out PackagedLaunchRequest request)
    {
        request = new PackagedLaunchRequest(string.Empty, PackagedLaunchMode.ControllerOnly);
        if (!TryParse(Tokenize(arguments), out var command, out _)
            || command.Action is not PackagedLaunchAction.Launch
            || command.Request is null)
        {
            return false;
        }

        request = command.Request;
        return true;
    }

    /// <summary>Splits a command-line string the way <see cref="Append" /> quotes one.</summary>
    /// <param name="arguments">The raw string.</param>
    /// <returns>The tokens, with surrounding quotes removed.</returns>
    private static List<string> Tokenize(string arguments)
    {
        List<string> tokens = [];
        StringBuilder current = new();
        var quoted = false;
        var started = false;
        foreach (var character in arguments)
        {
            if (character == '"')
            {
                quoted = !quoted;
                started = true;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (started)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    started = false;
                }

                continue;
            }

            current.Append(character);
            started = true;
        }

        if (started)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>Parses one command line.</summary>
    /// <param name="arguments">The arguments, without the executable.</param>
    /// <param name="command">The parsed command, when this returns true.</param>
    /// <param name="error">Why the command line was refused, when this returns false.</param>
    /// <returns>Whether the command line was understood.</returns>
    /// <remarks>
    ///     Every refusal names the problem. Nothing defaults: an unrecognized mode is a refusal
    ///     rather than a silent fall back to one route or the other, because the two routes differ in
    ///     whether anything is injected into the game.
    /// </remarks>
    internal static bool TryParse(
        IReadOnlyList<string> arguments,
        out PackagedLaunchCommandLine command,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        command = new PackagedLaunchCommandLine(PackagedLaunchAction.Help);
        error = null;

        string? aumid = null;
        string? mode = null;
        string? gameArguments = null;
        var multiplayer = false;
        var acknowledged = false;
        var diagnostics = false;
        var reportPrivileges = false;
        var recover = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--help" or "-h" or "/?":
                    command = new PackagedLaunchCommandLine(PackagedLaunchAction.Help);
                    return true;
                case AumidFlag:
                    if (!TryTake(arguments, ref index, AumidFlag, ref aumid, out error))
                    {
                        return false;
                    }

                    break;
                case ModeFlag:
                    if (!TryTake(arguments, ref index, ModeFlag, ref mode, out error))
                    {
                        return false;
                    }

                    break;
                case ArgumentsFlag:
                    if (!TryTake(arguments, ref index, ArgumentsFlag, ref gameArguments, out error))
                    {
                        return false;
                    }

                    break;
                case MultiplayerFlag:
                    multiplayer = true;
                    break;
                case AcknowledgeFlag:
                    acknowledged = true;
                    break;
                case DiagnosticsFlag:
                    diagnostics = true;
                    break;
                case ReportPrivilegesFlag:
                    reportPrivileges = true;
                    break;
                case RecoverFlag:
                    recover = true;
                    break;
                default:
                    error = $"Unknown option '{argument}'.";
                    return false;
            }
        }

        if (recover)
        {
            if (aumid is not null || mode is not null)
            {
                error = $"{RecoverFlag} releases stale package exemptions and takes no other option.";
                return false;
            }

            command = new PackagedLaunchCommandLine(PackagedLaunchAction.Recover);
            return true;
        }

        if (aumid is null)
        {
            error = $"{AumidFlag} is required.";
            return false;
        }

        if (!ValidAumid(aumid))
        {
            error = $"'{aumid}' is not a PackageFamilyName!ApplicationId.";
            return false;
        }

        if (mode is null)
        {
            error = $"{ModeFlag} is required, and is {SteamOverlayValue} or {ControllerOnlyValue}.";
            return false;
        }

        PackagedLaunchMode parsedMode;
        switch (mode)
        {
            case SteamOverlayValue:
                parsedMode = PackagedLaunchMode.SteamOverlay;
                break;
            case ControllerOnlyValue:
                parsedMode = PackagedLaunchMode.ControllerOnly;
                break;
            default:
                error = $"'{mode}' is not a launch mode. Use {SteamOverlayValue} or {ControllerOnlyValue}.";
                return false;
        }

        if (gameArguments is { Length: > MaximumArgumentsLength })
        {
            error = "The game arguments are too long.";
            return false;
        }

        // The one refusal that protects a person rather than the process. Controller-only is always
        // allowed for a multiplayer title; the overlay route is what carries the risk.
        if (multiplayer && parsedMode is PackagedLaunchMode.SteamOverlay && !acknowledged)
        {
            error = $"This title is marked multiplayer. The overlay route injects into it, so it needs "
                    + $"{AcknowledgeFlag}, which WSGM writes only when the user accepts that risk.";
            return false;
        }

        command = new PackagedLaunchCommandLine(
            PackagedLaunchAction.Launch,
            new PackagedLaunchRequest(
                aumid,
                parsedMode,
                multiplayer,
                acknowledged,
                string.IsNullOrEmpty(gameArguments) ? null : gameArguments,
                diagnostics,
                reportPrivileges));
        return true;
    }

    /// <summary>Whether a string is shaped like an application user model id.</summary>
    /// <param name="aumid">The candidate.</param>
    /// <returns>True when both halves are present and non-empty.</returns>
    internal static bool ValidAumid(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid) || aumid.Length > MaximumAumidLength)
        {
            return false;
        }

        var separator = aumid.IndexOf('!');
        return separator > 0
               && separator < aumid.Length - 1
               && aumid.IndexOf('!', separator + 1) < 0
               && aumid.IndexOf('"') < 0
               && !aumid.Contains('\0');
    }

    private static string Value(PackagedLaunchMode mode)
    {
        return mode switch
        {
            PackagedLaunchMode.SteamOverlay => SteamOverlayValue,
            PackagedLaunchMode.ControllerOnly => ControllerOnlyValue,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown launch mode.")
        };
    }

    private static void Append(StringBuilder composed, string flag, string? value)
    {
        if (composed.Length > 0)
        {
            composed.Append(' ');
        }

        composed.Append(flag);
        if (value is null)
        {
            return;
        }

        composed.Append(' ');
        if (value.Contains(' ') || value.Length == 0)
        {
            composed.Append('"').Append(value).Append('"');
            return;
        }

        composed.Append(value);
    }

    private static bool TryTake(
        IReadOnlyList<string> arguments,
        ref int index,
        string flag,
        ref string? destination,
        out string? error)
    {
        if (destination is not null)
        {
            error = $"{flag} was given more than once.";
            return false;
        }

        if (index + 1 >= arguments.Count)
        {
            error = $"{flag} needs a value.";
            return false;
        }

        destination = arguments[++index];
        error = null;
        return true;
    }
}
