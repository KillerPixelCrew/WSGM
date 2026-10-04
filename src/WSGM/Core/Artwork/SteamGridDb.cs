using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>One artwork candidate from SteamGridDB.</summary>
/// <param name="Id">SteamGridDB asset id.</param>
/// <param name="Url">Full-resolution image URL.</param>
/// <param name="Thumb">Thumbnail URL (for the picker grid).</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Extension">Verified static image format, <c>png</c> or <c>jpg</c>.</param>
/// <param name="Author">Artwork author.</param>
/// <param name="Style">SteamGridDB style identifier.</param>
/// <param name="Notes">SteamGridDB notes.</param>
/// <param name="Animated">Whether the result is animated.</param>
/// <param name="Nsfw">Whether the result is adult-tagged.</param>
/// <param name="Humor">Whether the result is humor-tagged.</param>
/// <param name="Epilepsy">Whether the result is flashing-content-tagged.</param>
// ReSharper disable once NotAccessedPositionalProperty.Global
internal sealed record SgdbAsset(
    int Id,
    string Url,
    string Thumb,
    int Width,
    int Height,
    string Extension,
    string? Author = null,
    string? Style = null,
    string? Notes = null,
    bool Animated = false,
    bool Nsfw = false,
    bool Humor = false,
    bool Epilepsy = false);

/// <summary>A SteamGridDB request failed for a reason the UI should surface.</summary>
/// <param name="message">A user-facing message.</param>
internal sealed class SteamGridDbException(string message) : ArtworkProviderException(message);

/// <summary>A game match from a SteamGridDB title search.</summary>
/// <param name="Id">SteamGridDB game id.</param>
/// <param name="Name">Game name.</param>
internal sealed record SgdbGame(int Id, string Name);

/// <summary>One official Steam store asset described by SteamGridDB platform metadata.</summary>
internal sealed record SgdbOfficialAsset(string Label, string Url, int Width, int Height, string Extension);

/// <summary>
///     Read-only client for the SteamGridDB v2 REST API: title search and per-slot
///     asset listing. Its images sit on an open content network, so <see cref="ArtworkDownload" />
///     fetches them like any other. Uses only <see cref="HttpClient" /> and <see cref="JsonDocument" />.
///     Auth is a bearer key the user sets in Settings (<see cref="ResolveKey" />); there is no
///     bundled key (SteamGridDB rejects the decky public key). Applying the chosen image
///     is <see cref="SteamArtwork" />'s job; this class only fetches.
/// </summary>
internal sealed partial class SteamGridDbProvider
{
    private const string ApiBase = "https://www.steamgriddb.com/api/v2";

    private const int PageSize = 50;

    /// <summary>Where a user gets a free SteamGridDB API key (shown in Settings).</summary>
    public const string KeyPageUrl = "https://www.steamgriddb.com/profile/preferences/api";

    /// <summary>How many times one request is attempted before it is reported as failed.</summary>
    private const int MaximumAttempts = 3;

    /// <summary>The longest a <c>Retry-After</c> may hold a page.</summary>
    private static readonly TimeSpan MaximumRetryWait = TimeSpan.FromSeconds(10);

    /// <summary>Four requests in flight, and the last 256 answers remembered for the session.</summary>
    /// <remarks>
    ///     Four, not one. Serializing every request made a Game Library scan of twenty titles take
    ///     minutes, six round trips per title one after another. A 429 still backs off by its
    ///     <c>Retry-After</c>, so a burst that does reach the limit slows down rather than fails.
    /// </remarks>
    private readonly ArtworkRequestGate Gate;

    private readonly HttpClient Http;

    internal SteamGridDbProvider(HttpMessageHandler? handler = null, ArtworkRequestGate? gate = null)
    {
        Http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        Http.Timeout = TimeSpan.FromSeconds(20);
        Gate = gate ?? new ArtworkRequestGate(4, 256);
    }

    /// <summary>
    ///     The user's configured API key (trimmed), or empty. There is no bundled
    ///     key — SteamGridDB rejects the decky public key — so the user must set their own
    ///     free key in Settings (see <see cref="KeyPageUrl" />).
    /// </summary>
    /// <param name="config">The loaded configuration.</param>
    public static string ResolveKey(ArtworkConfig config)
    {
        return config.SteamGridDbApiKey.Trim();
    }

