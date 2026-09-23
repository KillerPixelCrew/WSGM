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

/// <summary>One artwork image the Store offers for a title.</summary>
/// <param name="Purpose">The Store's own name for what the image is.</param>
/// <param name="Url">Where to fetch it.</param>
/// <param name="Width">Its width in pixels, or zero when the Store did not say.</param>
/// <param name="Height">Its height in pixels, or zero when the Store did not say.</param>
public sealed record StoreCatalogImage(string Purpose, string Url, int Width, int Height);

/// <summary>What the Store says about one title.</summary>
/// <param name="ProductId">The Store product id.</param>
/// <param name="Name">The title as the Store names it.</param>
/// <param name="IsGame">Whether the Store classifies it as a game.</param>
/// <param name="Multiplayer">Whether it reports a multiplayer capability.</param>
/// <param name="MultiplayerEvidence">Which capability decided that, in one sentence.</param>
/// <param name="Images">The artwork the Store offers.</param>
public sealed record StoreCatalogEntry(
    string ProductId,
    string Name,
    bool IsGame,
    MultiplayerVerdict Multiplayer,
    string MultiplayerEvidence,
    IReadOnlyList<StoreCatalogImage> Images);

/// <summary>Asks Microsoft's public Store catalog about an installed package.</summary>
/// <remarks>
///     <para>
///         One unauthenticated lookup per title, by package family name, serving two purposes at
///         once: whether the title has multiplayer, and the official artwork. Playnite's Xbox
///         library reaches the same facts through an authenticated Xbox Live sign-in; this does not,
///         because a library importer should not require an account to name a game.
///     </para>
///     <para>
///         The reference implementation paces itself at one request per 500 ms and this does the
///         same. A bulk import is the first thing in WSGM that queries a third party without
///         somebody watching each result, and the budget being spent belongs to the user.
///     </para>
///     <para>
///         Fetching is injected. The response shape is a third party's and varies, so the parser is
///         pure and tested against captured fixtures rather than against a live endpoint.
///     </para>
/// </remarks>
public sealed class StoreCatalogClient
{
    /// <summary>The public display catalog, queried by an alternate id.</summary>
    private const string Endpoint =
        "https://displaycatalog.mp.microsoft.com/v7.0/products/lookup"
        + "?market={0}&languages={1}&alternateId=PackageFamilyName&value={2}&fieldsTemplate=Details";

    /// <summary>One request per this interval, as the reference implementation paces itself.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(500);

    private readonly Func<string, CancellationToken, Task<string?>> _fetch;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>Creates the client over an injected fetch.</summary>
    /// <param name="fetch">Fetches a URL's body, or returns null when it could not be fetched.</param>
    public StoreCatalogClient(Func<string, CancellationToken, Task<string?>>? fetch = null)
    {
        _fetch = fetch ?? DefaultFetchAsync;
    }

    /// <summary>The market and language to ask in, from this machine's own settings.</summary>
    /// <remarks>Falls back to the Store's most complete market rather than failing the lookup.</remarks>
    public static (string Market, string Language) Locale
    {
        get
        {
            try
            {
                var culture = CultureInfo.CurrentUICulture;
                var region = culture.Name.Split('-').LastOrDefault();
                return region is { Length: 2 }
                    ? (region.ToUpperInvariant(), culture.Name)
                    : ("US", "en-US");
            }
            catch (CultureNotFoundException)
            {
                return ("US", "en-US");
            }
        }
    }

    /// <summary>Looks one installed package up.</summary>
    /// <param name="familyName">Its package family name.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>What the Store says, or null when it said nothing usable.</returns>
    public async Task<StoreCatalogEntry?> LookUpAsync(
        string familyName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(familyName))
        {
            return null;
        }

        var (market, language) = Locale;
        var url = string.Format(
            CultureInfo.InvariantCulture,
            Endpoint,
            Uri.EscapeDataString(market),
            Uri.EscapeDataString(language),
            Uri.EscapeDataString(familyName));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var since = DateTimeOffset.UtcNow - _last;
            if (since < MinimumInterval)
            {
                await Task.Delay(MinimumInterval - since, cancellationToken).ConfigureAwait(false);
            }

