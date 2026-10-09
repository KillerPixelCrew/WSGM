using System;
using System.Linq;

namespace WSGM.Setup;

/// <summary>What the command line asked for.</summary>
internal enum SetupMode
{
    /// <summary>Decide from what is installed: install, update, or the repair and uninstall chooser.</summary>
    Auto,

    /// <summary>Update the installed WSGM to this release.</summary>
    Update,

    /// <summary>Reinstall this release over itself.</summary>
    Repair,

    /// <summary>Remove WSGM.</summary>
    Uninstall,

    /// <summary>
    ///     The run after the restart a driver update asked for: install the USB/IP driver and turn WSGM's
    ///     autostart back on, nothing else. Setup schedules this itself through RunOnce.
    /// </summary>
    FinishDrivers
}

/// <summary>Parsed command line. Unknown arguments are refused, not ignored.</summary>
internal sealed record SetupOptions
{
    /// <summary>The usage text.</summary>
    public const string Usage =
        "WSGM.Setup.exe [/quiet] [/update | /repair | /uninstall | /finishdrivers] [/answers=<file>] "
        + "[/plugin=<id>|none] [/removedata] [/keepcomponents] [/payload=<dir>]";

    /// <summary>No window: take defaults or the given answers and report through the exit code and log.</summary>
    public bool Quiet { get; init; }

    /// <summary>The requested mode.</summary>
    public SetupMode Mode { get; init; }

    /// <summary>A setup answers file to apply instead of the defaults or current values.</summary>
    public string? AnswersFile { get; init; }

    /// <summary>Uninstall: delete settings and data too.</summary>
    public bool RemoveData { get; init; }

    /// <summary>Uninstall: leave USB/IP and HidHide installed.</summary>
    public bool KeepComponents { get; init; }

    /// <summary>The native handheld definition or family to enable, <c>none</c> to decline, or null to follow detection.</summary>
    public string? Plugin { get; init; }

    /// <summary>A payload directory to use instead of the embedded one, for development builds.</summary>
    public string? PayloadDirectory { get; init; }

    /// <summary>Parses the arguments.</summary>
    /// <param name="args">Process arguments.</param>
    /// <param name="error">Why the arguments were refused.</param>
    /// <returns>The options, or null when the arguments were refused.</returns>
    public static SetupOptions? Parse(string[] args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;
        SetupOptions options = new();
        foreach (var raw in args)
        {
            var argument = raw.Trim();
            var name = argument.Split('=', 2)[0].ToLowerInvariant().Replace('-', '/');
            var value = argument.Contains('=') ? argument[(argument.IndexOf('=') + 1)..] : null;
            switch (name)
            {
                case "/quiet":
                case "/silent":
                    options = options with { Quiet = true };
                    break;
                case "/update":
                    options = options with { Mode = SetupMode.Update };
                    break;
                case "/repair":
                    options = options with { Mode = SetupMode.Repair };
                    break;
                case "/uninstall":
                    options = options with { Mode = SetupMode.Uninstall };
                    break;
                case "/finishdrivers":
                    options = options with { Mode = SetupMode.FinishDrivers };
                    break;
                case "/removedata":
                    options = options with { RemoveData = true };
                    break;
                case "/keepcomponents":
                    options = options with { KeepComponents = true };
                    break;
                case "/answers" when !string.IsNullOrWhiteSpace(value):
                    options = options with { AnswersFile = value };
                    break;
                case "/plugin" when !string.IsNullOrWhiteSpace(value):
                    options = options with { Plugin = value };
                    break;
                case "/payload" when !string.IsNullOrWhiteSpace(value):
                    options = options with { PayloadDirectory = value };
                    break;
                default:
                    error = $"Unknown argument: {raw}";
                    return null;
            }
        }

        if (options.RemoveData || options.KeepComponents)
        {
            if (options.Mode is not SetupMode.Uninstall)
            {
                error = "/removedata and /keepcomponents only apply to /uninstall.";
                return null;
            }
        }

        return options;
    }

    /// <summary>Whether any argument asks for a window-less run.</summary>
    /// <param name="args">Raw command-line arguments, before full validation.</param>
    /// <returns>True for /quiet, /silent, or -quiet after trimming and case normalization.</returns>
    public static bool WantsQuiet(string[] args)
    {
        return args.Any(argument => argument.Trim().ToLowerInvariant() is "/quiet" or "/silent" or "-quiet");
    }
}
