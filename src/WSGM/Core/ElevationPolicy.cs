using System.Linq;

namespace WSGM.Core;

/// <summary>Decides whether this configuration needs WSGM to run elevated.
///
/// One rule for both callers: <see cref="SelfElevation"/> relaunches on it, and
/// <see cref="BootManifestWriter"/> projects it so the logon service picks the linked elevated
/// token. They used to disagree about device integration, which made a shortcut start and a
/// sign-in start of the same configuration land at different integrity levels.</summary>
public static class ElevationPolicy
{
    /// <summary>Why the configuration wants elevation, or null when it does not.</summary>
    /// <param name="config">The configuration to test.</param>
    /// <param name="steamAlreadyElevated">Whether a running or configured Steam is elevated;
    /// supplied by the caller because reading it enumerates processes.</param>
    /// <returns>A short reason for the log line, or null.</returns>
    public static string? ElevationReason(AppConfig config, bool steamAlreadyElevated)
    {
        if (steamAlreadyElevated)
        {
            return "Steam requires matching elevation";
        }
        if (config.StartupApps.Any(app => app.Enabled && app.Elevated))
        {
            return "the configuration starts elevated apps";
        }
        // The sole administrator-installed plugin inherits WSGM's token.
        if (config.DeviceIntegration.Enabled)
        {
            return "device integration is enabled";
        }
        // WSGM starts Steam itself in every mode, and children inherit the token. Elevating here is
        // what keeps Steam Input and the Steam Overlay working against elevated windows once the
        // user's own elevated Steam autostart is gone.
        if (!config.SteamLaunchUnelevated)
        {
            return "WSGM starts Steam at its own integrity";
        }
        return null;
    }

    /// <summary>Whether the configuration wants WSGM elevated.</summary>
    /// <param name="config">The configuration to test.</param>
    /// <param name="steamAlreadyElevated">Whether a running or configured Steam is elevated.</param>
    /// <returns>True when an elevated session is wanted.</returns>
    public static bool WantsElevation(AppConfig config, bool steamAlreadyElevated) =>
        ElevationReason(config, steamAlreadyElevated) is not null;
}
