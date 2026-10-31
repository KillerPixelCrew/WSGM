using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Writes artwork and logo placement to the running Steam client for every artwork browser session,
///     and words each outcome as the page shows it.
/// </summary>
/// <param name="steam">The Steam client artwork is written to.</param>
/// <param name="readConfiguration">Reads the current artwork configuration a download follows.</param>
/// <remarks>
///     Each write answers with the message and whether it failed, or null when its own cancellation
///     ended it, so the session that started it publishes nothing for a write it abandoned.
/// </remarks>
internal sealed class SteamArtworkWriter(SteamClient steam, Func<ArtworkConfig> readConfiguration)
{
    /// <summary>A 1×1 fully transparent PNG: what "Invisible" applies to a slot.</summary>
    private static readonly byte[] TransparentPixel = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVQYV2NgYAAAAAMAAWgmWQ0AAAAASUVORK5CYII=");

    /// <summary>Downloads an image and applies it to one slot.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="asset">The slot.</param>
    /// <param name="url">The image's address.</param>
    /// <param name="cancellationToken">Ends the download and the write.</param>
    /// <returns>The outcome, or null when cancelled.</returns>
    internal async Task<(string Message, bool Failed)?> ApplyFromUrlAsync(
        uint appId, ArtworkAsset asset, string url, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SteamArtwork
                .ApplyFromUrlAsync(steam, appId, asset, url, readConfiguration(), cancellationToken)
                .ConfigureAwait(false);
            return (result.Detail, !result.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam artwork page: apply failed: {ex.Message}");
            return (ex.Message, true);
        }
    }

    /// <summary>Reads a local image and applies it to one slot.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="asset">The slot.</param>
    /// <param name="path">A fully qualified local image path, already checked by the caller.</param>
    /// <param name="cancellationToken">Ends the read and the write.</param>
    /// <returns>The outcome, or null when cancelled.</returns>
    internal async Task<(string Message, bool Failed)?> ApplyLocalAsync(
        uint appId, ArtworkAsset asset, string path, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return ("The selected image no longer exists.", true);
            }

            if (info.Length is 0 or > ArtworkDownload.MaximumBytes)
            {
                return ("The selected image must be smaller than 16 MB.", true);
            }

            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Steam artwork page: local image could not be read: {ex.Message}");
            return ("The selected image could not be read.", true);
        }

        return await ApplyBytesAsync(appId, asset, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies a fully transparent image to one slot.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="asset">The slot; the caller refuses the icon.</param>
    /// <param name="cancellationToken">Ends the write.</param>
    /// <returns>The outcome, or null when cancelled.</returns>
    internal Task<(string Message, bool Failed)?> ApplyInvisibleAsync(
        uint appId, ArtworkAsset asset, CancellationToken cancellationToken)
    {
        return ApplyBytesAsync(appId, asset, [.. TransparentPixel], cancellationToken);
    }

    /// <summary>Removes one slot's custom artwork.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="asset">The slot.</param>
    /// <param name="cancellationToken">Ends the write.</param>
    /// <returns>The outcome, or null when cancelled.</returns>
    internal async Task<(string Message, bool Failed)?> ClearAsync(
        uint appId, ArtworkAsset asset, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SteamArtwork.ClearAsync(steam, appId, asset, cancellationToken).ConfigureAwait(false);
            return (result.Detail, !result.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam artwork page: reset failed: {ex.Message}");
            return (ex.Message, true);
        }
    }

    /// <summary>Saves where Steam draws the game's logo.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="position">The anchor and size the editor chose.</param>
    /// <param name="cancellationToken">Ends the write.</param>
    /// <returns>The outcome, or null when cancelled.</returns>
    internal async Task<(string Message, bool Failed)?> SaveLogoPositionAsync(
        uint appId, SteamLogoPosition position, CancellationToken cancellationToken)
    {
        try
        {
            var result = await steam.Apps.SaveLogoPositionAsync(appId, position, cancellationToken)
                .ConfigureAwait(false);
            // A position Steam received without answering is published as written.
            var written = result.Outcome is SteamClientWriteOutcome.Applied or SteamClientWriteOutcome.Unknown;
            return (written ? "Logo position saved." : result.Error ?? "Steam rejected the logo position.", !written);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (ex.Message, true);
        }
    }

    /// <summary>Returns the game's logo to Steam's default placement.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="cancellationToken">Ends the write.</param>
    /// <returns>The outcome, or null when cancelled.</returns>
    internal async Task<(string Message, bool Failed)?> ResetLogoPositionAsync(
        uint appId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await steam.Apps.ClearLogoPositionAsync(appId, cancellationToken).ConfigureAwait(false);
            var written = result.Outcome is SteamClientWriteOutcome.Applied or SteamClientWriteOutcome.Unknown;
            return (written ? "Logo position reset." : result.Error ?? "Steam rejected the logo reset.", !written);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (ex.Message, true);
        }
    }

    private async Task<(string Message, bool Failed)?> ApplyBytesAsync(
        uint appId, ArtworkAsset asset, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SteamArtwork.ApplyAsync(steam, appId, asset, bytes, cancellationToken)
                .ConfigureAwait(false);
            return (result.Detail, !result.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam artwork page: apply failed: {ex.Message}");
            return (ex.Message, true);
        }
    }
}
