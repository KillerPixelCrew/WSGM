using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Turns what Steam reports about one running AppID into RTSS pairing evidence.
/// </summary>
/// <remarks>
///     One details read serves both kinds of entry: a shortcut names its target executable, and a
///     store title names only its install folder, because Steam never exposes a store title's
///     executable. The folder is what the foreground pairing is validated against.
/// </remarks>
internal static class SteamRunningAppPairing
{
    /// <summary>Reads and normalizes one running AppID's pairing evidence.</summary>
    /// <param name="probe">The toolkit's running-app probe, which owns the transport.</param>
    /// <param name="steamAppId">The AppID Steam named.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Normalized pairing evidence, possibly partial with a diagnostic when Steam cannot resolve the application.</returns>
    internal static async Task<SteamRunningAppProfile> ResolveAsync(
        SteamRunningAppsProbe probe,
        uint steamAppId,
        CancellationToken cancellationToken)
    {
        var result = await probe.ReadDetailsAsync(steamAppId, cancellationToken).ConfigureAwait(false);
        if (result is not { Succeeded: true, Value: { } details })
        {
            return new SteamRunningAppProfile(null, null, result.Error);
        }

        return SteamApps.IsShortcutAppId(steamAppId)
            ? NormalizeShortcutTarget(details.ShortcutExe)
            : NormalizeInstallFolder(details.InstallFolder);
    }

    /// <summary>Turns a store title's reported install folder into pairing evidence.</summary>
    /// <param name="folder">The <c>strInstallFolder</c> value Steam reported.</param>
    /// <returns>
    ///     An existing absolute install folder as pairing evidence, or a diagnostic with no folder. No executable
    ///     identity is inferred here.
    /// </returns>
    internal static SteamRunningAppProfile NormalizeInstallFolder(string folder)
    {
        folder = folder.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return new SteamRunningAppProfile(
                null,
                null,
                "Steam did not report the running title's install folder; "
                + "RTSS remains on the global profile.");
        }

        try
        {
            if (!Path.IsPathFullyQualified(folder))
            {
                return new SteamRunningAppProfile(
                    null,
                    null,
                    "Steam reported an install folder that is not an absolute path.");
            }

            var normalized = Path.GetFullPath(folder);
            if (!Directory.Exists(normalized))
            {
                return new SteamRunningAppProfile(
                    null,
                    null,
                    "Steam's reported install folder is not present.");
            }

            return new SteamRunningAppProfile(
                null,
                null,
                "Steam exposes no executable for a store title; the RTSS profile pairs with the "
                + "foreground process running from its install folder.",
                normalized);
        }
        catch (Exception ex)
        {
            return new SteamRunningAppProfile(null, null, ex.Message);
        }
    }

    internal static SteamRunningAppProfile NormalizeShortcutTarget(string target)
    {
        target = target.Trim();
        if (target is ['"', .., '"'])
        {
            target = target[1..^1].Trim();
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return new SteamRunningAppProfile(
                null,
                null,
                "Steam did not expose the running shortcut's executable.");
        }

        string profileName;
        string normalizedPath;
        try
        {
            if (!Path.IsPathFullyQualified(target))
            {
                return new SteamRunningAppProfile(
                    null,
                    null,
                    "Steam reported a shortcut target that is not an absolute path.");
            }

            normalizedPath = Path.GetFullPath(target);
            profileName = Path.GetFileName(normalizedPath);
        }
        catch (Exception ex)
        {
            return new SteamRunningAppProfile(null, null, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(profileName)
            || !profileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || LaunchWrapperCommand.HelperExecutableNames.Contains(profileName))
        {
            return new SteamRunningAppProfile(
                null,
                null,
                "The shortcut target is not a truthful RTSS application profile.");
        }

        if (!File.Exists(normalizedPath))
        {
            return new SteamRunningAppProfile(
                null,
                null,
                "Steam's shortcut executable is no longer present.");
        }

        return new SteamRunningAppProfile(normalizedPath, profileName, null);
    }
}
