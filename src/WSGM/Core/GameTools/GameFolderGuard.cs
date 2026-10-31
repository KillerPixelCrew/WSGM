using System;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Rejects folders too broad to be a game's program folder.</summary>
internal static class GameFolderGuard
{
    /// <summary>Explains why a picked executable's folder can't be a game folder, or returns null.</summary>
    /// <param name="executable">The absolute path the user picked.</param>
    /// <param name="broadAllowed">Skips the broad-folder check, for a program inside an already accepted install root.</param>
    /// <returns>A user-facing reason, or null when the folder is acceptable.</returns>
    /// <remarks>
    ///     The match is exact: subfolders of these locations are allowed, so a game under Program Files
    ///     or Documents still works. WindowsApps is always refused because packaged games can't be modified;
    ///     this is the one place that rule lives.
    /// </remarks>
    internal static string? Reject(string executable, bool broadAllowed = false)
    {
        string folder;
        try
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(executable)) ?? "");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "Choose the game's program.";
        }

        if (folder.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase))
        {
            return "Games in the protected WindowsApps folder can't be modified.";
        }

        if (broadAllowed)
        {
            return null;
        }

        if (folder.Length == 0 || Path.GetPathRoot(folder) is { } root
            && Path.TrimEndingDirectorySeparator(root).Equals(folder, StringComparison.OrdinalIgnoreCase))
        {
            return "That folder is too broad. Choose a program inside the game's own folder.";
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] broad =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), profile,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            profile.Length > 0 ? Path.Combine(profile, "Downloads") : "",
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
            Environment.GetEnvironmentVariable("OneDriveConsumer") ?? "",
            Environment.GetEnvironmentVariable("OneDriveCommercial") ?? ""
        ];
        return broad.Any(path => path.Length > 0
                                 && Path.TrimEndingDirectorySeparator(path).Equals(folder, StringComparison.OrdinalIgnoreCase))
            ? "That folder is too broad. Choose a program inside the game's own folder."
            : null;
    }
}
