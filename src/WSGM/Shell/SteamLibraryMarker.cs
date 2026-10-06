using System.IO;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Reads a card's library marker; each caller decides how to handle unreadable files.</summary>
internal static class SteamLibraryMarker
{
    /// <summary>Reads the marker's own content id and label.</summary>
    /// <param name="libraryPath">The library root.</param>
    /// <param name="contentId">The first non-whitespace content id, or null.</param>
    /// <param name="label">That block's label, or empty when no label exists.</param>
    /// <returns>Whether the marker names a usable library.</returns>
    internal static bool TryRead(string libraryPath, out string? contentId, out string label)
    {
        var marker = Path.Combine(libraryPath, "libraryfolder.vdf");
        if (!File.Exists(marker))
        {
            contentId = null;
            label = "";
            return false;
        }

        return SteamLibraryVdf.TryParseMarker(File.ReadAllText(marker), out contentId, out label);
    }
}