    /// <summary>Searches SteamGridDB for games by title (autocomplete).</summary>
    /// <param name="term">The search term.</param>
    /// <param name="key">The bearer API key (see <see cref="ResolveKey" />).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<IReadOnlyList<SgdbGame>> SearchGamesAsync(
        string term, string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var url = $"{ApiBase}/search/autocomplete/{Uri.EscapeDataString(term.Trim())}";
        var root = await GetAsync(url, key, cancellationToken).ConfigureAwait(false);
        if (root is null || !root.Value.TryGetProperty("data", out var data)
                         || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<SgdbGame>();
        foreach (var game in data.EnumerateArray())
        {
            if (game.TryGetProperty("id", out var id) && id.TryGetInt32(out var gameId))
            {
                list.Add(new SgdbGame(gameId, game.TryGetProperty("name", out var n)
                    ? n.GetString() ?? ""
                    : ""));
            }
        }

        return list;
    }



    /// <summary>Lists a filtered, zero-based page for a Steam app.</summary>
    public Task<ArtworkPage> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, string key, ArtworkQuery query,
        CancellationToken cancellationToken = default)
    {
        return GetAssetsAsync(asset, "steam", steamAppId.ToString(CultureInfo.InvariantCulture), key,
            cancellationToken, query);
    }



    /// <summary>Resolves official Steam assets for a SteamGridDB game.</summary>
    public async Task<IReadOnlyList<SgdbOfficialAsset>> GetOfficialAssetsForGameAsync(
        ArtworkAsset asset, int sgdbGameId, string key, CancellationToken cancellationToken = default)
    {
        var root = await GetAsync(
                $"{ApiBase}/games/id/{sgdbGameId.ToString(CultureInfo.InvariantCulture)}?platformdata=steam",
                key,
                cancellationToken)
            .ConfigureAwait(false);
        return root is null ? [] : ParseOfficialAssets(root.Value, asset);
    }

    /// <summary>Resolves official Steam assets for a Steam application id.</summary>
    public async Task<IReadOnlyList<SgdbOfficialAsset>> GetOfficialAssetsForSteamAppAsync(
        ArtworkAsset asset, uint steamAppId, string key, CancellationToken cancellationToken = default)
    {
        var game = await GetAsync(
                $"{ApiBase}/games/steam/{steamAppId.ToString(CultureInfo.InvariantCulture)}",
                key,
                cancellationToken)
            .ConfigureAwait(false);
        if (game is null || !game.Value.TryGetProperty("data", out var data))
        {
            return [];
        }

        if (data.ValueKind == JsonValueKind.Array)
        {
            data = data.EnumerateArray().FirstOrDefault();
        }

        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("id", out var id)
                                                   || !id.TryGetInt32(out var sgdbGameId))
        {
            return [];
        }

        return await GetOfficialAssetsForGameAsync(asset, sgdbGameId, key, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Lists a filtered, zero-based page for a SteamGridDB game.</summary>
    public Task<ArtworkPage> GetAssetsForGameAsync(
        ArtworkAsset asset, int sgdbGameId, string key, ArtworkQuery query,
        CancellationToken cancellationToken = default)
    {
        return GetAssetsAsync(asset, "game", sgdbGameId.ToString(CultureInfo.InvariantCulture), key,
            cancellationToken, query);
    }

    private async Task<ArtworkPage> GetAssetsAsync(
        ArtworkAsset asset, string idKind, string id, string key, CancellationToken cancellationToken,
        ArtworkQuery query)
    {
        var segment = asset switch
        {
            ArtworkAsset.Grid or ArtworkAsset.Wide => "grids",
            ArtworkAsset.Hero => "heroes",
            ArtworkAsset.Logo => "logos",
            ArtworkAsset.Icon => "icons",
            _ => "grids"
        };
        var parameters = new List<string>();
        parameters.Add($"page={Math.Max(0, query.Page).ToString(CultureInfo.InvariantCulture)}");
        parameters.Add("types=" + EncodeCsv(
        [
            .. new[] { query.Static ? "static" : null, query.Animated ? "animated" : null }
                .Where(value => value is not null).Select(value => value!)
        ]));
        AddCsv(parameters, "styles", query.Styles);
        AddCsv(parameters, "dimensions", query.Dimensions);
        AddCsv(parameters, "mimes", query.Mimes);
        parameters.Add("nsfw=" + (query.Adult ? "any" : "false"));
        parameters.Add("humor=" + (query.Untagged ? query.Humor ? "any" : "false" : "any"));
        parameters.Add("epilepsy=" + (query.Untagged ? query.Epilepsy ? "any" : "false" : "any"));
        if (!query.Untagged)
        {
            var tags = new[]
                {
                    query.Adult ? "nsfw" : null,
                    query.Humor ? "humor" : null,
                    query.Epilepsy ? "epilepsy" : null
                }
                .Where(value => value is not null)
                .Select(value => value!)
                .ToArray();
            AddCsv(parameters, "oneoftag", tags);
        }

        var url = $"{ApiBase}/{segment}/{idKind}/{id}?{string.Join('&', parameters)}";

        var root = await GetAsync(url, key, cancellationToken).ConfigureAwait(false);
        if (root is null || !root.Value.TryGetProperty("data", out var data)
                         || data.ValueKind != JsonValueKind.Array)
        {
            Log.Warn($"SteamGridDB answered {url} without a result list.");
            return new ArtworkPage([], false);
        }

        var list = new List<SgdbAsset>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var full = urlEl.GetString() ?? "";
            var thumb = item.TryGetProperty("thumb", out var t) ? t.GetString() ?? full : full;
            var w = item.TryGetProperty("width", out var wi) && wi.TryGetInt32(out var wv) ? wv : 0;
            var h = item.TryGetProperty("height", out var he) && he.TryGetInt32(out var hv) ? hv : 0;
            var assetId = item.TryGetProperty("id", out var ai) && ai.TryGetInt32(out var av) ? av : 0;
            var extension = ImageExtension(full);
            if (full.Length > 0 && extension is not null)
            {
                var author = item.TryGetProperty("author", out var authorElement)
                             && authorElement.ValueKind == JsonValueKind.Object
                             && authorElement.TryGetProperty("name", out var authorName)
                    ? authorName.GetString()
                    : null;
                var style = item.TryGetProperty("style", out var styleElement)
                    ? styleElement.GetString()
                    : null;
                var animated = item.TryGetProperty("type", out var typeElement)
                               && typeElement.GetString() == "animated";
                list.Add(new SgdbAsset(
                    assetId,
                    full,
                    thumb,
                    w,
                    h,
                    extension,
                    author,
                    style,
                    item.TryGetProperty("notes", out var notesElement) ? notesElement.GetString() : null,
                    animated,
                    ReadBoolean(item, "nsfw"),
                    ReadBoolean(item, "humor"),
                    ReadBoolean(item, "epilepsy")));
            }
        }

        if (list.Count == 0 && data.GetArrayLength() > 0)
        {
            Log.Warn($"SteamGridDB returned {data.GetArrayLength()} results for {url}, "
                     + "none with an https image address of a known format.");
        }

        return new ArtworkPage(Convert(list), data.GetArrayLength() == PageSize);
    }

    private static void AddCsv(List<string> parameters, string name, IReadOnlyList<string>? values)
    {
        if (values is { Count: > 0 })
        {
            parameters.Add(name + "=" + EncodeCsv(values));
        }
    }

    private static IReadOnlyList<SgdbOfficialAsset> ParseOfficialAssets(JsonElement root, ArtworkAsset asset)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                                                       || !data.TryGetProperty("external_platform_data",
                                                           out var platforms)
                                                       || platforms.ValueKind != JsonValueKind.Object
                                                       || !platforms.TryGetProperty("steam", out var steamEntries)
                                                       || steamEntries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<SgdbOfficialAsset> assets = [];
        foreach (var steam in steamEntries.EnumerateArray())
        {
            if (!steam.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                                                               || !uint.TryParse(idElement.GetString(),
                                                                   NumberStyles.None, CultureInfo.InvariantCulture,
                                                                   out var steamAppId)
                                                               || !steam.TryGetProperty("metadata", out var metadata)
                                                               || metadata.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var timestamp = metadata.TryGetProperty("store_asset_mtime", out var mtime)
                            && mtime.TryGetInt64(out var value)
                ? "?t=" + value.ToString(CultureInfo.InvariantCulture)
                : "";
            if (asset == ArtworkAsset.Icon)
            {
                if (metadata.TryGetProperty("clienticon", out var icon)
                    && icon.GetString() is { Length: > 0 } iconHash)
                {
                    assets.Add(new SgdbOfficialAsset(
                        "Steam icon",
                        $"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{steamAppId.ToString(CultureInfo.InvariantCulture)}/{Uri.EscapeDataString(iconHash)}.ico",
                        32,
                        32,
                        "ico"));
                }

                continue;
            }

            var (property, width, height) = asset switch
            {
                ArtworkAsset.Grid => ("library_capsule_full", 600, 900),
                ArtworkAsset.Wide => ("header_image_full", 920, 430),
                ArtworkAsset.Hero => ("library_hero_full", 3840, 1240),
                ArtworkAsset.Logo => ("library_logo_full", 0, 0),
                _ => ("", 0, 0)
            };
            if (property.Length == 0 || !metadata.TryGetProperty(property, out var full)
                                     || full.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var images = full;
            if (asset != ArtworkAsset.Wide)
            {
                if (!full.TryGetProperty("image2x", out images) || images.ValueKind != JsonValueKind.Object)
                {
                    if (!full.TryGetProperty("image", out images) || images.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                }
            }

            foreach (var language in images.EnumerateObject())
            {
                if (language.Value.ValueKind != JsonValueKind.String
                    || language.Value.GetString() is not { Length: > 0 } fileName)
                {
                    continue;
                }

                if (asset == ArtworkAsset.Wide && fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = fileName[..^4] + "_2x.jpg";
                }

                var extension = ImageExtension(fileName);
                if (extension is null)
                {
                    continue;
                }

                assets.Add(new SgdbOfficialAsset(
                    language.Name,
                    $"https://shared.steamstatic.com/store_item_assets/steam/apps/{steamAppId.ToString(CultureInfo.InvariantCulture)}/{Uri.EscapeDataString(fileName)}{timestamp}",
                    width,
                    height,
                    extension));
            }
        }

        return assets;
    }

    private static string EncodeCsv(IEnumerable<string> values)
    {
        return Uri.EscapeDataString(string.Join(',', values));
    }

    private static bool ReadBoolean(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value)
               && value.ValueKind is JsonValueKind.True or JsonValueKind.Number
               && (value.ValueKind == JsonValueKind.True || (value.TryGetInt32(out var number) && number != 0));
    }

    /// <summary>Forgets every cached response.</summary>
    /// <remarks>
    ///     Called when the API key changes, so a key that was rejected is not remembered as a
    ///     working one and the next search really asks.
    /// </remarks>
    public void ResetCache()
    {
        Gate.Clear();
    }

    /// <summary>Whether a response is worth asking again for.</summary>
    /// <param name="status">The status the service answered with.</param>
    /// <returns>True when the same request could plausibly succeed.</returns>
    /// <remarks>
    ///     A 4xx other than 429 and 408 is the request itself being wrong, and repeating it only
    ///     spends the user's rate limit to be told the same thing.
    /// </remarks>
    internal static bool IsTransient(HttpStatusCode status)
    {
        return status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
               || (int)status >= 500;
    }

    /// <summary>How long the service asked us to wait, bounded.</summary>
    /// <param name="response">The throttled response.</param>
    /// <returns>The wait, or null when it named none.</returns>
    /// <remarks>
    ///     Honoured rather than guessed at, because the service knows its own window. Bounded
    ///     because an unbounded <c>Retry-After</c> would hang the page on a spinner for as long as
    ///     a stranger's header says.
    /// </remarks>
    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var after = response.Headers.RetryAfter;
        var delay = after?.Delta
                    ?? (after?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return delay is null || delay <= TimeSpan.Zero
            ? null
            : delay > MaximumRetryWait
                ? MaximumRetryWait
                : delay;
    }

    private Task<JsonElement?> GetAsync(string url, string key, CancellationToken cancellationToken)
    {
        // The key travels in a header, so the URL alone identifies the answer and holds nothing secret.
        return Gate.CachedAsync(url, token => FetchAsync(url, key, token), cancellationToken);
    }

    private async Task<JsonElement?> FetchAsync(
        string url, string key, CancellationToken cancellationToken)
    {
        for (var attempt = 1;; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await Http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < MaximumAttempts && IsTransient(response.StatusCode))
                    {
                        var wait = RetryAfter(response) ?? TimeSpan.FromMilliseconds(400 * attempt);
                        Log.Warn($"SteamGridDB {(int)response.StatusCode} for {url}; "
                                 + $"retrying in {wait.TotalMilliseconds:F0} ms.");
                        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    Log.Warn($"SteamGridDB {(int)response.StatusCode} for {url}.");
                    throw new SteamGridDbException(response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                            => "SteamGridDB rejected the API key.",
                        HttpStatusCode.TooManyRequests
                            => "SteamGridDB rate limit reached. Try again later.",
                        HttpStatusCode.NotFound
                            => "SteamGridDB does not know this game. "
                               + "Find it by name in the Filter panel's Game search.",
                        _ => $"SteamGridDB returned HTTP {(int)response.StatusCode}."
                    });
                }

                using var body = await BoundedHttp.ReadAsync(response.Content, ArtworkDownload.MaximumJsonBytes,
                    () => new SteamGridDbException("SteamGridDB's answer is larger than expected."),
                    cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                // Clone so the element survives disposal of the document.
                return document.RootElement.Clone();
            }
            catch (SteamGridDbException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt >= MaximumAttempts)
                {
                    Log.Warn($"SteamGridDB request failed ({url}): {ex.Message}");
                    throw new SteamGridDbException("Could not contact SteamGridDB.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>The image format a URL's own suffix declares, or null for one no slot takes.</summary>
    /// <param name="url">The image URL.</param>
    /// <returns><c>png</c>, <c>jpg</c>, <c>webp</c> or <c>ico</c>, or null.</returns>
    internal static string? ImageExtension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "jpg",
            ".png" => "png",
            ".webp" => "webp",
            ".ico" => "ico",
            _ => null
        };
    }
}
