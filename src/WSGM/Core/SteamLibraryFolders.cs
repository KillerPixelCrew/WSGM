using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Core;

/// <summary>
///     WSGM's library-folder policy over the toolkit's <see cref="SteamInstallFolders" />: selection of a
///     live library by the content id written on a card.
/// </summary>
/// <remarks>
///     Steam's folder API only ever selects by path, so a content id first resolves to its registered
///     path through <see cref="SteamLibraryVdf" />. An ambiguous path, which is what a reused card-reader
///     drive letter produces, refuses rather than guessing: acting on the wrong card's library is worse
///     than doing nothing.
/// </remarks>
public static class SteamLibraryFolders
{
    /// <summary>
    ///     Removes the live Steam library whose registration carries <paramref name="contentId" />. Only
    ///     that id's own path is passed to Steam, so a reused card-reader drive letter cannot select a
    ///     different card's library.
    /// </summary>
    /// <param name="steam">The session's Steam client.</param>
    /// <param name="contentId">The stable identity read from the card marker.</param>
    /// <param name="libraryFoldersVdf">Steam's current libraryfolders configuration.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The live removal outcome.</returns>
    public static async Task<SteamLibraryRemoveResult> RemoveLibraryByContentIdAsync(
        SteamClient steam, string contentId, string libraryFoldersVdf, CancellationToken cancellationToken = default)
    {
        var selection = SelectPath(contentId, libraryFoldersVdf);
        if (selection.Path is null)
        {
            return selection.Ambiguous
                ? new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.Rejected, "ContentIdPathAmbiguous")
                : new SteamLibraryRemoveResult(SteamLibraryRemoveStatus.NotPresent, null);
        }

        return await steam.InstallFolders.RemoveAllAtPathAsync(selection.Path, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Relabels the live Steam library whose registration carries <paramref name="contentId" />, under
    ///     the same identity discipline as removal.
    /// </summary>
    /// <param name="steam">The session's Steam client.</param>
    /// <param name="contentId">The stable identity read from the card marker.</param>
    /// <param name="libraryFoldersVdf">Steam's current libraryfolders configuration.</param>
    /// <param name="label">The new label.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The live relabel outcome.</returns>
    public static async Task<SteamLibraryLabelResult> SetLibraryLabelByContentIdAsync(
        SteamClient steam, string contentId, string libraryFoldersVdf, string label,
        CancellationToken cancellationToken = default)
    {
        var selection = SelectPath(contentId, libraryFoldersVdf);
        if (selection.Path is null)
        {
            return selection.Ambiguous
                ? new SteamLibraryLabelResult(SteamLibraryLabelStatus.Rejected, "ContentIdPathAmbiguous")
                : new SteamLibraryLabelResult(SteamLibraryLabelStatus.NotPresent, null);
        }

        return await steam.InstallFolders.SetLabelAsync(selection.Path, label, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Resolves a content id to the one library path it identifies. The comparison uses the toolkit's
    ///     normalizer, which is also the injected script's, so both sides agree on what "the same folder"
    ///     means; a bare trim would leave <c>D:/Games</c> and <c>D:\Games</c> unequal here while Steam
    ///     treats them as one.
    /// </summary>
    /// <param name="contentId">The card's identity.</param>
    /// <param name="libraryFoldersVdf">Steam's current libraryfolders configuration.</param>
    /// <returns>The path, or none with <c>Ambiguous</c> set when several registrations share it.</returns>
    internal static (string? Path, bool Ambiguous) SelectPath(string contentId, string libraryFoldersVdf)
    {
        var libraryPath = SteamLibraryVdf.PathForContentId(libraryFoldersVdf, contentId);
        if (libraryPath is null)
        {
            return (null, false);
        }

        var normalized = SteamLibraryVdf.NormalizePath(libraryPath);
        var matchingPaths = SteamLibraryVdf.ValuesOf(libraryFoldersVdf, "path")
            .Count(path => string.Equals(
                SteamLibraryVdf.NormalizePath(path),
                normalized,
                StringComparison.Ordinal));
        return matchingPaths == 1 ? (libraryPath, false) : (null, true);
    }
}
