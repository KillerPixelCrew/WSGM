using System;
using System.IO;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     A game's current custom artwork as the browser's Manage tab shows it: which slots carry a
///     custom image, each with a preview read from Steam's grid folder.
/// </summary>
internal static class ArtworkManagedSlots
{
    /// <summary>Reads every slot's current custom artwork for one game.</summary>
    /// <param name="appId">The game's Steam app id.</param>
    /// <returns>One slot per artwork asset in canonical order; a preview that cannot be read is null.</returns>
    internal static SteamArtworkManagedSlot[] Read(uint appId)
    {
        return ArtworkAssetNames.Ordered.Select(slot => ReadSlot(appId, slot.Asset, slot.Id, slot.Label)).ToArray();
    }

    private static SteamArtworkManagedSlot ReadSlot(uint appId, ArtworkAsset asset, string id, string label)
    {
        var path = SteamArtwork.FindCustomArtFile(appId, asset);
        return new SteamArtworkManagedSlot(id, label, path is not null, DataUrl(path));
    }

    private static string? DataUrl(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path))
            {
                return null;
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > ArtworkDownload.MaximumBytes)
            {
                Log.Warn(
                    $"Steam artwork page: current-art preview exceeds {ArtworkDownload.MaximumBytes} bytes: {path}");
                return null;
            }

            if (stream.Length <= 0)
            {
                return null;
            }

            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return ImageHeader.DataUrl(bytes);
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam artwork page: current-art preview failed: {ex.Message}");
            return null;
        }
    }
}
