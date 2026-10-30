using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core.Downloads;

/// <summary>A file a release publishes.</summary>
/// <param name="Name">The file name, which must be a plain name before it is downloaded.</param>
/// <param name="Url">The HTTPS download URL.</param>
/// <param name="Sha256">The published lowercase or uppercase SHA-256, or an empty string when none was published.</param>
/// <param name="Size">The published size in bytes, or zero when unknown.</param>
/// <param name="Revision">A provider revision that changes when the file is replaced in place, or empty.</param>
internal sealed record ReleaseAsset(string Name, string Url, string Sha256 = "", long Size = 0, string Revision = "");

/// <summary>The HTTPS-only client WSGM's package and release owners download through.</summary>
/// <remarks>
///     <para>
///         Every request must use HTTPS. A timeout before response headers arrive is reported as a
///         <see cref="TimeoutException" /> naming the host, distinct from the caller's own cancellation, and a
///         non-success status is an <see cref="HttpRequestException" />.
///     </para>
///     <para>
///         <see cref="DownloadAsync" /> writes through <see cref="VerifiedDownload" />: bounded streaming into a
///         private partial file, the published size and SHA-256 checked when they are known, and an atomic move into
///         place. It never retries; callers own any retry policy.
///     </para>
/// </remarks>
internal sealed class DownloadClient : IDisposable
{
    private readonly HttpClient _http;

    /// <summary>Creates a client.</summary>
    /// <param name="handler">An HTTP handler that stays owned by the caller, the test seam; null for the default.</param>
    internal DownloadClient(HttpMessageHandler? handler)
    {
        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, false);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"WSGM/{UpdateChecker.CurrentVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
    }

    /// <summary>Parses an absolute HTTPS URL.</summary>
    /// <param name="value">The URL.</param>
    /// <returns>The URI.</returns>
    /// <exception cref="InvalidDataException">The value is not an absolute HTTPS URL.</exception>
    internal static Uri Https(string value)
    {
        if (JsonRead.HttpsUrl(value).Length == 0 || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException("Download sources must use HTTPS.");
        }

        return uri;
    }

    /// <summary>Parses an HTTPS URL that relative paths are resolved against.</summary>
    /// <param name="value">The URL.</param>
    /// <returns>The URI, ending with a slash.</returns>
    /// <exception cref="InvalidDataException">The value is not an absolute HTTPS URL.</exception>
    internal static Uri DirectoryUri(string value)
    {
        var uri = Https(value);
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    /// <summary>Whether a published asset name is a plain file name that can't escape its download folder.</summary>
    /// <param name="name">The asset name.</param>
    /// <returns>True for a non-empty name without directories or invalid characters.</returns>
    internal static bool IsSafeAssetName(string name)
    {
        return name.Length > 0 && Path.GetFileName(name) == name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    /// <summary>Starts a GET and returns once the headers of a successful response arrived.</summary>
    /// <param name="url">The HTTPS URL.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, owned by the caller.</returns>
    /// <exception cref="InvalidDataException">The URL is not HTTPS.</exception>
    /// <exception cref="TimeoutException">The headers did not arrive in time.</exception>
    /// <exception cref="HttpRequestException">The request failed or returned a non-success status.</exception>
    internal async Task<HttpResponseMessage> OpenAsync(string url, CancellationToken cancellationToken)
    {
        var uri = Https(url);
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The request to {uri.Host} timed out before response headers arrived.",
                exception);
        }

        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Reads a text resource such as release metadata.</summary>
    /// <param name="url">The HTTPS URL.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The body.</returns>
    internal async Task<string> TextAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await OpenAsync(url, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Downloads an asset to a path once, verified against its published size and SHA-256.</summary>
    /// <param name="asset">The asset; an empty <see cref="ReleaseAsset.Sha256" /> only reports the hash.</param>
    /// <param name="destination">The final path, written only with a verified file.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <param name="copied">Receives the bytes copied so far.</param>
    /// <returns>The lowercase SHA-256 of the downloaded file.</returns>
    /// <exception cref="InvalidDataException">
    ///     The asset name is unsafe, or the size or SHA-256 does not match.
    /// </exception>
    internal async Task<string> DownloadAsync(ReleaseAsset asset, string destination,
        CancellationToken cancellationToken, Action<long>? copied = null)
    {
        if (!IsSafeAssetName(asset.Name))
        {
            throw new InvalidDataException("The published package has an unsafe file name.");
        }

        return await VerifiedDownload.WriteAsync(token => OpenAsync(asset.Url, token), destination, asset.Sha256,
            asset.Size, cancellationToken, copied).ConfigureAwait(false);
    }
}
