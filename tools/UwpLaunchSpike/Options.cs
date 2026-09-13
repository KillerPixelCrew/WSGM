using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Wsgm.UwpSpike;

internal sealed class Options
{
    internal LaunchMode Mode { get; private set; } = LaunchMode.Aam;

    internal string? Aumid { get; private set; }

    internal string? Target { get; private set; }

    internal string? GameArguments { get; private set; }

    internal List<string> Match { get; } = [];

    internal TimeSpan Settle { get; private set; } = TimeSpan.FromSeconds(90);

    internal TimeSpan ExitGrace { get; private set; } = TimeSpan.FromSeconds(5);

    internal TimeSpan Poll { get; private set; } = TimeSpan.FromSeconds(1);

    internal TimeSpan Heartbeat { get; private set; } = TimeSpan.FromSeconds(30);

    internal bool ProbeRights { get; private set; }

    internal bool HideConsole { get; private set; }

    internal bool HelpRequested { get; private set; }

    internal string LogPath { get; private set; } = string.Empty;

    internal static Options Parse(string[] args, out string? error)
    {
        var options = new Options();
        string? failure = null;
        string? logPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            string? Next(string name)
            {
                if (index + 1 >= args.Length)
                {
                    failure ??= $"{name} needs a value.";
                    return null;
                }

                return args[++index];
            }

            switch (argument)
            {
                case "--help":
                case "-h":
                case "/?":
                    options.HelpRequested = true;
                    break;
                case "--aumid":
                    options.Aumid = Next(argument);
                    break;
                case "--target":
                    options.Target = Next(argument);
                    break;
                case "--args":
                    options.GameArguments = Next(argument);
                    break;
                case "--match":
                    if (Next(argument) is { } match) { options.Match.Add(match); }
                    break;
                case "--mode":
                    if (Next(argument) is { } mode)
                    {
                        if (Enum.TryParse<LaunchMode>(mode, ignoreCase: true, out var parsed))
                        {
                            options.Mode = parsed;
                        }
                        else
                        {
                            failure ??= $"Unknown mode '{mode}'. Use aam, shell, powershell, or exe.";
                        }
                    }

                    break;
                case "--settle":
                    options.Settle = Seconds(Next(argument), options.Settle, ref failure);
                    break;
                case "--exit-grace":
                    options.ExitGrace = Seconds(Next(argument), options.ExitGrace, ref failure);
                    break;
                case "--poll":
                    options.Poll = Milliseconds(Next(argument), options.Poll, ref failure);
                    break;
                case "--heartbeat":
                    options.Heartbeat = Seconds(Next(argument), options.Heartbeat, ref failure);
                    break;
                case "--probe-rights":
                    options.ProbeRights = true;
                    break;
                case "--hide-console":
                    options.HideConsole = true;
                    break;
                case "--log":
                    logPath = Next(argument);
                    break;
                default:
                    failure ??= $"Unknown argument '{argument}'.";
                    break;
            }
        }

        if (!options.HelpRequested && failure is null)
        {
            if (options.Mode == LaunchMode.Exe)
            {
                if (string.IsNullOrWhiteSpace(options.Target))
                {
                    failure = "--mode exe needs --target with the path to an executable.";
                }
            }
            else if (string.IsNullOrWhiteSpace(options.Aumid))
            {
                failure = "--aumid with a PackageFamilyName!AppId value is required.";
            }
        }

        options.LogPath = logPath ?? DefaultLogPath(options);
        error = failure;
        return options;
    }

    /// The package family half of the AUMID, which is how the supervisor recognises the
    /// real game no matter which process the activation broker ends up creating.
    internal string? PackageFamily
    {
        get
        {
            if (string.IsNullOrEmpty(Aumid))
            {
                return null;
            }

            var separator = Aumid.IndexOf('!');
            return separator < 0 ? Aumid : Aumid[..separator];
        }
    }

    private static TimeSpan Seconds(string? value, TimeSpan fallback, ref string? error)
    {
        if (value is null) { return fallback; }
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
        {
            return TimeSpan.FromSeconds(parsed);
        }

        error ??= $"'{value}' is not a number of seconds.";
        return fallback;
    }

    private static TimeSpan Milliseconds(string? value, TimeSpan fallback, ref string? error)
    {
        if (value is null) { return fallback; }
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            return TimeSpan.FromMilliseconds(parsed);
        }

        error ??= $"'{value}' is not a number of milliseconds.";
        return fallback;
    }

    private static string DefaultLogPath(Options options)
    {
        var name = options.Mode.ToString().ToLowerInvariant();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WSGM",
            "uwp-spike",
            $"{stamp}-{name}.log");
    }

    internal const string Usage = """
        WsgmUwpSpike - Xbox/MSIX launch supervisor spike (issue 48)

        Steam launches this wrapper, the wrapper activates the packaged title, keeps
        running for the whole session, and records what happens to process lineage,
        security context, and Steam overlay injection.

          --aumid <family!app>   Packaged app to activate (required except for --mode exe).
          --target <path>        Executable for --mode exe.
          --args <text>          Arguments handed to the game.
          --mode <m>             aam (default) | shell | powershell | exe.
          --match <substring>    Extra image-name hint for finding the real game process.
          --settle <seconds>     How long to wait for the game to appear (default 90).
          --exit-grace <seconds> How long the game must be gone before exiting (default 5).
          --poll <ms>            Poll interval (default 1000).
          --heartbeat <seconds>  Idle status line interval (default 30).
          --probe-rights         Test injector-grade OpenProcess masks on the game.
          --hide-console         Hide the console window after startup.
          --log <path>           Transcript path (default %LOCALAPPDATA%\WSGM\uwp-spike).
          --help                 Show this text.
        """;
}
