using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Downloads one artwork image.</summary>
/// <remarks>
///     Shared by every provider rather than owned by one: an image on SteamGridDB's content network,
///     on Steam's store network or on Screenscraper's media endpoint is fetched the same way, with the
///     same HTTPS rule and the same 16 MiB cap. A provider whose images count against its own
///     allowance wraps this in its own pacing (<see cref="IArtworkProvider.DownloadAsync" />).
/// </remarks>
public static class ArtworkDownload
{
    /// <summary>The largest image accepted.</summary>
    public const int MaximumBytes = 16 * 1024 * 1024;

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
    public static async Task<byte[]> GetAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArtworkProviderException("Artwork URL was not a secure HTTPS address.");
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var transferToken = deadline.Token;
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                transferToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"Artwork image download answered {(int)response.StatusCode} ({ArtworkUrls.Redact(url)}).");
                throw new ArtworkProviderException(
                    $"The artwork server answered HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength is > MaximumBytes)
            {
                throw new ArtworkProviderException("Artwork is larger than the 16 MB safety limit.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(transferToken)
                .ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await input.ReadAsync(buffer, transferToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MaximumBytes)
                {
                    throw new ArtworkProviderException("Artwork is larger than the 16 MB safety limit.");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
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
}
