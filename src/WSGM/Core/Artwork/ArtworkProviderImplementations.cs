using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>SteamGridDB behind the shared provider contract.</summary>
/// <remarks>
///     A thin adapter rather than a rewrite: <see cref="SteamGridDb" /> keeps every endpoint, header and
///     failure message it already had, and this only re-shapes the results and reports readiness. The
///     existing search and picker behaviour is unchanged, which is what the second provider was
///     required not to disturb.
/// </remarks>
public sealed class SteamGridDbProvider : IArtworkProvider
{
    /// <inheritdoc />
    public string Id => "steamgriddb";

    /// <inheritdoc />
    public string DisplayName => "SteamGridDB";

    /// <inheritdoc />
    public ArtworkProviderStatus GetStatus(ArtworkConfig config)
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
        string term, ArtworkConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var matches = await SteamGridDb.SearchGamesAsync(
            term, SteamGridDb.ResolveKey(config), cancellationToken).ConfigureAwait(false);
        var trimmed = term.Trim();
        return
        [
            .. matches
                .Select(game => new ArtworkGameMatch(
                    Id,
                    game.Id.ToString(CultureInfo.InvariantCulture),
                    game.Name,
                    string.Equals(game.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkCandidate>> GetAssetsForGameAsync(
        ArtworkAsset asset, string gameId, ArtworkConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!int.TryParse(gameId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return [];
        }

        var assets = await SteamGridDb.GetAssetsForGameAsync(
            asset, id, SteamGridDb.ResolveKey(config), cancellationToken).ConfigureAwait(false);
        return Convert(assets);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkCandidate>> GetAssetsForGameAsync(
        ArtworkAsset asset, string gameId, ArtworkConfig config, ArtworkQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!int.TryParse(gameId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return [];
        }

        var assets = await SteamGridDb.GetAssetsForGameAsync(
            asset, id, SteamGridDb.ResolveKey(config), query, cancellationToken).ConfigureAwait(false);
        return Convert(assets);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkCandidate>> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, ArtworkConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var assets = await SteamGridDb.GetAssetsForSteamAppAsync(
            asset, steamAppId, SteamGridDb.ResolveKey(config), cancellationToken).ConfigureAwait(false);
        return Convert(assets);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkCandidate>> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, ArtworkConfig config, ArtworkQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var assets = await SteamGridDb.GetAssetsForSteamAppAsync(
            asset, steamAppId, SteamGridDb.ResolveKey(config), query, cancellationToken).ConfigureAwait(false);
        return Convert(assets);
    }

    private static ArtworkCandidate[] Convert(IReadOnlyList<SgdbAsset> assets)
    {
        return
        [
            .. assets.Select(a => new ArtworkCandidate(
                a.Url,
                a.Thumb,
                a.Width,
                a.Height,
                a.Extension,
                Author: a.Author,
                Style: a.Style,
                Notes: a.Notes,
                Animated: a.Animated,
                Nsfw: a.Nsfw,
                Humor: a.Humor,
                Epilepsy: a.Epilepsy))
        ];
    }
}

/// <summary>
///     Screenscraper.fr behind the shared provider contract.
/// </summary>
/// <remarks>
///     Screenscraper differs from SteamGridDB in the two ways that shaped the abstraction. Its
///     credentials are the application's own — a registered developer pair that ships with the build
///     (see <see cref="ScreenscraperCredentials" />), plus an optional user account whose level decides
///     the quota — where SteamGridDB needs a key the user obtains. And it is organised around emulated
///     systems and ROM names rather than Steam app ids, so it can answer a title search but has nothing
///     to say about a Steam app id.
///     <para>
///         Its media vocabulary is its own and does not line up one-to-one with Steam's artwork slots, so
///         the mapping lives here rather than leaking into the picker. Regional variants are preferred
///         world-first, because a world release is the one most likely to match what the user expects.
///     </para>
///     <para>
///         Rate limiting is explicit in this API: HTTP 429 means the concurrent-thread or per-minute quota
///         is spent, 430 the daily scrape quota, and 431 too many lookups in a day for titles Screenscraper
///         does not hold. All three are reported as provider failures rather than as empty results, so one
///         provider running out cannot read as the game having no art.
///     </para>
///     <para>
///         431 is the one to watch. Screenscraper is a ROM database being asked about a Steam library, so a
///         miss is the ordinary outcome rather than the exceptional one, and the misses count. All three are
///         counted against the account an <c>ssid</c> names, or against the requesting IP when there is
///         none, never against the shipped developer pair — so a user can only ever spend their own
///         allowance, and each quota message names the free personal account that raises it.
///     </para>
/// </remarks>
public sealed class ScreenscraperProvider : IArtworkProvider
{
    private const string ApiBase = "https://api.screenscraper.fr/api2";

    private const int MaxJsonResponseBytes = 4 * 1024 * 1024;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = MaxJsonResponseBytes
    };

    /// <summary>How Screenscraper's media types map onto Steam's artwork slots.</summary>
    /// <remarks>
    ///     In preference order per slot. Screenscraper has no icon media, so that slot falls back to the
    ///     2D box, which is the only square-ish art it reliably has.
    /// </remarks>
    private static readonly Dictionary<ArtworkAsset, string[]> MediaTypes = new()
    {
        [ArtworkAsset.Grid] = ["box-2D", "box-3D", "flyer"],
        [ArtworkAsset.Hero] = ["fanart", "ss", "sstitle"],
        [ArtworkAsset.Logo] = ["wheel", "wheel-hd", "screenmarquee"],
        [ArtworkAsset.Wide] = ["screenmarquee", "marquee", "fanart"],
        [ArtworkAsset.Icon] = ["box-2D", "wheel"]
    };

    /// <summary>Region preference: a world release first, then the common regional ones.</summary>
    private static readonly string[] RegionPreference = ["wor", "us", "eu", "jp", "ss"];

    /// <inheritdoc />
    public string Id => "screenscraper";

    /// <inheritdoc />
    public string DisplayName => "Screenscraper.fr";

    /// <inheritdoc />
    /// <remarks>
    ///     Credentials are never missing here, unlike SteamGridDB: WSGM ships a developer pair and the
    ///     user's own only replaces it. The switch in Settings is the whole of the readiness question.
    /// </remarks>
    public ArtworkProviderStatus GetStatus(ArtworkConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.ScreenscraperEnabled
            ? ArtworkProviderStatus.Ready
            : new ArtworkProviderStatus(ArtworkProviderReadiness.Disabled, "Turned off in Settings.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkGameMatch>> SearchGamesAsync(
        string term, ArtworkConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var trimmed = term.Trim();
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

            var gameId = id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? ""
                : id.ToString();
            if (gameId.Length == 0)
            {
                continue;
            }

            var name = ReadName(game);
            matches.Add(new ArtworkGameMatch(
                Id, gameId, name, string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase)));
        }

        return matches;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkCandidate>> GetAssetsForGameAsync(
        ArtworkAsset asset, string gameId, ArtworkConfig config, CancellationToken cancellationToken)
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

        var wanted = MediaTypes.TryGetValue(asset, out var types) ? types : MediaTypes[ArtworkAsset.Grid];
        var candidates = new List<(int TypeRank, int RegionRank, ArtworkCandidate Candidate)>();
        foreach (var media in medias.EnumerateArray())
        {
            if (media.ValueKind != JsonValueKind.Object
                || !media.TryGetProperty("type", out var typeElement)
                || !media.TryGetProperty("url", out var urlElement))
            {
                continue;
            }

            var type = typeElement.GetString() ?? "";
            var typeRank = Array.IndexOf(wanted, type);
            if (typeRank < 0)
            {
                continue;
            }

            var url = urlElement.GetString() ?? "";
            var extension = ExtensionOf(media, url);
            if (extension is null)
            {
                continue;
            }

            var region = media.TryGetProperty("region", out var regionElement)
                ? regionElement.GetString() ?? ""
                : "";
            var regionRank = Array.IndexOf(RegionPreference, region);
            candidates.Add((
                typeRank,
                regionRank < 0 ? RegionPreference.Length : regionRank,
                new ArtworkCandidate(url, url, 0, 0, extension)));
        }

        return
        [
            .. candidates
                .OrderBy(entry => entry.TypeRank)
                .ThenBy(entry => entry.RegionRank)
                .Select(entry => entry.Candidate)
        ];
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Screenscraper indexes emulated systems by ROM, so a Steam app id means nothing to it. Saying
    ///     so by returning nothing is correct; the user reaches it through a title search instead.
    /// </remarks>
    public Task<IReadOnlyList<ArtworkCandidate>> GetAssetsForSteamAppAsync(
        ArtworkAsset asset, long steamAppId, ArtworkConfig config, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<ArtworkCandidate>>([]);
    }

    private static string Credentials(ArtworkConfig config)
    {
        var parts = new List<string>
        {
            "output=json",
            $"softname={Uri.EscapeDataString(ScreenscraperCredentials.SoftName)}",
            $"devid={Uri.EscapeDataString(ScreenscraperCredentials.DevId)}",
            $"devpassword={Uri.EscapeDataString(ScreenscraperCredentials.DevPassword)}"
        };

        // The user account is optional and only raises the quota, so its absence is not a refusal.
        var user = config.ScreenscraperUser.Trim();
        var password = config.ScreenscraperUserPassword.Trim();
        if (user.Length > 0 && password.Length > 0)
        {
            parts.Add($"ssid={Uri.EscapeDataString(user)}");
            parts.Add($"sspassword={Uri.EscapeDataString(password)}");
        }

        // Present only in a local build that was given one. It costs a slot in a daily allowance of
        // 100 against the developer account, so it is never sent on a user's behalf.
        if (ScreenscraperCredentials.DebugPassword is { } debug)
        {
            parts.Add($"devdebugpassword={Uri.EscapeDataString(debug)}");
        }

        return string.Join('&', parts);
    }

    private static string ReadName(JsonElement game)
    {
        // Screenscraper returns names as a region-tagged list, so the same preference applies.
        if (!game.TryGetProperty("noms", out var names) || names.ValueKind != JsonValueKind.Array)
        {
            return ReadSingleName(game);
        }

        foreach (var region in RegionPreference)
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

        return ReadSingleName(game);
    }

    private static string ReadSingleName(JsonElement game)
    {
        return game.TryGetProperty("nom", out var single) ? single.GetString() ?? "" : "";
    }

    /// <summary>The image format, from the media's own field or the URL, and only if static.</summary>
    private static string? ExtensionOf(JsonElement media, string url)
    {
        var declared = media.TryGetProperty("format", out var format)
            ? (format.GetString() ?? "").ToLowerInvariant()
            : "";
        var candidate = declared switch
        {
            "png" => "png",
            "jpg" or "jpeg" => "jpg",
            _ => ""
        };
        if (candidate.Length > 0)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var checkedUri)
                   && checkedUri.Scheme == Uri.UriSchemeHttps
                ? candidate
                : null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "jpg",
            ".png" => "png",
            _ => null
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
                    430 => "Screenscraper daily scrape quota is used up. "
                           + "A free Screenscraper account, set in Settings, raises it.",
                    431 => "Screenscraper stopped answering for today after too many titles it "
                           + "does not have. A free Screenscraper account, set in Settings, raises it.",
                    _ => $"Screenscraper returned HTTP {(int)response.StatusCode}."
                });
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
