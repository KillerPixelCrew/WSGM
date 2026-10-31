using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Core;

/// <summary>
///     WSGM's artwork changer. Grid/Hero/Logo/Wide go to the live client through
///     <see cref="SteamApps" />, so Steam persists and renders them with no restart. Icons are the
///     exception: a real game's icon lives in the per-app cache and a non-Steam shortcut points at
///     an image in its userdata grid directory. WSGM updates both through the toolkit's bounded
///     Steam client operations. The image bytes are fetched through <see cref="ArtworkSearch" />
///     and <see cref="ArtworkDownload" />, and their format is read from the bytes themselves;
///     finding what is currently applied is local file work and stays here.
/// </summary>
internal static class SteamArtwork
{
    // Steam persists SetCustomArtworkForApp into userdata\<account>\config\grid using
    // the unsigned app id plus a per-slot suffix. Filenames per slot:
    //   Grid (portrait)  <id>p.<ext>      Hero  <id>_hero.<ext>
    //   Logo             <id>_logo.<ext>  Wide  <id>.<ext>   Icon  <id>_icon.<ext>
    private static readonly string[] GridExtensions = ["png", "jpg", "jpeg", "webp", "ico"];

    /// <summary>Builds Steam's unsigned app-id filename stem for a custom artwork slot.</summary>
    /// <param name="appId">Normalized unsigned app or shortcut id.</param>
    /// <param name="asset">Artwork slot.</param>
    /// <returns>The basename without a file extension.</returns>
    internal static string GridStem(uint appId, ArtworkAsset asset)
    {
        var id = appId.ToString(CultureInfo.InvariantCulture);
        return asset switch
        {
            ArtworkAsset.Grid => id + "p",
            ArtworkAsset.Hero => id + "_hero",
            ArtworkAsset.Logo => id + "_logo",
            ArtworkAsset.Icon => id + "_icon",
            ArtworkAsset.Wide => id,
            _ => throw new ArgumentOutOfRangeException(nameof(asset), asset, "Unknown artwork slot.")
        };
    }

