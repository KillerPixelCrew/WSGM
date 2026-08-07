using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Builds the Steam launch-option command for the Steam Input Lease
/// wrapper, which blocks Steam Input for the lifetime of one game.</summary>
/// <remarks>
/// Some games read controllers directly (SDL, raw HID, DirectInput) and fight
/// Steam Input over the same device. The wrapper holds a lease while the game's
/// process tree lives, so Steam stops opening the controller and hands it to the
/// game instead; the lease is pipe-backed, so a crash still restores Steam.
/// </remarks>
internal static class SteamInputLeaseCommand
{
    internal const string HelperFileName = "steam-input-lease.exe";

    /// <summary>Resolves the wrapper beside the running WSGM executable.</summary>
    /// <returns>The absolute path the copied launch option will reference.</returns>
    internal static string HelperPathForCurrentDeployment()
    {
        var directory = Path.GetDirectoryName(Environment.ProcessPath);
        return Path.Combine(directory ?? Installer.InstallDir, HelperFileName);
    }

    /// <summary>Builds the value a user pastes into a game's Steam launch options.</summary>
    /// <param name="helperPath">Absolute path of the wrapper executable.</param>
    /// <returns>The launch-option string, quoted for paths containing spaces.</returns>
    /// <exception cref="ArgumentException"><paramref name="helperPath"/> is missing.</exception>
    internal static string SteamLaunchOptions(string helperPath)
    {
        if (string.IsNullOrWhiteSpace(helperPath))
        {
            throw new ArgumentException("A helper path is required.", nameof(helperPath));
        }
        // Everything after `--` is Steam's original command; the wrapper applies
        // Windows quoting itself. Unlike the de-elevation helper, the separator
        // is mandatory — without it the wrapper reads %command% as its own flags.
        return $"\"{helperPath}\" -- %command%";
    }
}
