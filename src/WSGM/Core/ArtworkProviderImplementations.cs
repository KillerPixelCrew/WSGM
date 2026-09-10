using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>SteamGridDB behind the shared provider contract.</summary>
/// <remarks>
/// A thin adapter rather than a rewrite: <see cref="SteamGridDb"/> keeps every endpoint, header and
/// failure message it already had, and this only re-shapes the results and reports readiness. The
/// existing search and picker behaviour is unchanged, which is what the second provider was
/// required not to disturb.
/// </remarks>
public sealed class SteamGridDbProvider : IArtworkProvider
{
    /// <inheritdoc />
    public string Id => "steamgriddb";

    /// <inheritdoc />
    public string DisplayName => "SteamGridDB";

    /// <inheritdoc />
    public ArtworkProviderStatus GetStatus(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SteamGridDb.ResolveKey(config).Length > 0
            ? ArtworkProviderStatus.Ready
            : new ArtworkProviderStatus(
                ArtworkProviderReadiness.MissingCredentials,
                $"No API key. Get a free one at {SteamGridDb.KeyPageUrl} and set it in Settings.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkGameMatch>> SearchGamesAsync(
        string term, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var matches = await SteamGridDb.SearchGamesAsync(
            term, SteamGridDb.ResolveKey(config), cancellationToken).ConfigureAwait(false);
        string trimmed = (term ?? "").Trim();
        return matches
            .Select(game => new ArtworkGameMatch(
                Id,
                game.Id.ToString(CultureInfo.InvariantCulture),
                game.Name,
                string.Equals(game.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkCandidate>> GetAssetsForGameAsync(
        ArtworkAsset asset, string gameId, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!int.TryParse(gameId, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
        {
            return [];
        }
        var assets = await SteamGridDb.GetAssetsForGameAsync(
            asset, id, SteamGridDb.ResolveKey(config), cancellationToken).ConfigureAwait(false);
        return Convert(assets);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkCandidate>> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var assets = await SteamGridDb.GetAssetsForSteamAppAsync(
            asset, steamAppId, SteamGridDb.ResolveKey(config), cancellationToken).ConfigureAwait(false);
        return Convert(assets);
    }

    private IReadOnlyList<ArtworkCandidate> Convert(IReadOnlyList<SgdbAsset> assets) => assets
        .Select(a => new ArtworkCandidate(Id, DisplayName, a.Id, a.Url, a.Thumb, a.Width, a.Height, a.Extension))
        .ToArray();
}

/// <summary>
/// Screenscraper.fr behind the shared provider contract.
/// </summary>
/// <remarks>
/// Screenscraper differs from SteamGridDB in the two ways that shaped the abstraction. It needs
/// credentials — a registered developer id and password, and optionally a user account whose level
/// decides the quota — where SteamGridDB needs only a key. And it is organised around emulated
/// systems and ROM names rather than Steam app ids, so it can answer a title search but has nothing
/// to say about a Steam app id.
/// <para>
/// Its media vocabulary is its own and does not line up one-to-one with Steam's artwork slots, so
/// the mapping lives here rather than leaking into the picker. Regional variants are preferred
/// world-first, because a world release is the one most likely to match what the user expects.
/// </para>
/// <para>
/// Rate limiting is explicit in this API: HTTP 429 means the concurrent-thread or per-minute quota
/// is spent and 430 means the daily scrape quota is gone. Both are reported as provider failures
/// rather than as empty results, so one provider running out cannot read as the game having no art.
/// </para>
/// </remarks>
public sealed class ScreenscraperProvider : IArtworkProvider
{
    private const string ApiBase = "https://api.screenscraper.fr/api2";

    /// <summary>Where a user registers for the developer credentials this provider needs.</summary>
    public const string AccountPageUrl = "https://www.screenscraper.fr/";

    private const int MaxJsonResponseBytes = 4 * 1024 * 1024;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = MaxJsonResponseBytes,
    };

    /// <summary>How Screenscraper's media types map onto Steam's artwork slots.</summary>
    /// <remarks>
    /// In preference order per slot. Screenscraper has no icon media, so that slot falls back to the
    /// 2D box, which is the only square-ish art it reliably has.
    /// </remarks>
    private static readonly Dictionary<ArtworkAsset, string[]> MediaTypes = new()
    {
        [ArtworkAsset.Grid] = ["box-2D", "box-3D", "flyer"],
        [ArtworkAsset.Hero] = ["fanart", "ss", "sstitle"],
        [ArtworkAsset.Logo] = ["wheel", "wheel-hd", "screenmarquee"],
        [ArtworkAsset.Wide] = ["screenmarquee", "marquee", "fanart"],
        [ArtworkAsset.Icon] = ["box-2D", "wheel"],
    };

    /// <summary>Region preference: a world release first, then the common regional ones.</summary>
    private static readonly string[] RegionPreference = ["wor", "us", "eu", "jp", "ss"];

    /// <inheritdoc />
    public string Id => "screenscraper";

    /// <inheritdoc />
    public string DisplayName => "Screenscraper.fr";

    /// <inheritdoc />
    public ArtworkProviderStatus GetStatus(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.ScreenscraperEnabled)
        {
            return new ArtworkProviderStatus(ArtworkProviderReadiness.Disabled, "Turned off in Settings.");
        }

        return (config.ScreenscraperDevId ?? "").Trim().Length > 0
            && (config.ScreenscraperDevPassword ?? "").Trim().Length > 0
            ? ArtworkProviderStatus.Ready
            : new ArtworkProviderStatus(
                ArtworkProviderReadiness.MissingCredentials,
                $"No developer credentials. Register at {AccountPageUrl} and set them in Settings.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkGameMatch>> SearchGamesAsync(
        string term, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        string trimmed = (term ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        var root = await GetAsync(
            $"jeuRecherche.php?{Credentials(config)}&recherche={Uri.EscapeDataString(trimmed)}",
            cancellationToken).ConfigureAwait(false);
        if (root is null || !root.Value.TryGetProperty("response", out var response)
            || !response.TryGetProperty("jeux", out var games) || games.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var matches = new List<ArtworkGameMatch>();
        foreach (var game in games.EnumerateArray())
        {
            if (!game.TryGetProperty("id", out var id))
            {
                continue;
            }
            string gameId = id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? ""
                : id.ToString();
            if (gameId.Length == 0)
            {
                continue;
            }

            string name = ReadName(game);
            matches.Add(new ArtworkGameMatch(
                Id, gameId, name, string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase)));
        }
        return matches;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkCandidate>> GetAssetsForGameAsync(
        ArtworkAsset asset, string gameId, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var root = await GetAsync(
            $"jeuInfos.php?{Credentials(config)}&gameid={Uri.EscapeDataString(gameId)}",
            cancellationToken).ConfigureAwait(false);
        if (root is null || !root.Value.TryGetProperty("response", out var response)
            || !response.TryGetProperty("jeu", out var game)
            || !game.TryGetProperty("medias", out var medias) || medias.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        string[] wanted = MediaTypes.TryGetValue(asset, out string[]? types) ? types : MediaTypes[ArtworkAsset.Grid];
        var candidates = new List<(int TypeRank, int RegionRank, ArtworkCandidate Candidate)>();
        foreach (var media in medias.EnumerateArray())
        {
            if (media.ValueKind != JsonValueKind.Object
                || !media.TryGetProperty("type", out var typeElement)
                || !media.TryGetProperty("url", out var urlElement))
            {
                continue;
            }

            string type = typeElement.GetString() ?? "";
            int typeRank = Array.IndexOf(wanted, type);
            if (typeRank < 0)
            {
                continue;
            }

            string url = urlElement.GetString() ?? "";
            string? extension = ExtensionOf(media, url);
            if (extension is null)
            {
                continue;
            }

            string region = media.TryGetProperty("region", out var regionElement)
                ? regionElement.GetString() ?? "" : "";
            int regionRank = Array.IndexOf(RegionPreference, region);
            candidates.Add((
                typeRank,
                regionRank < 0 ? RegionPreference.Length : regionRank,
                new ArtworkCandidate(Id, DisplayName, 0, url, url, 0, 0, extension)));
        }

        return candidates
            .OrderBy(entry => entry.TypeRank)
            .ThenBy(entry => entry.RegionRank)
            .Select(entry => entry.Candidate)
            .ToArray();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Screenscraper indexes emulated systems by ROM, so a Steam app id means nothing to it. Saying
    /// so by returning nothing is correct; the user reaches it through a title search instead.
    /// </remarks>
    public Task<IReadOnlyList<ArtworkCandidate>> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, AppConfig config, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ArtworkCandidate>>([]);

    private static string Credentials(AppConfig config)
    {
        var parts = new List<string>
        {
            "output=json",
            "softname=WSGM",
            $"devid={Uri.EscapeDataString((config.ScreenscraperDevId ?? "").Trim())}",
            $"devpassword={Uri.EscapeDataString((config.ScreenscraperDevPassword ?? "").Trim())}",
        };

        // The user account is optional and only raises the quota, so its absence is not a refusal.
        string user = (config.ScreenscraperUser ?? "").Trim();
        string password = (config.ScreenscraperUserPassword ?? "").Trim();
        if (user.Length > 0 && password.Length > 0)
        {
            parts.Add($"ssid={Uri.EscapeDataString(user)}");
            parts.Add($"sspassword={Uri.EscapeDataString(password)}");
        }
        return string.Join('&', parts);
    }

    private static string ReadName(JsonElement game)
    {
        // Screenscraper returns names as a region-tagged list, so the same preference applies.
        if (game.TryGetProperty("noms", out var names) && names.ValueKind == JsonValueKind.Array)
        {
            foreach (string region in RegionPreference)
            {
                foreach (var entry in names.EnumerateArray())
                {
                    if (entry.TryGetProperty("region", out var r) && r.GetString() == region
                        && entry.TryGetProperty("text", out var text))
                    {
                        return text.GetString() ?? "";
                    }
                }
            }
            foreach (var entry in names.EnumerateArray())
            {
                if (entry.TryGetProperty("text", out var text))
                {
                    return text.GetString() ?? "";
                }
            }
        }
        return game.TryGetProperty("nom", out var single) ? single.GetString() ?? "" : "";
    }

    /// <summary>The image format, from the media's own field or the URL, and only if static.</summary>
    private static string? ExtensionOf(JsonElement media, string url)
    {
        string declared = media.TryGetProperty("format", out var format)
            ? (format.GetString() ?? "").ToLowerInvariant() : "";
        string candidate = declared switch
        {
            "png" => "png",
            "jpg" or "jpeg" => "jpg",
            _ => "",
        };
        if (candidate.Length > 0)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var checkedUri)
                && checkedUri.Scheme == Uri.UriSchemeHttps ? candidate : null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }
        return System.IO.Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "jpg",
            ".png" => "png",
            _ => null,
        };
    }

    private static async Task<JsonElement?> GetAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(
                $"{ApiBase}/{path}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"Screenscraper {(int)response.StatusCode} for {path.Split('?')[0]}.");
                throw new SteamGridDbException((int)response.StatusCode switch
                {
                    401 or 403 => "Screenscraper rejected the credentials.",
                    429 => "Screenscraper thread or minute quota reached. Try again shortly.",
                    430 => "Screenscraper daily scrape quota is used up.",
                    _ => $"Screenscraper returned HTTP {(int)response.StatusCode}.",
                });
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
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
        catch (JsonException ex)
        {
            // Screenscraper answers with a plain-text error body on some failures, which is not a
            // transport fault and must not read like one.
            Log.Warn($"Screenscraper returned unparseable JSON: {ex.Message}");
            throw new SteamGridDbException("Screenscraper returned a response WSGM could not read.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Screenscraper request failed: {ex.Message}");
            throw new SteamGridDbException("Could not contact Screenscraper.");
        }
    }
}
