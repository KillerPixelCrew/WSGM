using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The user-facing line for each <see cref="GameToolPhase" /> of a running operation.</summary>
internal static class GameToolsPhases
{
    /// <summary>Describes a phase.</summary>
    /// <param name="phase">The <see cref="GameToolPhase" /> id.</param>
    /// <param name="operation">The operation, which names the settings phase of a configure.</param>
    /// <param name="tool">The tool's name.</param>
    /// <param name="version">The version being installed as the user reads it, or null while unknown.</param>
    /// <param name="provider">Where the package is downloaded from, such as GitHub, or null while unknown.</param>
    /// <returns>Such as "Downloading OptiScaler 0.9.4 from GitHub".</returns>
    internal static string Detail(string phase, string operation, string tool, string? version, string? provider)
    {
        return phase switch
        {
            GameToolPhase.Download => "Downloading " + tool + (version is { Length: > 0 } ? " " + version : "")
                                      + (provider is { Length: > 0 } ? " from " + provider : ""),
            GameToolPhase.Verify => "Checking the package",
            GameToolPhase.Copy => "Backing up game files and copying " + tool,
            GameToolPhase.Settings => operation == "configure" ? "Saving settings" : "Applying settings",
            GameToolPhase.Remove => "Removing " + tool + " and restoring the game's files",
            GameToolPhase.Rescan => "Checking the game folder",
            _ => "Starting"
        };
    }
}
