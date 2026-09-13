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

    internal bool Contain { get; private set; } = true;

    internal bool Proxy { get; private set; } = true;

    /// Whether to set the environment and inject the moment activation returns a process
    /// id, rather than on the supervisor's first poll. On by default: the overlay renderer
    /// hooks device creation, so arriving after the game's swapchain exists is useless.
    internal bool Early { get; private set; } = true;

    internal List<string> Inject { get; } = [];

    internal bool NoSuspend { get; private set; } = true;

    internal bool PassSteamEnvironment { get; private set; } = true;

    /// How long to let the game initialise before its environment block is edited.
    ///
    /// Not zero, and the difference is the whole game: patching 30ms after the process
    /// appeared killed it 1.1s later, every single time, while the identical patch at 8s
    /// left it running happily. The process is still being initialised by the loader and
    /// the packaged-app runtime in that window.
    internal TimeSpan EnvironmentDelay { get; private set; } = TimeSpan.FromSeconds(8);

    /// The Steam launch variables this wrapper received, in name=value form, ready to be
    /// handed to the packaged title that the broker would otherwise start with none.
    internal List<string> SteamEnvironment { get; } = [];

    internal TimeSpan InjectDelay { get; private set; } = TimeSpan.Zero;

    /// Zero-argument exports to call inside the game after injection, as dll!Export.
    internal List<string> Call { get; } = [];

    internal bool HelpRequested { get; private set; }

    /// Name or pid of an already-running process to report on and exit. The control case:
    /// what a process the Steam overlay actually works in looks like from outside.
    internal string? Observe { get; private set; }

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
                case "--no-contain":
                    options.Contain = false;
                    break;
                case "--no-proxy":
                    options.Proxy = false;
                    break;
                case "--no-early":
                    options.Early = false;
                    break;
                case "--allow-suspend":
                    options.NoSuspend = false;
                    break;
                case "--no-steam-env":
                    options.PassSteamEnvironment = false;
                    break;
                case "--env-delay":
                    options.EnvironmentDelay = Seconds(Next(argument), options.EnvironmentDelay, ref failure);
                    break;
                case "--observe":
                    options.Observe = Next(argument);
                    break;
                case "--inject":
                    if (Next(argument) is { } dll) { options.Inject.Add(dll); }
                    break;
                case "--inject-steam-overlay":
                    options.Inject.Add(SteamOverlayPath());
                    break;
                case "--inject-steam-client":
                    // Dependencies first and by full path. LoadLibraryW searches the target
                    // process's directory, not the loaded DLL's, so steamclient64 cannot
                    // find tier0_s64 and vstdlib_s64 on its own inside a packaged game;
                    // mapping them first lets the loader satisfy the imports by name.
                    foreach (var client in SteamClientPaths())
                    {
                        options.Inject.Add(client);
                    }

                    break;
                case "--call":
                    if (Next(argument) is { } call) { options.Call.Add(call); }
                    break;
                case "--steam-api-init":
                    // The full chain a Steam build performs for itself: map the game-side
                    // shim, then make the call that loads steamclient and registers the
                    // process with the running client.
                    if (Next(argument) is { } apiDll)
                    {
                        options.Inject.Add(apiDll);
                        options.Call.Add($"{apiDll}!SteamAPI_Init");
                    }

                    break;
                case "--inject-delay":
                    options.InjectDelay = Seconds(Next(argument), options.InjectDelay, ref failure);
                    break;
                case "--log":
                    logPath = Next(argument);
                    break;
                default:
                    failure ??= $"Unknown argument '{argument}'.";
                    break;
            }
        }

        // The renderer reads the Steam session variables when it loads, so it must never be
        // injected before the environment carrying them is in place.
        if (options.Inject.Count > 0 && options.PassSteamEnvironment
            && options.InjectDelay < options.EnvironmentDelay + TimeSpan.FromSeconds(2))
        {
            options.InjectDelay = options.EnvironmentDelay + TimeSpan.FromSeconds(2);
        }

        if (!options.HelpRequested && failure is null && options.Observe is null)
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

    /// The steamclient stack in load order: its two dependencies, then itself.
    internal static IReadOnlyList<string> SteamClientPaths()
    {
        var root = SteamRoot();
        return
        [
            Path.Combine(root, "tier0_s64.dll"),
            Path.Combine(root, "vstdlib_s64.dll"),
            Path.Combine(root, "steamclient64.dll"),
        ];
    }

    private static string SteamRoot()
    {
        var steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        return string.IsNullOrEmpty(steam)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
            : steam.Replace('/', '\\');
    }

    /// Steam's 64-bit overlay renderer, from the install path Steam records for itself.
    internal static string SteamOverlayPath() => Path.Combine(SteamRoot(), "GameOverlayRenderer64.dll");

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
          --hide-console         Hide the console window and stop writing to it.
          --no-contain           Do not put the game in a kill-on-close job, so it
                                 survives the wrapper being stopped from Steam.
          --no-proxy             Do not create the window Steam activates to raise the game.
          --inject <dll>         Remote-load a DLL into the game. Repeatable.
          --inject-steam-overlay Remote-load Steam's GameOverlayRenderer64.dll.
          --inject-steam-client  Remote-load tier0_s64, vstdlib_s64 and steamclient64, in
                                 that order. Put it before --inject-steam-overlay.
          --call <dll>!<Export>  Call a zero-argument export inside the game. Repeatable.
          --steam-api-init <dll> Inject steam_api64.dll and call SteamAPI_Init in it. Only
                                 for testing that hypothesis: a non-Steam shortcut gets the
                                 overlay without ever calling it, so it should not be needed.
          --inject-delay <secs>  Wait this long after the game appears before injecting.
          --allow-suspend        Leave the package under normal PLM suspension.
          --no-steam-env         Do not pass Steam's launch variables to the package.
          --env-delay <secs>     Wait before editing the game's environment (default 8).
                                 Patching during startup kills the game about a second in;
                                 injection is held until 2s after this.
          --observe <name|pid>   Report on an already-running process and exit. Use it on a
                                 game where the Steam overlay works, as the control case.
          --log <path>           Transcript path (default %LOCALAPPDATA%\WSGM\uwp-spike).
          --help                 Show this text.
        """;
}
