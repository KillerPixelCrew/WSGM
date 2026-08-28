using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

internal sealed record SteamUiEndpoint(
    string BrowserId,
    string TargetId,
    SteamUiTargetRole Role,
    Uri SocketUri,
    string Type,
    string Title,
    string Url);

internal interface ISteamUiEndpointDiscovery
{
    Task<SteamUiEndpoint?> DiscoverAsync(
        SteamUiTargetRole role, CancellationToken cancellationToken);
}

internal sealed class SteamUiEndpointDiscovery : ISteamUiEndpointDiscovery
{
    private const int DebugPort = 8080;
    private const int MaximumDiscoveryBytes = 1024 * 1024;
    private static readonly Uri VersionUri = new($"http://127.0.0.1:{DebugPort}/json/version");
    private static readonly Uri TargetsUri = new($"http://127.0.0.1:{DebugPort}/json/list");
    private readonly HttpClient _httpClient;

    internal SteamUiEndpointDiscovery()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
    {
    }

    internal SteamUiEndpointDiscovery(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<SteamUiEndpoint?> DiscoverAsync(
        SteamUiTargetRole role, CancellationToken cancellationToken)
    {
        if (!IsSteamPortOwner())
        {
            return null;
        }

        using var version = await ReadBoundedJsonAsync(VersionUri, cancellationToken)
            .ConfigureAwait(false);
        var browserUrl = ReadString(version.RootElement, "webSocketDebuggerUrl");
        if (!SteamCef.IsAllowedDebuggerUrl(browserUrl))
        {
            throw new InvalidDataException("Steam UI browser endpoint was not loopback port 8080.");
        }

        var browserId = new Uri(browserUrl!, UriKind.Absolute)
            .AbsolutePath.TrimEnd('/').Split('/').Last();
        using var targets = await ReadBoundedJsonAsync(TargetsUri, cancellationToken)
            .ConfigureAwait(false);
        if (targets.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Steam UI target list was not an array.");
        }

        SteamUiEndpoint? match = null;
        foreach (var target in targets.RootElement.EnumerateArray())
        {
            if (!TryReadTarget(target, role, browserId, out var candidate))
            {
                continue;
            }

            if (match is not null)
            {
                throw new InvalidDataException($"Steam UI reported multiple {role} targets.");
            }
            match = candidate;
        }
        return match;
    }

    internal static bool MatchesTarget(
        SteamUiTargetRole role, string type, string title, string url) =>
        role switch
        {
            SteamUiTargetRole.SharedJsContext =>
                type == "page"
                && title == "SharedJSContext"
                && Uri.TryCreate(url, UriKind.Absolute, out var sharedUri)
                && sharedUri.Scheme == Uri.UriSchemeHttps
                && sharedUri.Host == "steamloopback.host",
            SteamUiTargetRole.QuickAccess =>
                type == "page"
                && title == "QuickAccess"
                && url.StartsWith("about:blank?", StringComparison.Ordinal)
                && url.Contains("createflags", StringComparison.Ordinal)
                && url.Contains("browserviewpopup", StringComparison.Ordinal)
                && url.Contains("openerid", StringComparison.Ordinal),
            _ => false,
        };

    private static bool TryReadTarget(
        JsonElement target,
        SteamUiTargetRole role,
        string browserId,
        out SteamUiEndpoint? endpoint)
    {
        endpoint = null;
        var id = ReadString(target, "id");
        var type = ReadString(target, "type");
        var title = ReadString(target, "title");
        var url = ReadString(target, "url");
        var socketUrl = ReadString(target, "webSocketDebuggerUrl");
        if (string.IsNullOrEmpty(id)
            || type is null
            || title is null
            || url is null
            || !MatchesTarget(role, type, title, url)
            || !SteamCef.IsAllowedDebuggerUrl(socketUrl))
        {
            return false;
        }

        endpoint = new SteamUiEndpoint(
            browserId, id, role, new Uri(socketUrl!, UriKind.Absolute), type, title, url);
        return true;
    }

    private static string? ReadString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : null;

    private async Task<JsonDocument> ReadBoundedJsonAsync(
        Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumDiscoveryBytes)
        {
            throw new InvalidDataException("Steam UI discovery response exceeded its byte limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (destination.Length + read > MaximumDiscoveryBytes)
            {
                throw new InvalidDataException("Steam UI discovery response exceeded its byte limit.");
            }
            destination.Write(buffer, 0, read);
        }
        destination.Position = 0;
        return await JsonDocument.ParseAsync(destination, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsSteamPortOwner() =>
        SteamCef.IsSteamPortOwner(NativeTcp.ListListeners(), static processId =>
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.ProcessName;
            }
            catch
            {
                return null;
            }
        });
}
