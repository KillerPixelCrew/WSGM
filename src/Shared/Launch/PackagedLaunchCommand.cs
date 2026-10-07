// Shared between WSGM and WSGM.PackagedLaunch (linked as a source file), so the importer and the
// launcher cannot drift. No Log, no ConfigStore, explicit usings.

using System;
using System.Collections.Generic;
using System.IO;
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

/// <summary>One parsed request to start a launcher's game and stay alive for as long as it runs.</summary>
/// <param name="Program">The launcher program to start, absolute and unquoted.</param>
/// <param name="Arguments">Its arguments, verbatim.</param>
/// <param name="Directory">The game's install folder: a process running from inside it is the game.</param>
/// <param name="Marker">
///     A path the game's command line carries, for a game that runs from a shared runtime such as
///     Java: a Minecraft instance's folder. Empty when the install folder is enough.
/// </param>
internal sealed record PackagedFollowRequest(
    string Program,
    string Arguments,
    string Directory,
    string Marker)
{
    /// <summary>The runtime images whose command line is read for <see cref="Marker" />.</summary>
    /// <remarks>
    ///     <c>--marker</c> is defined as "the Java process whose command line carries this path", so the
    ///     images belong to the command's vocabulary rather than to the rule that matches processes.
    ///     Only Java is read: every process a marker would match is contained in a kill-on-close job,
    ///     and an editor or file manager opened on the instance folder must never be one of them.
    /// </remarks>
    internal IReadOnlyList<string> MarkerImages => Marker.Length > 0 ? PackagedLaunchCommand.JavaImages : [];
}

/// <summary>What the launcher was asked to do, which is not always to launch something.</summary>
internal enum PackagedLaunchAction
{
    /// <summary>Resolve and launch one durable managed content record.</summary>
    Managed,

    /// <summary>Activate and supervise a packaged title.</summary>
    Launch,

    /// <summary>Start another launcher's game and supervise it.</summary>
    Follow,

    /// <summary>Release package-lifetime exemptions left behind by a launcher that was killed.</summary>
    Recover,

    /// <summary>Print the usage text.</summary>
    Help
}