    /// <summary>Downloads an image and applies it to an artwork slot.</summary>
    /// <param name="steam">The session's Steam client.</param>
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
        SteamClient steam, long appId, ArtworkAsset asset, string url, ArtworkConfig config,
        CancellationToken cancellationToken)
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

        return await ApplyAsync(steam, appId, asset, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies several images to one title: downloaded together, applied one at a time.</summary>
    /// <param name="steam">The session's Steam client.</param>
    /// <param name="appId">The Steam app id.</param>
    /// <param name="images">The slot and address of each image; an empty address clears the slot.</param>
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
        SteamClient steam, long appId, IReadOnlyList<(ArtworkAsset Asset, string Url)> images, ArtworkConfig config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);
        (byte[]? Bytes, string? Failure)[] downloads;
        try
        {
            downloads = await Task.WhenAll(images.Select(async image =>
            {
                if (image.Url.Length == 0)
                {
                    return (Bytes: null, Failure: null);
                }

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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Downloads finish before any write. A stopped batch has no native side effects yet.
            return images.Select(_ => new ArtworkResult("Stopped before the image was written.")).ToArray();
        }

        var results = new ArtworkResult[images.Count];
        for (var index = 0; index < images.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                for (var remaining = index; remaining < images.Count; remaining++)
                {
                    results[remaining] = new ArtworkResult("Stopped before the image was written.");
                }
                break;
            }
            if (images[index].Url.Length == 0)
            {
                results[index] = await ClearAsync(steam, appId, images[index].Asset, CancellationToken.None)
                    .ConfigureAwait(false);
                continue;
            }

            results[index] = downloads[index] is { Bytes: { } bytes }
                ? await ApplyAsync(steam, appId, images[index].Asset, bytes, CancellationToken.None).ConfigureAwait(false)
                : new ArtworkResult(downloads[index].Failure ?? "The image did not download.");
        }

        return results;
    }

    /// <summary>Applies an image to an artwork slot.</summary>
    /// <param name="steam">The session's Steam client.</param>
    /// <param name="appId">
    ///     The Steam app id (unsigned; a non-Steam shortcut id is
    ///     accepted as its unsigned 32-bit form).
    /// </param>
    /// <param name="asset">Which slot.</param>
    /// <param name="imageBytes">The raw image bytes; their own header decides the format.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome. An image that is not PNG, JPEG, WEBP or ICO is refused, not guessed at.</returns>
    public static async Task<ArtworkResult> ApplyAsync(
        SteamClient steam, long appId, ArtworkAsset asset, byte[] imageBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
        {
            return new ArtworkResult("The image was empty.");
        }

        if (ImageHeader.Format(imageBytes) is not { } format)
        {
            return new ArtworkResult("The image is not a PNG, JPEG, WEBP or ICO file.");
        }

        if (asset == ArtworkAsset.Icon)
        {
            return await ApplyIconAsync(
                steam, SteamApps.NormalizeAppId(appId), imageBytes, format, cancellationToken).ConfigureAwait(false);
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

        var result = await steam.Apps.SetCustomArtworkAsync(
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
            && await steam.Apps.ReadLogoPositionAsync((uint)appId, cancellationToken).ConfigureAwait(false)
                is { Succeeded: true, Value: null })
        {
            var position = await steam.Apps.SaveLogoPositionAsync(
                    (uint)appId, new SteamLogoPosition("BottomLeft", 50, 50), cancellationToken)
                .ConfigureAwait(false);
            if (position.Outcome is SteamClientWriteOutcome.NotSent or SteamClientWriteOutcome.Rejected)
            {
                return new ArtworkResult(
                    "Logo applied, but Steam did not accept its default position: " + position.Error,
                    true);
            }
        }

        return interpreted;
    }

    /// <summary>Resets an artwork slot back to Steam's official art.</summary>
    /// <param name="steam">The session's Steam client.</param>
    /// <param name="appId">The Steam app id.</param>
    /// <param name="asset">Which slot.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The accepted reset result or an explained failure; cancellation propagates.</returns>
    public static async Task<ArtworkResult> ClearAsync(
        SteamClient steam, long appId, ArtworkAsset asset, CancellationToken cancellationToken = default)
    {
        if (asset == ArtworkAsset.Icon)
        {
            return await ClearIconAsync(steam, SteamApps.NormalizeAppId(appId), cancellationToken)
                .ConfigureAwait(false);
        }

        var result = await steam.Apps.ClearCustomArtworkAsync(
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
    /// <returns>The newest matching custom file, the store icon cache path for a Steam title, or null when unavailable.</returns>
    public static string? FindCustomArtFile(long appId, ArtworkAsset asset)
    {
        try
        {
            var steamRoot = Steam.InstallDirectory;
            if (steamRoot is null)
            {
                return null;
            }

            var userdata = Path.Combine(steamRoot, "userdata");
            var normalized = SteamApps.NormalizeAppId(appId);
            if (asset == ArtworkAsset.Icon && !SteamApps.IsShortcutAppId(normalized))
            {
                var storeIcon = Path.Combine(steamRoot, "appcache", "librarycache", $"{normalized}_icon.jpg");
                return File.Exists(storeIcon) ? storeIcon : null;
            }

            if (!Directory.Exists(userdata))
            {
                return null;
            }

            var stem = GridStem(normalized, asset);
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
        switch (result.Outcome)
        {
            case SteamClientWriteOutcome.Applied:
                return new ArtworkResult(okMessage, true);
            case SteamClientWriteOutcome.NotSent:
                return new ArtworkResult("Steam isn't reachable — is it running?");
            case SteamClientWriteOutcome.Unknown:
                // Sent, so it is reported as written; nothing retries it.
                Log.Warn($"Artwork change sent but Steam did not answer: {result.Error}.");
                return new ArtworkResult(okMessage + " Steam did not confirm it.", true);
            default:
                Log.Warn($"Artwork change failed: {result.Error}.");
                return new ArtworkResult(result.Error ?? "Steam rejected the change.");
        }
    }

    private static async Task<ArtworkResult> ApplyIconAsync(
        SteamClient steam, uint appId, byte[] imageBytes, string format, CancellationToken cancellationToken)
    {
        var steamRoot = Steam.InstallDirectory;
        if (steamRoot is null)
        {
            return new ArtworkResult("Steam's installation directory is unavailable.");
        }

        if (SteamApps.IsShortcutAppId(appId))
        {
            var accountId = await steam.Apps.ReadAccountIdAsync(cancellationToken).ConfigureAwait(false);
            if (!accountId.Succeeded)
            {
                return new ArtworkResult("Steam's active userdata account is unavailable.");
            }

            var directory = Path.Combine(steamRoot, "userdata", accountId.Value.ToString(CultureInfo.InvariantCulture),
                "config", "grid");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{GridStem(appId, ArtworkAsset.Icon)}.{format}");
            await AtomicFile.WriteAsync(path, (stream, token) => stream.WriteAsync(imageBytes, token).AsTask(), false,
                cancellationToken).ConfigureAwait(false);
            return Interpret(
                await steam.Apps.SetShortcutIconAsync(appId, path, cancellationToken).ConfigureAwait(false),
                "Shortcut icon applied.");
        }

        var cache = Path.Combine(steamRoot, "appcache", "librarycache");
        Directory.CreateDirectory(cache);
        var storePath = Path.Combine(cache, $"{appId}_icon.jpg");
        await AtomicFile.WriteAsync(storePath, (stream, token) => stream.WriteAsync(imageBytes, token).AsTask(), false,
            cancellationToken).ConfigureAwait(false);
        return Interpret(
            await steam.Apps.RefreshIconAsync(appId, cancellationToken).ConfigureAwait(false),
            "Icon applied.");
    }

    private static async Task<ArtworkResult> ClearIconAsync(
        SteamClient steam, uint appId, CancellationToken cancellationToken)
    {
        if (SteamApps.IsShortcutAppId(appId))
        {
            var accountId = await steam.Apps.ReadAccountIdAsync(cancellationToken).ConfigureAwait(false);
            var result = Interpret(
                await steam.Apps.ClearShortcutIconAsync(appId, cancellationToken).ConfigureAwait(false),
                "Shortcut icon reset.");
            if (result.Succeeded && accountId is { Succeeded: true, Value: var account }
                                 && Steam.InstallDirectory is { } steamRoot)
            {
                var directory = Path.Combine(steamRoot, "userdata",
                    account.ToString(CultureInfo.InvariantCulture), "config", "grid");
                foreach (var extension in GridExtensions)
                {
                    try
                    {
                        File.Delete(Path.Combine(directory, $"{GridStem(appId, ArtworkAsset.Icon)}.{extension}"));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Warn($"Artwork: cleared shortcut icon but its old file could not be deleted: {ex.Message}");
                    }
                }
            }

            return result;
        }

        var read = await steam.Apps.ReadOfficialIconUrlAsync(appId, cancellationToken).ConfigureAwait(false);
        if (read is not { Succeeded: true, Value: { } url })
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

        return ImageHeader.Format(bytes) is { } format
            ? await ApplyIconAsync(steam, appId, bytes, format, cancellationToken).ConfigureAwait(false)
            : new ArtworkResult("Steam's official icon was not an image WSGM can apply.");
    }
}
