using System;

namespace WSGM.Core;

/// <summary>Which of setup's three install modes the user picked.</summary>
public enum InstallProfileKind
{
    /// <summary>Game Mode at sign-in, nothing device-specific.</summary>
    Minimal,

    /// <summary>Game Mode at sign-in, with the device integration and the virtual controller.</summary>
    Claw8A2Vm,

    /// <summary>A resident desktop session at sign-in, nothing device-specific.</summary>
    DesktopFirst,
}

/// <summary>Turns the install mode into the configuration a first run should start from.
///
/// Setup chooses which bytes land on disk; this chooses what the first run of those bytes does.
/// They are separate decisions: the device package installs disabled on any machine whose SMBIOS
/// does not match, and a person can turn every one of these settings around afterwards.
///
/// It only ever seeds a machine that has no configuration yet. Re-running setup over an install is
/// how people repair and upgrade, and a mode that rewrote their start mode and integration switch
/// each time would silently undo what they set in Settings. Changing the mode of an existing
/// install therefore means changing it in Settings, which is where it is visible.</summary>
public static class InstallProfile
{
    /// <summary>Reads the mode from setup's <c>--profile=</c> argument.</summary>
    /// <param name="value">The argument value, or null when setup passed none.</param>
    /// <param name="kind">Set to the parsed mode.</param>
    /// <returns>True when the value named a mode.</returns>
    public static bool TryParse(string? value, out InstallProfileKind kind)
    {
        kind = InstallProfileKind.Minimal;
        if (string.IsNullOrWhiteSpace(value)) { return false; }
        switch (value.Trim().ToLowerInvariant())
        {
            case "minimal": kind = InstallProfileKind.Minimal; return true;
            case "claw8a2vm": kind = InstallProfileKind.Claw8A2Vm; return true;
            case "desktop": kind = InstallProfileKind.DesktopFirst; return true;
            // "custom" is a real setup type, and it deliberately names no mode: the user picked
            // components rather than an intent, so the configuration's own defaults are the honest
            // starting point.
            default: return false;
        }
    }

    /// <summary>Reads the <c>--profile=</c> value out of a command line.</summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>The value, or null when the argument is absent.</returns>
    public static string? Read(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        const string prefix = "--profile=";
        foreach (string argument in args)
        {
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument[prefix.Length..];
            }
        }
        return null;
    }

    /// <summary>Applies the mode to a configuration that has just been created.</summary>
    /// <param name="config">The configuration to seed.</param>
    /// <param name="kind">The mode setup ran with.</param>
    /// <param name="freshInstall">Whether this machine had no configuration before setup ran.</param>
    /// <returns>True when the configuration was changed and needs saving.</returns>
    public static bool Apply(AppConfig config, InstallProfileKind kind, bool freshInstall)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!freshInstall) { return false; }

        config.StartAtSignIn = true;
        config.StartMode = kind == InstallProfileKind.DesktopFirst
            ? SessionStartMode.Desktop
            : SessionStartMode.Game;
        // Naming a device in setup is the explicit choice the integration waits for. Leaving it off
        // after someone selected their exact handheld would install the package and do nothing with
        // it, which reads as a broken install rather than a safe default. It still starts disabled
        // for every other mode, and the package refuses any machine whose identity does not match.
        config.DeviceIntegration.Enabled = kind == InstallProfileKind.Claw8A2Vm;
        return true;
    }
}
