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

/// <summary>
///     An artwork slot. The numeric values are Steam's own <c>eAssetType</c>
///     (capsule/portrait = 0, hero = 1, logo = 2, wide capsule = 3, icon = 4) so they pass
///     straight into <see cref="SteamArtwork" />'s <c>SetCustomArtworkForApp</c> call.
/// </summary>
public enum ArtworkAsset
{
    /// <summary>Portrait capsule (600×900).</summary>
    Grid = 0,

    /// <summary>Hero banner (1920×620).</summary>
    Hero = 1,

    /// <summary>Transparent logo.</summary>
    Logo = 2,

    /// <summary>Wide capsule (460×215).</summary>
    Wide = 3,

    /// <summary>Icon.</summary>
    Icon = 4
}

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
public sealed record SgdbAsset(
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
public sealed class SteamGridDbException : Exception
{
    /// <summary>Creates a request failure with a user-facing message.</summary>
    public SteamGridDbException(string message) : base(message)
    {
    }
}

/// <summary>A game match from a SteamGridDB title search.</summary>
/// <param name="Id">SteamGridDB game id.</param>
/// <param name="Name">Game name.</param>
public sealed record SgdbGame(int Id, string Name);

/// <summary>One official Steam store asset described by SteamGridDB platform metadata.</summary>
public sealed record SgdbOfficialAsset(string Label, string Url, int Width, int Height, string Extension);

/// <summary>
///     Read-only client for the SteamGridDB v2 REST API: title search and per-slot
///     asset listing, plus raw image download. Uses only <see cref="HttpClient" /> and <see cref="JsonDocument" />.
///     Auth is a bearer key the user sets in Settings (<see cref="ResolveKey" />); there is no
///     bundled key (SteamGridDB rejects the decky public key). Applying the chosen image
///     is <see cref="SteamArtwork" />'s job; this class only fetches.
/// </summary>
public static class SteamGridDb
{
    private const string ApiBase = "https://www.steamgriddb.com/api/v2";

    /// <summary>Where a user gets a free SteamGridDB API key (shown in Settings).</summary>
    public const string KeyPageUrl = "https://www.steamgriddb.com/profile/preferences/api";

    // MaxResponseContentBufferSize bounds the BUFFERED reads — the JSON endpoints, whose
    // bodies are a few hundred KB at most — so a hostile or malfunctioning response
    // cannot buffer without limit into a string on a memory-constrained handheld. It
    // does not apply to the image download, which streams with ResponseHeadersRead and
    // enforces its own 16 MB counted cap.
    private const int MaxJsonResponseBytes = 4 * 1024 * 1024;

    /// <summary>How many times one request is attempted before it is reported as failed.</summary>
    private const int MaximumAttempts = 3;

    /// <summary>How many responses are remembered for the rest of the session.</summary>
    private const int MaximumCachedResponses = 256;

    /// <summary>The longest a <c>Retry-After</c> may hold a page.</summary>
    private static readonly TimeSpan MaximumRetryWait = TimeSpan.FromSeconds(10);

    /// <summary>One request in flight, so bulk work cannot race itself into the rate limit.</summary>
    private static readonly SemaphoreSlim Requests = new(1, 1);

    private static readonly Lock CacheGate = new();
    private static readonly Dictionary<string, JsonElement> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> CacheOrder = new();

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = MaxJsonResponseBytes
    };

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
    public static async Task<IReadOnlyList<SgdbGame>> SearchGamesAsync(
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

    /// <summary>
    ///     Lists artwork candidates for a Steam app id in the given slot. Grid vs
    ///     Wide are the same SteamGridDB endpoint filtered by dimensions.
    /// </summary>
    /// <param name="asset">Which artwork slot.</param>
    /// <param name="steamAppId">The Steam app id.</param>
    /// <param name="key">The bearer API key.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<IReadOnlyList<SgdbAsset>> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, string key, CancellationToken cancellationToken = default)
    {
        return GetAssetsAsync(asset, "steam", steamAppId.ToString(CultureInfo.InvariantCulture), key,
            cancellationToken);
    }

    /// <summary>Lists a filtered, zero-based page for a Steam app.</summary>
    public static Task<IReadOnlyList<SgdbAsset>> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, string key, ArtworkQuery query,
        CancellationToken cancellationToken = default)
    {
        return GetAssetsAsync(asset, "steam", steamAppId.ToString(CultureInfo.InvariantCulture), key,
            cancellationToken, query);
    }

    /// <summary>
    ///     Lists artwork candidates for a SteamGridDB game id (used when a Steam
    ///     app has no direct SteamGridDB mapping and the user searched by title).
    /// </summary>
    /// <param name="asset">Which artwork slot.</param>
    /// <param name="sgdbGameId">The SteamGridDB game id.</param>
    /// <param name="key">The bearer API key.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<IReadOnlyList<SgdbAsset>> GetAssetsForGameAsync(
        ArtworkAsset asset, int sgdbGameId, string key, CancellationToken cancellationToken = default)
    {
        return GetAssetsAsync(asset, "game", sgdbGameId.ToString(CultureInfo.InvariantCulture), key,
            cancellationToken);
    }

    /// <summary>Resolves official Steam assets for a SteamGridDB game.</summary>
    public static async Task<IReadOnlyList<SgdbOfficialAsset>> GetOfficialAssetsForGameAsync(
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
    public static async Task<IReadOnlyList<SgdbOfficialAsset>> GetOfficialAssetsForSteamAppAsync(
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
    public static Task<IReadOnlyList<SgdbAsset>> GetAssetsForGameAsync(
        ArtworkAsset asset, int sgdbGameId, string key, ArtworkQuery query,
        CancellationToken cancellationToken = default)
    {
        return GetAssetsAsync(asset, "game", sgdbGameId.ToString(CultureInfo.InvariantCulture), key,
            cancellationToken, query);
    }

    private static async Task<IReadOnlyList<SgdbAsset>> GetAssetsAsync(
        ArtworkAsset asset, string idKind, string id, string key, CancellationToken cancellationToken,
        ArtworkQuery? query = null)
    {
        var (segment, dimensions) = asset switch
        {
            ArtworkAsset.Grid => ("grids", "600x900"),
            ArtworkAsset.Wide => ("grids", "460x215"),
            ArtworkAsset.Hero => ("heroes", null),
            ArtworkAsset.Logo => ("logos", null),
            ArtworkAsset.Icon => ("icons", null),
            _ => ("grids", null)
        };
        var parameters = new List<string>();
        if (query is null)
        {
            parameters.Add("types=static");
            if (dimensions is not null)
            {
                parameters.Add($"dimensions={dimensions}");
            }
        }
        else
        {
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
        }

        var url = $"{ApiBase}/{segment}/{idKind}/{id}?{string.Join('&', parameters)}";

        var root = await GetAsync(url, key, cancellationToken).ConfigureAwait(false);
        if (root is null || !root.Value.TryGetProperty("data", out var data)
                         || data.ValueKind != JsonValueKind.Array)
        {
            return [];
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

        return list;
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

        return assets.Take(24).ToArray();
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

    /// <summary>
    ///     Downloads raw image bytes from a URL (SteamGridDB CDN or Steam's own
    ///     store CDN), capped at 16 MB. There is no null failure result: every failure —
    ///     a non-HTTPS URL, an HTTP error, an oversized body, a transport fault — throws
    ///     <see cref="SteamGridDbException" /> carrying a user-facing message, so callers
    ///     must wrap the call. The nullable return type is defensive only.
    /// </summary>
    /// <param name="url">The image URL.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static async Task<byte[]?> DownloadImageAsync(
        string url, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new SteamGridDbException("Artwork URL was not a secure HTTPS address.");
            }

            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            const int maxBytes = 16 * 1024 * 1024;
            if (response.Content.Headers.ContentLength is > maxBytes)
            {
                throw new SteamGridDbException("Artwork is larger than the 16 MB safety limit.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > maxBytes)
                {
                    throw new SteamGridDbException("Artwork is larger than the 16 MB safety limit.");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
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
            Log.Warn($"SteamGridDB image download failed ({url}): {ex.Message}");
            throw new SteamGridDbException("Could not download the artwork image.");
        }
    }

    /// <summary>Forgets every cached response.</summary>
    /// <remarks>
    ///     Called when the API key changes, so a key that was rejected is not remembered as a
    ///     working one and the next search really asks.
    /// </remarks>
    public static void ResetCache()
    {
        lock (CacheGate)
        {
            Cache.Clear();
            CacheOrder.Clear();
        }
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

    private static async Task<JsonElement?> GetAsync(
        string url, string key, CancellationToken cancellationToken)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(url, out var hit))
            {
                return hit;
            }
        }

        // One request in flight at a time. Bulk work asks for five assets of the same game at once,
        // and firing those in parallel is what runs a user into the rate limit they then have to
        // wait out.
        await Requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (CacheGate)
            {
                if (Cache.TryGetValue(url, out var hit))
                {
                    return hit;
                }
            }

            var element = await FetchAsync(url, key, cancellationToken).ConfigureAwait(false);
            if (element is not null)
            {
                Remember(url, element.Value);
            }

            return element;
        }
        finally
        {
            Requests.Release();
        }
    }

    private static async Task<JsonElement?> FetchAsync(
        string url, string key, CancellationToken cancellationToken)
    {
        for (var attempt = 1;; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await Http.SendAsync(request, cancellationToken)
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
                        _ => $"SteamGridDB returned HTTP {(int)response.StatusCode}."
                    });
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
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

    /// <summary>Keeps a response for the rest of the session, evicting the oldest past the bound.</summary>
    /// <param name="url">The request this answered.</param>
    /// <param name="element">What it answered.</param>
    private static void Remember(string url, JsonElement element)
    {
        lock (CacheGate)
        {
            if (!Cache.TryAdd(url, element))
            {
                return;
            }

            CacheOrder.Enqueue(url);
            while (CacheOrder.Count > MaximumCachedResponses && CacheOrder.TryDequeue(out var oldest))
            {
                Cache.Remove(oldest);
            }
        }
    }

    private static string? ImageExtension(string url)
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