            _last = DateTimeOffset.UtcNow;
            var body = await _fetch(url, cancellationToken).ConfigureAwait(false);
            return Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            Log.Warn($"Store lookup failed for {familyName}: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads one catalog response. Pure, for tests.</summary>
    /// <param name="json">The response body.</param>
    /// <returns>What it says, or null when it says nothing usable.</returns>
    public static StoreCatalogEntry? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Products", out var products)
                || products.ValueKind != JsonValueKind.Array
                || products.GetArrayLength() == 0)
            {
                return null;
            }

            var product = products[0];
            var properties = product.TryGetProperty("Properties", out var found) ? found : default;
            var localized = Localized(product);

            var attributes = Attributes(properties);
            var multiplayer = MultiplayerFrom(attributes, out var evidence);

            return new StoreCatalogEntry(
                Text(product, "ProductId"),
                Text(localized, "ProductTitle"),
                IsGame(product, properties),
                multiplayer,
                evidence,
                Images(localized));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement Localized(JsonElement product)
    {
        return product.TryGetProperty("LocalizedProperties", out var localized)
               && localized.ValueKind == JsonValueKind.Array
               && localized.GetArrayLength() > 0
            ? localized[0]
            : default;
    }

    private static IReadOnlyList<string> Attributes(JsonElement properties)
    {
        if (properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty("Attributes", out var attributes)
            || attributes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. attributes.EnumerateArray()
                .Select(attribute => attribute.ValueKind == JsonValueKind.Object
                    ? Text(attribute, "Name")
                    : attribute.ValueKind == JsonValueKind.String
                        ? attribute.GetString() ?? string.Empty
                        : string.Empty)
                .Where(name => name.Length > 0)
        ];
    }

    /// <summary>
    ///     Whether any reported capability describes playing with other people.
    /// </summary>
    /// <remarks>
    ///     Only capabilities that actually mean multiplayer. Network capabilities deliberately do
    ///     not count: a single-player game with telemetry or an updater declares those, and treating
    ///     them as multiplayer would mark almost everything and make the distinction useless.
    /// </remarks>
    private static MultiplayerVerdict MultiplayerFrom(
        IReadOnlyList<string> attributes, out string evidence)
    {
        string[] multiplayer =
        [
            "XboxLiveMultiplayer", "CrossPlatformMultiplayer", "OnlineMultiplayer",
            "LocalMultiplayer", "SharedSplitScreen", "OnlineCoOp", "LocalCoOp",
            "CrossPlatformCoOp", "XboxLiveCoOp"
        ];

        var matched = attributes
            .Where(name => multiplayer.Any(capability => name.Contains(capability, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matched.Count > 0)
        {
            evidence = $"The Store reports {string.Join(", ", matched)}.";
            return MultiplayerVerdict.Multiplayer;
        }

        if (attributes.Count == 0)
        {
            evidence = "The Store listed no capabilities for this title.";
            return MultiplayerVerdict.Unknown;
        }

        evidence = "The Store reports no multiplayer capability for this title.";
        return MultiplayerVerdict.SinglePlayer;
    }

    private static bool IsGame(JsonElement product, JsonElement properties)
    {
        if (Text(product, "ProductType").Equals("Game", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return properties.ValueKind == JsonValueKind.Object
               && Text(properties, "ProductGroupName").Contains("Game", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<StoreCatalogImage> Images(JsonElement localized)
    {
        if (localized.ValueKind != JsonValueKind.Object
            || !localized.TryGetProperty("Images", out var images)
            || images.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<StoreCatalogImage> found = [];
        foreach (var image in images.EnumerateArray())
        {
            var uri = Text(image, "Uri");
            if (uri.Length == 0)
            {
                continue;
            }

            // The catalog returns protocol-relative URLs. Https only: an artwork download must not
            // be downgraded to plaintext.
            if (uri.StartsWith("//", StringComparison.Ordinal))
            {
                uri = "https:" + uri;
            }

            if (!uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The apply path names the file by its suffix. An image with none it recognises would be
            // handed to Steam as whatever the guess was, so it is not offered at all.
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                || Path.GetExtension(parsed.AbsolutePath).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg"
                    or ".webp"))
            {
                continue;
            }

            found.Add(new StoreCatalogImage(
                Text(image, "ImagePurpose"),
                uri,
                Number(image, "Width"),
                Number(image, "Height")));
        }

        return found;
    }

    private static string Text(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int Number(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var number)
            ? number
            : 0;
    }

    private static async Task<string?> DefaultFetchAsync(string url, CancellationToken cancellationToken)
    {
        using HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            : null;
    }
}
