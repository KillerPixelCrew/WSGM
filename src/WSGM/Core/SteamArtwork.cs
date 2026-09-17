using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Outcome of an artwork change.</summary>
/// <param name="Detail">
///     A user-facing note (why it failed, or a follow-up such as
///     "restart Steam").
/// </param>
public readonly record struct ArtworkResult(string Detail);

/// <summary>
///     WSGM's artwork changer. Grid/Hero/Logo/Wide go to the live client through
///     <see cref="SteamApps" />, so Steam persists and renders them with no restart. Icons are the
///     exception and are NOT supported yet for either kind of entry: Steam has no client API for
///     them, a real game's icon lives in a versioned per-app cache and a non-Steam shortcut's needs
///     a <c>shortcuts.vdf</c> edit plus a Steam restart, so both apply and reset report
///     not-yet-supported and write nothing. The image bytes are fetched by
///     <see cref="SteamGridDb" />; finding what is currently applied is local file work and stays
///     here.
/// </summary>
public static class SteamArtwork
{
    // Steam persists SetCustomArtworkForApp into userdata\<account>\config\grid using
    // the unsigned app id plus a per-slot suffix. Filenames per slot:
    //   Grid (portrait)  <id>p.<ext>      Hero  <id>_hero.<ext>
    //   Logo             <id>_logo.<ext>  Wide  <id>.<ext>   Icon  <id>_icon.<ext>
    private static readonly string[] GridExtensions = ["png", "jpg", "jpeg", "webp"];

    /// <summary>Applies an image to an artwork slot.</summary>
    /// <param name="appId">
    ///     The Steam app id (unsigned; a non-Steam shortcut id is
    ///     accepted as its unsigned 32-bit form).
    /// </param>
    /// <param name="asset">Which slot.</param>
    /// <param name="imageBytes">The raw image bytes (from <see cref="SteamGridDb" />).</param>
    /// <param name="ext">The image extension, <c>png</c> or <c>jpg</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<ArtworkResult> ApplyAsync(
        long appId, ArtworkAsset asset, byte[] imageBytes, string ext,
        CancellationToken cancellationToken = default)
    {
        if (imageBytes.Length == 0)
        {
            return new ArtworkResult("The image was empty.");
        }

        if (asset == ArtworkAsset.Icon)
        {
            return new ArtworkResult(
                "Steam icons use a versioned per-app cache and cannot be changed safely here yet.");
        }

        var result = await SteamApps.SetCustomArtworkAsync(
                SteamApps.NormalizeAppId(appId),
                SlotFor(asset),
                imageBytes,
                ext is "jpg" or "jpeg" ? SteamArtworkFormat.Jpeg : SteamArtworkFormat.Png,
                cancellationToken)
            .ConfigureAwait(false);
        return Interpret(result, "Artwork applied.");
    }

    /// <summary>Resets an artwork slot back to Steam's official art.</summary>
    /// <param name="appId">The Steam app id.</param>
    /// <param name="asset">Which slot.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<ArtworkResult> ClearAsync(
        long appId, ArtworkAsset asset, CancellationToken cancellationToken = default)
    {
        if (asset == ArtworkAsset.Icon)
        {
            return new ArtworkResult("Icons can't be reset from here yet.");
        }

        var result = await SteamApps.ClearCustomArtworkAsync(
                SteamApps.NormalizeAppId(appId), SlotFor(asset), cancellationToken)
            .ConfigureAwait(false);
        return Interpret(result, "Reset to official art.");
    }

    /// <summary>Maps WSGM's slot to the toolkit's. Icon never reaches here.</summary>
    private static SteamArtworkSlot SlotFor(ArtworkAsset asset)
    {
        return asset switch
        {
            ArtworkAsset.Grid => SteamArtworkSlot.Grid,
            ArtworkAsset.Hero => SteamArtworkSlot.Hero,
            ArtworkAsset.Logo => SteamArtworkSlot.Logo,
            ArtworkAsset.Wide => SteamArtworkSlot.Wide,
            _ => throw new ArgumentOutOfRangeException(nameof(asset), asset, "Unsupported artwork slot.")
        };
    }

    /// <summary>
    ///     The on-disk file of the CURRENT custom artwork for a slot, or null when
    ///     the slot uses Steam's official art (no custom file). Scans every account's grid
    ///     folder and prefers the most recently written match. Local file I/O only.
    /// </summary>
    /// <param name="appId">The Steam app id (signed shortcut ids accepted).</param>
    /// <param name="asset">Which slot.</param>
    public static string? FindCustomArtFile(long appId, ArtworkAsset asset)
    {
        try
        {
            var steamExe = Steam.ExePath;
            if (steamExe is null)
            {
                return null;
            }

            var userdata = Path.Combine(Path.GetDirectoryName(steamExe)!, "userdata");
            if (!Directory.Exists(userdata))
            {
                return null;
            }

            var id = SteamApps.NormalizeAppId(appId).ToString(CultureInfo.InvariantCulture);
            var stem = asset switch
            {
                ArtworkAsset.Grid => id + "p",
                ArtworkAsset.Hero => id + "_hero",
                ArtworkAsset.Logo => id + "_logo",
                ArtworkAsset.Icon => id + "_icon",
                _ => id
            };
            string? newest = null;
            var newestTime = DateTime.MinValue;
            foreach (var account in Directory.EnumerateDirectories(userdata))
            {
                var grid = Path.Combine(account, "config", "grid");
                if (!Directory.Exists(grid))
                {
                    continue;
                }

                foreach (var ext in GridExtensions)
                {
                    var candidate = Path.Combine(grid, stem + "." + ext);
                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    var time = File.GetLastWriteTimeUtc(candidate);
                    if (time <= newestTime)
                    {
                        continue;
                    }

                    newestTime = time;
                    newest = candidate;
                }
            }

            return newest;
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork: custom-art lookup failed: {ex.Message}");
            return null;
        }
    }

    private static ArtworkResult Interpret(SteamClientWriteResult result, string okMessage)
    {
        if (!result.Reachable)
        {
            return new ArtworkResult("Steam isn't reachable — is it running?");
        }

        if (result.Accepted)
        {
            return new ArtworkResult(okMessage);
        }

        Log.Warn($"Artwork change failed: {result.Error}.");
        return new ArtworkResult(result.Error ?? "Steam rejected the change.");
    }
}