/// <summary>One parsed command line, or the reason it was refused.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Request">The launch request, when <see cref="Action" /> is a launch.</param>
/// <param name="Follow">The follow request, when <see cref="Action" /> is a follow.</param>
/// <param name="ManagedIdentity">The durable managed record id.</param>
internal sealed record PackagedLaunchCommandLine(
    PackagedLaunchAction Action,
    PackagedLaunchRequest? Request = null,
    PackagedFollowRequest? Follow = null,
    string? ManagedIdentity = null);

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
    internal const string ManagedFlag = "--managed";

    private const string AumidFlag = "--aumid";
    private const string ModeFlag = "--mode";
    private const string ArgumentsFlag = "--args";
    private const string MultiplayerFlag = "--multiplayer";
    private const string AcknowledgeFlag = "--acknowledge-ban-risk";
    private const string DiagnosticsFlag = "--diagnostics";
    private const string ReportPrivilegesFlag = "--report-privileges";
    private const string RecoverFlag = "--recover";
    private const string FollowFlag = "--follow";
    private const string DirectoryFlag = "--dir";
    private const string MarkerFlag = "--marker";
    private const string EndOfOptions = "--";
    private const string SteamOverlayValue = "steam-overlay";
    private const string ControllerOnlyValue = "controller-only";

    /// <summary>The Windows command-line limit, including its terminating NUL.</summary>
    internal const int WindowsCommandLineLimit = WindowsCommandLine.MaximumLength;

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
                                    --managed <id>           Resolve one imported ROM or portable title from its
                                                             durable WSGM content record.
                                    --follow [--dir <folder>] [--marker <path>] -- "<program>" <arguments>
                                                             Start another launcher's game and stay alive while
                                                             it runs. The game is the process running from
                                                             <folder>, or the Java process whose command line
                                                             carries <path>; at least one of the two is
                                                             required, and neither may be a drive root. The
                                                             program's arguments follow -- and are passed on
                                                             exactly as written. Nothing is injected.
                                    --help                   Show this text.

                                  The launch route is decided from the activated process, not from this command line:
                                  a package can be updated after its shortcut was written.
                                  """;

    /// <summary>The Java runtime's images, the processes a <c>--marker</c> is looked for in.</summary>
    internal static readonly IReadOnlyList<string> JavaImages = ["java.exe", "javaw.exe"];

    internal static bool ValidManagedId(string id)
    {
        return id.Length == 32 && Guid.TryParseExact(id, "N", out _);
    }

    internal static string ComposeManagedTarget(string launcher, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcher);
        if (!ValidManagedId(id))
        {
            throw new ArgumentException("The managed content identity must be a 32-character hexadecimal id.",
                nameof(id));
        }

        var target = WindowsCommandLine.Quote(launcher, true) + " " + ManagedFlag + " " + WindowsCommandLine.Quote(id);
        if (CommandLineRefusal(target, "") is { } refusal)
        {
            throw new ArgumentException(refusal, nameof(launcher));
        }

        return target;
    }

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

        var arguments = composed.ToString();
        if (CommandLineRefusal(string.Empty, arguments) is { } tooLong)
        {
            throw new ArgumentException(tooLong, nameof(request));
        }

        return arguments;
    }

    /// <summary>Builds the Launch Arguments for a shortcut that follows another launcher's game.</summary>
    /// <param name="request">The request to encode.</param>
    /// <returns>The argument string. The program's own arguments follow <c>--</c> verbatim.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    /// <exception cref="ArgumentException">The request could not be encoded.</exception>
    /// <remarks>
    ///     <para>
    ///         A launcher's arguments can carry quotes of their own - Battle.net's
    ///         <c>--exec="launch Pro"</c> does - which no quoting of a single value survives. So they go
    ///         last, after <c>--</c>, and the launcher hands them on untouched.
    ///     </para>
    ///     <para>
    ///         The folder and the marker are written without a trailing backslash. Launchers store
    ///         install paths either way, and under Windows' own argument rules a backslash before the
    ///         closing quote escapes it, which swallows everything after it. Trimming here, once, is
    ///         what keeps every source from having to remember it.
    ///     </para>
    /// </remarks>
    internal static string ComposeFollow(PackagedFollowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (FollowRefusal(request) is { } refusal)
        {
            throw new ArgumentException(refusal, nameof(request));
        }

        request = Normalized(request);
        StringBuilder composed = new();
        Append(composed, FollowFlag, null);
        if (request.Directory.Length > 0)
        {
            AppendQuoted(composed, DirectoryFlag, request.Directory);
        }

        if (request.Marker.Length > 0)
        {
            AppendQuoted(composed, MarkerFlag, request.Marker);
        }

        composed.Append(' ').Append(EndOfOptions).Append(" \"").Append(request.Program).Append('"');
        if (request.Arguments.Trim().Length > 0)
        {
            composed.Append(' ').Append(request.Arguments.Trim());
        }

        var arguments = composed.ToString();
        if (CommandLineRefusal(string.Empty, arguments) is { } tooLong)
        {
            throw new ArgumentException(tooLong, nameof(request));
        }

        return arguments;
    }

    /// <summary>Parses a follow command line from the raw string, keeping the program's arguments intact.</summary>
    /// <param name="raw">
    ///     Everything after this launcher's own path, exactly as Windows passed it: the process's
    ///     <c>GetCommandLineW</c>, never an argument array rebuilt from the split one.
    /// </param>
    /// <param name="request">The request, when this returns true.</param>
    /// <param name="error">Why it was refused, when this returns false.</param>
    /// <returns>Whether the command line is a follow request this understands.</returns>
    internal static bool TryParseFollow(string raw, out PackagedFollowRequest request, out string? error)
    {
        ArgumentNullException.ThrowIfNull(raw);
        request = new PackagedFollowRequest(string.Empty, string.Empty, string.Empty, string.Empty);
        var split = EndOfOptionsIndex(raw);
        if (split < 0)
        {
            error = $"{FollowFlag} needs {EndOfOptions} and the program to start.";
            return false;
        }

        var options = Tokenize(raw[..split]);
        var tail = raw[(split + EndOfOptions.Length)..].TrimStart();
        string? directory = null;
        string? marker = null;
        var follow = false;
        for (var index = 0; index < options.Count; index++)
        {
            switch (options[index])
            {
                case FollowFlag:
                    follow = true;
                    break;
                case DirectoryFlag:
                    if (!TryTake(options, ref index, DirectoryFlag, ref directory, out error))
                    {
                        return false;
                    }

                    break;
                case MarkerFlag:
                    if (!TryTake(options, ref index, MarkerFlag, ref marker, out error))
                    {
                        return false;
                    }

                    break;
                default:
                    error = $"Unknown option '{options[index]}' for {FollowFlag}.";
                    return false;
            }
        }

        if (!follow)
        {
            error = $"{FollowFlag} is required before {EndOfOptions}.";
            return false;
        }

        string program;
        string arguments;
        if (tail.StartsWith('"'))
        {
            var close = tail.IndexOf('"', 1);
            if (close < 0)
            {
                error = "The program's path is not closed with a quote.";
                return false;
            }

            program = tail[1..close];
            arguments = tail[(close + 1)..].Trim();
        }
        else
        {
            var space = tail.IndexOf(' ');
            program = space < 0 ? tail : tail[..space];
            arguments = space < 0 ? string.Empty : tail[(space + 1)..].Trim();
        }

        request = new PackagedFollowRequest(program, arguments, directory ?? string.Empty, marker ?? string.Empty);
        error = FollowRefusal(request);
        if (error is not null)
        {
            return false;
        }

        // A shortcut written before the composer trimmed its folders still carries the backslash,
        // which is harmless once the raw line is read, and is read back in the one form.
        request = Normalized(request);
        return true;
    }

    /// <summary>Why a follow request cannot be run, or null when it can.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The refusal, naming the condition and the value that failed it.</returns>
    /// <remarks>
    ///     Judged on the folders as <see cref="ComposeFollow" /> writes them, without a trailing
    ///     backslash, so a launcher that stores <c>C:\Games\Foo\</c> is accepted as <c>C:\Games\Foo</c>.
    /// </remarks>
    internal static string? FollowRefusal(PackagedFollowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Program.IndexOf('"') >= 0)
        {
            return $"The program to start, '{request.Program}', contains a quote.";
        }

        if (!Path.IsPathFullyQualified(request.Program))
        {
            return $"The program to start, '{request.Program}', is not an absolute path.";
        }

        if (!request.Program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return $"The program to start, '{request.Program}', is not an .exe.";
        }

        if (request.Directory.Length == 0 && request.Marker.Length == 0)
        {
            return $"{FollowFlag} needs {DirectoryFlag}, {MarkerFlag} or both, or the game cannot be recognised.";
        }

        foreach (var (flag, path) in new[] { (DirectoryFlag, request.Directory), (MarkerFlag, request.Marker) })
        {
            if (path.Length > 0 && PathRefusal(flag, path) is { } refusal)
            {
                return refusal;
            }
        }

        return null;
    }

    /// <summary>Why a folder given to <c>--dir</c> or <c>--marker</c> cannot identify a game, or null.</summary>
    private static string? PathRefusal(string flag, string path)
    {
        if (path.IndexOf('"') >= 0)
        {
            return $"{flag} '{path}' contains a quote.";
        }

        var trimmed = TrimSeparators(path);
        if (trimmed.Length == 0 || (trimmed.Length == 2 && trimmed[1] == ':'))
        {
            // Every process on the drive runs from inside it, so the whole drive would be contained
            // and killed with the game.
            return $"{flag} '{path}' is a drive root, which cannot tell one game from everything else on it.";
        }

        return Path.IsPathFullyQualified(trimmed) ? null : $"{flag} '{path}' is not an absolute folder path.";
    }

    /// <summary>The request with its folder and marker in the one form the command writes.</summary>
    private static PackagedFollowRequest Normalized(PackagedFollowRequest request)
    {
        return request with
        {
            Directory = TrimSeparators(request.Directory),
            Marker = TrimSeparators(request.Marker)
        };
    }

    private static string TrimSeparators(string path)
    {
        return path.TrimEnd('\\', '/');
    }

    /// <summary>Where the first <c>--</c> outside quotes starts, or -1.</summary>
    private static int EndOfOptionsIndex(string raw)
    {
        var quoted = false;
        for (var index = 0; index < raw.Length - 1; index++)
        {
            if (raw[index] == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted || raw[index] != '-' || raw[index + 1] != '-')
            {
                continue;
            }

            var startsToken = index == 0 || char.IsWhiteSpace(raw[index - 1]);
            var endsToken = index + 2 >= raw.Length || char.IsWhiteSpace(raw[index + 2]);
            if (startsToken && endsToken)
            {
                return index;
            }
        }

        return -1;
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
        if (arguments.Count > 0 && arguments[0] == ManagedFlag)
        {
            if (arguments.Count != 2 || !ValidManagedId(arguments[1]))
            {
                error = "The managed launch identity is invalid or has unexpected arguments.";
                return false;
            }

            command = new PackagedLaunchCommandLine(PackagedLaunchAction.Managed,
                ManagedIdentity: arguments[1]);
            return true;
        }

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

    internal static string? CommandLineRefusal(string quotedTarget, string arguments)
    {
        var length = (long)quotedTarget.Length + arguments.Length + 1
                     + (quotedTarget.Length > 0 && arguments.Length > 0 ? 1 : 0);
        return length > WindowsCommandLineLimit
            ? $"The full command line exceeds Windows' {WindowsCommandLineLimit}-character limit, including its terminating NUL."
            : null;
    }

    /// <summary>Whether a string is shaped like an application user model id.</summary>
    /// <param name="aumid">The candidate.</param>
    /// <returns>True when both halves are present and non-empty.</returns>
    internal static bool ValidAumid(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
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

    private static void AppendQuoted(StringBuilder composed, string flag, string value)
    {
        composed.Append(' ').Append(flag).Append(" \"").Append(value).Append('"');
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
