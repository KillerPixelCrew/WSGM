using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Downloads one artwork image.</summary>
/// <remarks>
///     Shared by every provider rather than owned by one: an image on SteamGridDB's content network,
///     on Steam's store network or on Screenscraper's media endpoint is fetched the same way, with the
///     same HTTPS rule and the same 16 MiB cap, read through <see cref="BoundedHttp" /> like every
///     provider's JSON answer. A provider whose images count against its own allowance wraps this in its
///     own pacing (<see cref="IArtworkProvider.DownloadAsync" />).
/// </remarks>
internal static class ArtworkDownload
{
    /// <summary>The largest image accepted.</summary>
    public const int MaximumBytes = 16 * 1024 * 1024;

    /// <summary>The largest provider JSON answer accepted, read through <see cref="BoundedHttp" />.</summary>
    /// <remarks>A search or asset page is a few hundred KB at most; this bounds a hostile or broken answer.</remarks>
    internal const int MaximumJsonBytes = 4 * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Downloads an image's bytes.</summary>
    /// <param name="url">The image's HTTPS address.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The image's bytes.</returns>
    /// <exception cref="ArtworkProviderException">
    ///     The address was not HTTPS, the server refused, the image was larger than
    ///     <see cref="MaximumBytes" />, or the transfer failed. Every failure carries a message for the
    ///     page; none is answered as an empty image.
    /// </exception>
    public static Task<byte[]> GetAsync(string url, CancellationToken cancellationToken)
    {
        return GetAsync(url, Http, cancellationToken);
    }

    /// <summary>Downloads HTTPS artwork with a caller-supplied HTTP transport and the shared size/stall limits.</summary>
    /// <param name="url">Image address to fetch.</param>
    /// <param name="client">Borrowed client; this method disposes the response, not the client.</param>
    /// <param name="cancellationToken">Cancels the request and body read.</param>
    /// <returns>Downloaded bytes; image format and dimensions are checked separately before applying them.</returns>
    internal static async Task<byte[]> GetAsync(string url, HttpClient client, CancellationToken cancellationToken)
    {
        if (!HttpUrls.IsHttps(url))
        {
            throw new ArtworkProviderException("Artwork URL was not a secure HTTPS address.");
        }

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return await ReadAsync(response, url, cancellationToken).ConfigureAwait(false);
        }
        catch (ArtworkProviderException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork image download failed ({ArtworkUrls.Redact(url)}): {ex.Message}");
            throw new ArtworkProviderException("Could not download the artwork image.");
        }
    }

    /// <summary>Reads a provider-owned image response using the shared download bounds.</summary>
    internal static async Task<byte[]> ReadAsync(HttpResponseMessage response, string url,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            Log.Warn($"Artwork image download answered {(int)response.StatusCode} ({ArtworkUrls.Redact(url)}).");
            throw new ArtworkProviderException($"The artwork server answered HTTP {(int)response.StatusCode}.");
        }

        using var body = await BoundedHttp.ReadAsync(response.Content, MaximumBytes,
            () => new ArtworkProviderException("Artwork is larger than the 16 MB safety limit."),
            cancellationToken).ConfigureAwait(false);
        return body.ToArray();
    }
}
