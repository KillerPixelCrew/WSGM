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
/// <param name="Succeeded">Whether Steam accepted the requested change.</param>
public readonly record struct ArtworkResult(string Detail, bool Succeeded = false);

/// <summary>
///     WSGM's artwork changer. Grid/Hero/Logo/Wide go to the live client through
///     <see cref="SteamApps" />, so Steam persists and renders them with no restart. Icons are the
///     exception: a real game's icon lives in the per-app cache and a non-Steam shortcut points at
///     an image in its userdata grid directory. WSGM updates both through the toolkit's bounded
///     Steam client operations. The image bytes are fetched by
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
            return await ApplyIconAsync(
                SteamApps.NormalizeAppId(appId), imageBytes, ext, cancellationToken).ConfigureAwait(false);
        }

        if (!await HasSafeDimensionsAsync(imageBytes, ext, cancellationToken).ConfigureAwait(false))
        {
            return new ArtworkResult("The image header is invalid or declares unsafe dimensions.");
        }

        var result = await SteamApps.SetCustomArtworkAsync(
                SteamApps.NormalizeAppId(appId),
                SlotFor(asset),
                imageBytes,
                NormalizeExtension(ext) switch
                {
                    "jpg" => SteamArtworkFormat.Jpeg,
                    "webp" => SteamArtworkFormat.Webp,
                    _ => SteamArtworkFormat.Png
                },
                cancellationToken)
            .ConfigureAwait(false);
        var interpreted = Interpret(result, "Artwork applied.");
        if (interpreted.Succeeded && asset == ArtworkAsset.Logo && SteamApps.IsShortcutAppId((uint)appId)
            && await SteamApps.ReadLogoPositionAsync((uint)appId, cancellationToken).ConfigureAwait(false) is null)
        {
            var position = await SteamApps.SaveLogoPositionAsync(
                    (uint)appId, new SteamLogoPosition("BottomLeft", 50, 50), cancellationToken)
                .ConfigureAwait(false);
            if (!position.Accepted)
            {
                return new ArtworkResult(
                    "Logo applied, but Steam did not accept its default position: " + position.Error,
                    true);
            }
        }

        return interpreted;
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
            return await ClearIconAsync(SteamApps.NormalizeAppId(appId), cancellationToken).ConfigureAwait(false);
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
            var normalized = SteamApps.NormalizeAppId(appId);
            if (asset == ArtworkAsset.Icon && !SteamApps.IsShortcutAppId(normalized))
            {
                var storeIcon = Path.Combine(
                    Path.GetDirectoryName(steamExe)!, "appcache", "librarycache", $"{normalized}_icon.jpg");
                return File.Exists(storeIcon) ? storeIcon : null;
            }

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
            return new ArtworkResult(okMessage, true);
        }

        Log.Warn($"Artwork change failed: {result.Error}.");
        return new ArtworkResult(result.Error ?? "Steam rejected the change.");
    }

    private static async Task<ArtworkResult> ApplyIconAsync(
        uint appId, byte[] imageBytes, string extension, CancellationToken cancellationToken)
    {
        var steamExe = Steam.ExePath;
        if (steamExe is null)
        {
            return new ArtworkResult("Steam's installation directory is unavailable.");
        }

        var steamRoot = Path.GetDirectoryName(steamExe)!;
        if (SteamApps.IsShortcutAppId(appId))
        {
            var accountId = await SteamApps.ReadAccountIdAsync(cancellationToken).ConfigureAwait(false);
            if (accountId is null)
            {
                return new ArtworkResult("Steam's active userdata account is unavailable.");
            }

            var directory = Path.Combine(steamRoot, "userdata", accountId.Value.ToString(CultureInfo.InvariantCulture),
                "config", "grid");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{appId}_icon.{NormalizeExtension(extension)}");
            await WriteAtomicallyAsync(path, imageBytes, cancellationToken).ConfigureAwait(false);
            return Interpret(
                await SteamApps.SetShortcutIconAsync(appId, path, cancellationToken).ConfigureAwait(false),
                "Shortcut icon applied.");
        }

        var cache = Path.Combine(steamRoot, "appcache", "librarycache");
        Directory.CreateDirectory(cache);
        var storePath = Path.Combine(cache, $"{appId}_icon.jpg");
        await WriteAtomicallyAsync(storePath, imageBytes, cancellationToken).ConfigureAwait(false);
        return Interpret(
            await SteamApps.RefreshIconAsync(appId, cancellationToken).ConfigureAwait(false),
            "Icon applied.");
    }

    private static async Task<ArtworkResult> ClearIconAsync(uint appId, CancellationToken cancellationToken)
    {
        if (SteamApps.IsShortcutAppId(appId))
        {
            return Interpret(
                await SteamApps.ClearShortcutIconAsync(appId, cancellationToken).ConfigureAwait(false),
                "Shortcut icon reset.");
        }

        var url = await SteamApps.ReadOfficialIconUrlAsync(appId, cancellationToken).ConfigureAwait(false);
        if (url is null)
        {
            return new ArtworkResult("Steam did not provide the official icon URL.");
        }

        var bytes = await SteamGridDb.DownloadImageAsync(url, cancellationToken).ConfigureAwait(false);
        return bytes is null
            ? new ArtworkResult("The official icon download was empty.")
            : await ApplyIconAsync(appId, bytes, "jpg", cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAtomicallyAsync(
        string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + ".wsgm-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string NormalizeExtension(string extension)
    {
        return extension.TrimStart('.').ToLowerInvariant() switch
        {
            "jpeg" => "jpg",
            "jpg" => "jpg",
            "ico" => "ico",
            "webp" => "webp",
            _ => "png"
        };
    }

    private static async Task<bool> HasSafeDimensionsAsync(
        byte[] bytes, string extension, CancellationToken cancellationToken)
    {
        var normalized = NormalizeExtension(extension);
        if (normalized is "ico" or "webp")
        {
            return true;
        }

        var temporary = Path.Combine(Path.GetTempPath(), $"wsgm-art-{Guid.NewGuid():N}.{normalized}");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            return ArtworkImageHeader.TryReadSize(temporary, out var width, out var height)
                   && ArtworkImageHeader.IsWithinLimits(width, height);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
