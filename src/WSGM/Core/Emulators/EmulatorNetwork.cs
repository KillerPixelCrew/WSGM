using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Release metadata and package transport shared by the reviewed source adapters.</summary>
internal sealed class EmulatorNetwork : IDisposable
{
    private readonly HttpClient _http;

    public EmulatorNetwork(HttpMessageHandler? handler)
    {
        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, false);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WSGM-EmulatorManager/2.1");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    public static Uri Https(string value)
    {
        if (JsonRead.HttpsUrl(value).Length == 0 || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException("Emulator sources must use HTTPS.");
        }

        return uri;
    }

    public static Uri DirectoryUri(string value)
    {
        var uri = Https(value);
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    public async Task<HttpResponseMessage> OpenAsync(string url, CancellationToken cancellationToken)
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
            throw new TimeoutException($"The emulator request to {uri.Host} timed out before response headers arrived.",
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

    public async Task<string> TextAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await OpenAsync(url, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
