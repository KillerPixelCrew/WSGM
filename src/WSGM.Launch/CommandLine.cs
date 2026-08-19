using System.Collections.Generic;

namespace WSGM.Launch;

/// <summary>What a single invocation of the wrapper was asked to do.</summary>
internal sealed class LaunchOptions
{
    /// <summary>Run the target at medium integrity when Steam launched us elevated.</summary>
    internal bool Deelevate { get; set; }

    /// <summary>Hold a Steam Input block lease for the target's lifetime, using the
    /// resident shim Steam loaded itself. Never injects; fails open when no shim
    /// answers.</summary>
    internal bool InputLease { get; set; }

    /// <summary>Hold the same lease by injecting the gate into Steam.</summary>
    /// <remarks>
    /// Written into a game's launch options only while Steam Input Management is
    /// off, because then Steam has loaded no shim to connect to. This is the single
    /// route in the shipped product that can inject.
    /// </remarks>
    internal bool InputLeaseInject { get; set; }

    /// <summary>Whether either lease behaviour was requested.</summary>
    internal bool AnyLease => InputLease || InputLeaseInject;

    internal bool Status { get; set; }

    internal bool Rescan { get; set; }

    internal bool Help { get; set; }

    /// <summary>Process the lease payload is injected into.</summary>
    internal string? TargetName { get; set; }

    /// <summary>Override path to <c>steam_input_gate.dll</c>.</summary>
    internal string? PayloadPath { get; set; }

    /// <summary>Target executable followed by its individual arguments.</summary>
    internal string[] Command { get; set; } = [];

    /// <summary>Whether this invocation only reports state instead of launching.</summary>
    internal bool IsDiagnostic => Status || Rescan || Help;
}

internal static class CommandLine
{
    internal const string Separator = "--";

    /// <summary>
    /// Parses the wrapper's own flags, stopping at <c>--</c>. Everything after the
    /// separator is the target command and is preserved as individual Windows
    /// arguments — Steam expands <c>%command%</c> into several of them, and
    /// re-quoting them here would corrupt paths containing spaces.
    /// </summary>
    /// <param name="arguments">Raw process arguments, excluding the executable.</param>
    /// <param name="options">The parsed options when parsing succeeded.</param>
    /// <param name="error">Why parsing failed, or <see langword="null"/>.</param>
    /// <returns>Whether <paramref name="arguments"/> formed a usable invocation.</returns>
    internal static bool TryParse(string[] arguments, out LaunchOptions options, out string? error)
    {
        options = new LaunchOptions();
        error = null;

        var command = new List<string>();
        var afterSeparator = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (afterSeparator)
            {
                command.Add(argument);
                continue;
            }

            switch (argument)
            {
                case Separator:
                    afterSeparator = true;
                    break;
                case "--deelevate":
                    options.Deelevate = true;
                    break;
                case "--input-lease":
                    options.InputLease = true;
                    break;
                case "--input-lease-inject":
                    options.InputLeaseInject = true;
                    break;
                case "--status":
                    options.Status = true;
                    break;
                case "--rescan":
                    options.Rescan = true;
                    break;
                case "--help" or "-h" or "/?":
                    options.Help = true;
                    break;
                case "--target-name":
                    if (!TryReadValue(arguments, ref index, argument, out var targetName, out error))
                    {
                        return false;
                    }
                    options.TargetName = targetName;
                    break;
                case "--payload":
                    if (!TryReadValue(arguments, ref index, argument, out var payloadPath, out error))
                    {
                        return false;
                    }
                    options.PayloadPath = payloadPath;
                    break;
                default:
                    error = $"Unknown option: {argument}";
                    return false;
            }
        }

        options.Command = [.. command];
        if (options.IsDiagnostic)
        {
            return true;
        }

        if (command.Count == 0)
        {
            error = afterSeparator
                ? "A target command is required after --."
                : "A target command is required. Expected: WSGM.Launch.exe [options] -- %command%";
            return false;
        }
        if (options.InputLease && options.InputLeaseInject)
        {
            // The two differ only in how the block is delivered, so asking for both
            // is a configuration mistake rather than a combination to reconcile.
            error = "--input-lease and --input-lease-inject are mutually exclusive.";
            return false;
        }
        if (!options.Deelevate && !options.AnyLease)
        {
            // Launching the target with neither wrapper behaviour would silently
            // add a process to Steam's chain for no benefit; say so instead.
            error =
                "At least one of --deelevate, --input-lease or --input-lease-inject is required.";
            return false;
        }
        return true;
    }

    internal static string UsageText =>
        """
        WSGM Launch wrapper

        Steam launch options (real Steam titles):
          "C:\path\WSGM.Launch.exe" --deelevate --input-lease -- %command%

        Non-Steam shortcuts put the wrapper in Target and the rest in Launch Arguments:
          Target:           "C:\path\WSGM.Launch.exe"
          Launch Arguments: --deelevate -- "C:\path\game.exe"

        Behaviours (at least one required):
          --deelevate       run the target at medium integrity under elevated Steam
          --input-lease     block Steam Input for the target's lifetime using the
                            shim Steam loaded from its own directory. Requires Steam
                            Input Management to be on; fails open if it is not.
          --input-lease-inject
                            block Steam Input by injecting into Steam instead. Used
                            when Steam Input Management is off, so there is no shim

        Diagnostics:
          --status          report the Steam Input gate's lease and handle counts
          --rescan          ask Steam to rediscover controllers

        Options:
          --target-name process.exe
          --payload path\steam_input_gate.dll
        """;

    private static bool TryReadValue(
        string[] arguments,
        ref int index,
        string option,
        out string? value,
        out string? error)
    {
        if (index + 1 >= arguments.Length)
        {
            value = null;
            error = $"{option} requires a value.";
            return false;
        }
        value = arguments[++index];
        error = null;
        return true;
    }
}
