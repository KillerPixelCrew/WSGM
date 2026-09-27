using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
///     Steam client operations. The image bytes are fetched through <see cref="ArtworkSearch" />
///     and <see cref="ArtworkDownload" />, and their format is read from the bytes themselves;
///     finding what is currently applied is local file work and stays here.
/// </summary>
public static class SteamArtwork
{
    // Steam persists SetCustomArtworkForApp into userdata\<account>\config\grid using
    // the unsigned app id plus a per-slot suffix. Filenames per slot:
    //   Grid (portrait)  <id>p.<ext>      Hero  <id>_hero.<ext>
    //   Logo             <id>_logo.<ext>  Wide  <id>.<ext>   Icon  <id>_icon.<ext>
    private static readonly string[] GridExtensions = ["png", "jpg", "jpeg", "webp"];

    /// <summary>Downloads an image and applies it to an artwork slot.</summary>
    /// <param name="appId">The Steam app id (unsigned; a shortcut id in its unsigned 32-bit form).</param>
    /// <param name="asset">Which slot.</param>
    /// <param name="url">The image's address, as a provider answered it.</param>
    /// <param name="config">The loaded configuration, for the provider's credentials.</param>
    /// <param name="cancellationToken">Cancels the download and the apply.</param>
    /// <returns>The outcome; a failed download is a failed outcome with its reason, never an exception.</returns>
    /// <remarks>
    ///     The download goes through the provider that serves the image (<see cref="ArtworkSearch.DownloadAsync" />),
    ///     so an image from a paced API waits its turn with that API's searches.
    /// </remarks>
    public static async Task<ArtworkResult> ApplyFromUrlAsync(
        long appId, ArtworkAsset asset, string url, ArtworkConfig config, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await ArtworkSearch.DownloadAsync(url, config, cancellationToken).ConfigureAwait(false);
        }
        catch (ArtworkProviderException ex)
        {
            return new ArtworkResult(ex.Message);
        }

        return await ApplyAsync(appId, asset, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies several images to one title: downloaded together, applied one at a time.</summary>
    /// <param name="appId">The Steam app id.</param>
    /// <param name="images">The slot and address of each image.</param>
    /// <param name="config">The loaded configuration, for the providers' credentials.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Each slot's outcome, in the order given.</returns>
    /// <remarks>
    ///     One image failing does not stop the others: a title with a poster and no logo still gets
    ///     its poster. Full-size capsules are the slow half of dressing a title, so they download
    ///     together; each provider's own gate still paces its share. The applies stay serial because
    ///     each is a write into the running client's library store.
    /// </remarks>
    public static async Task<IReadOnlyList<ArtworkResult>> ApplyManyFromUrlsAsync(
        long appId, IReadOnlyList<(ArtworkAsset Asset, string Url)> images, ArtworkConfig config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);
        var downloads = await Task.WhenAll(images.Select(async image =>
        {
            try
            {
                return (Bytes: await ArtworkSearch.DownloadAsync(image.Url, config, cancellationToken)
                    .ConfigureAwait(false), Failure: null);
            }
            catch (ArtworkProviderException ex)
            {
                return (Bytes: (byte[]?)null, Failure: (string?)ex.Message);
            }
        })).ConfigureAwait(false);

        var results = new ArtworkResult[images.Count];
        for (var index = 0; index < images.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[index] = downloads[index] is { Bytes: { } bytes }
                ? await ApplyAsync(appId, images[index].Asset, bytes, cancellationToken).ConfigureAwait(false)
                : new ArtworkResult(downloads[index].Failure ?? "The image did not download.");
        }

        return results;
    }

    /// <summary>Applies an image to an artwork slot.</summary>
    /// <param name="appId">
    ///     The Steam app id (unsigned; a non-Steam shortcut id is
    ///     accepted as its unsigned 32-bit form).
    /// </param>
    /// <param name="asset">Which slot.</param>
    /// <param name="imageBytes">The raw image bytes; their own header decides the format.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome. An image that is not PNG, JPEG, WEBP or ICO is refused, not guessed at.</returns>
    public static async Task<ArtworkResult> ApplyAsync(
        long appId, ArtworkAsset asset, byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
        {
            return new ArtworkResult("The image was empty.");
        }

        if (ImageFormat(imageBytes) is not { } format)
        {
            return new ArtworkResult("The image is not a PNG, JPEG, WEBP or ICO file.");
        }

        if (asset == ArtworkAsset.Icon)
        {
            return await ApplyIconAsync(
                SteamApps.NormalizeAppId(appId), imageBytes, format, cancellationToken).ConfigureAwait(false);
        }

        if (format == "ico")
        {
            return new ArtworkResult("An ICO file can only be used as an icon.");
        }

        if (format != "webp"
            && !(ImageHeader.TryReadSize(imageBytes, out var width, out var height)
                 && ImageHeader.IsWithinLimits(width, height)))
        {
            return new ArtworkResult("The image header is invalid or declares unsafe dimensions.");
        }

        var result = await SteamApps.SetCustomArtworkAsync(
                SteamApps.NormalizeAppId(appId),
                SlotFor(asset),
                imageBytes,
                format switch
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

    /// <summary>Which of the formats Steam takes for artwork an image's first bytes declare.</summary>
    /// <param name="bytes">The image.</param>
    /// <returns><c>png</c>, <c>jpg</c>, <c>webp</c> or <c>ico</c>, or null for anything else.</returns>
    /// <remarks>
    ///     The bytes decide, never the address they came from. A provider's media endpoint answers
    ///     every format under one URL, and Steam files an image under the extension it is told, so a
    ///     JPEG named as a PNG shows as a blank tile.
    /// </remarks>
    internal static string? ImageFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "png";
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "jpg";
        }

        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return "webp";
        }

        return bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00]) ? "ico" : null;
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
        uint appId, byte[] imageBytes, string format, CancellationToken cancellationToken)
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
            var path = Path.Combine(directory, $"{appId}_icon.{format}");
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

        byte[] bytes;
        try
        {
            bytes = await ArtworkDownload.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (ArtworkProviderException ex)
        {
            return new ArtworkResult(ex.Message);
        }

        return ImageFormat(bytes) is { } format
            ? await ApplyIconAsync(appId, bytes, format, cancellationToken).ConfigureAwait(false)
            : new ArtworkResult("Steam's official icon was not an image WSGM can apply.");
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
}
