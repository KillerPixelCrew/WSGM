using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>The theme store could not answer, with a message for the page.</summary>
/// <param name="message">What went wrong.</param>
public sealed class ThemeStoreException(string message) : Exception(message);

/// <summary>What the store lists about a theme.</summary>
/// <param name="Id">The store id.</param>
/// <param name="Name">The theme's name, which is its identity once installed.</param>
/// <param name="DisplayName">What the store shows.</param>
/// <param name="Version">The version text.</param>
/// <param name="Target">The part of Steam the theme restyles.</param>
/// <param name="Targets">Every target the store tags it with.</param>
/// <param name="ManifestVersion">The manifest version it needs.</param>
/// <param name="SpecifiedAuthor">The author as the theme names them.</param>
/// <param name="AuthorName">The uploading account's name.</param>
/// <param name="ImageIds">The screenshots, as blob ids.</param>
/// <param name="DownloadId">The package, as a blob id, or null when the store lists none.</param>
/// <param name="DownloadCount">How often it was downloaded.</param>
/// <param name="StarCount">How often it was starred.</param>
/// <param name="Updated">When it was last updated, or null.</param>
public sealed record ThemeStoreSummary(
    string Id,
    string Name,
    string DisplayName,
    string Version,
    string Target,
    IReadOnlyList<string> Targets,
    int ManifestVersion,
    string SpecifiedAuthor,
    string AuthorName,
    IReadOnlyList<string> ImageIds,
    string? DownloadId,
    int DownloadCount,
    int StarCount,
    DateTimeOffset? Updated);

/// <summary>A theme the store lists as another's dependency.</summary>
/// <param name="Id">The store id.</param>
/// <param name="Name">The theme's name.</param>
/// <param name="DisplayName">What the store shows.</param>
/// <param name="Version">The version text.</param>
public sealed record ThemeStoreDependency(string Id, string Name, string DisplayName, string Version);

/// <summary>Everything the store says about one theme.</summary>
/// <param name="Summary">The listing.</param>
/// <param name="Description">The author's description, or empty.</param>
/// <param name="Dependencies">The themes it needs.</param>
/// <param name="Source">Where its source lives, or null.</param>
public sealed record ThemeStoreDetails(
    ThemeStoreSummary Summary,
    string Description,
    IReadOnlyList<ThemeStoreDependency> Dependencies,
    string? Source);

/// <summary>One page of the store, as asked for.</summary>
/// <param name="Page">The page, from one.</param>
/// <param name="PerPage">How many themes a page holds.</param>
/// <param name="Filter">The target to show, or <c>All</c>.</param>
/// <param name="Order">The order the store lists in, one of its own names.</param>
/// <param name="Search">The search text, or empty.</param>
public sealed record ThemeStoreQuery(int Page, int PerPage, string Filter, string Order, string Search)
{
    /// <summary>The filter that shows every target.</summary>
    public const string AllFilter = "All";

    /// <summary>The order CSS Loader opens on.</summary>
    public const string DefaultOrder = "Last Updated";

    /// <summary>The first page of everything, newest first.</summary>
    public static ThemeStoreQuery Default { get; } = new(1, 50, AllFilter, DefaultOrder, string.Empty);
}

/// <summary>One page of listings and how many there are in all.</summary>
/// <param name="Total">How many themes match, across every page.</param>
/// <param name="Items">This page's listings.</param>
public sealed record ThemeStorePage(int Total, IReadOnlyList<ThemeStoreSummary> Items);

/// <summary>The targets the store can filter by, each with its count, and the orders it offers.</summary>
/// <param name="Filters">Each target and how many themes carry it.</param>
/// <param name="Orders">The order names, as the store spells them.</param>
public sealed record ThemeStoreFilters(IReadOnlyDictionary<string, int> Filters, IReadOnlyList<string> Orders);

/// <summary>Read-only client for DeckThemes, the store CSS Loader browses and installs from.</summary>
/// <remarks>
///     <para>
///         The same feed, the same endpoints and the same query CSS Loader sends (<c>api.ts</c>,
///         <c>bulkThemeUpdateCheck.ts</c> and <c>css_remoteinstall.py</c> in b1bc683): a listing
///         asks <c>/themes</c> with the filter prefix that hides desktop themes and profiles unless
///         they are asked for, the targets come from <c>/themes/filters?type=CSS</c>, one theme from
///         <c>/themes/{id}</c>, updates from <c>/themes/ids?ids=a.b.c</c>, and every image and package
///         is a blob under <c>/blobs/{id}</c>. The class translations are <c>/stable.json</c> and
///         <c>/beta.json</c>.
///     </para>
///     <para>
///         Every answer is parsed with <see cref="JsonDocument" /> and bounded; the shapes are a
///         third party's. Nothing here signs in: starring and submissions need a DeckThemes account
///         key and are not offered.
///     </para>
/// </remarks>
public sealed class ThemeStoreClient
{
    /// <summary>Where DeckThemes answers.</summary>
    public const string DefaultApiUrl = "https://api.deckthemes.com";

    /// <summary>The largest package or image accepted.</summary>
    public const int MaximumBlobBytes = 64 * 1024 * 1024;

    private const int MaximumJsonBytes = 8 * 1024 * 1024;
    private readonly HttpClient _http;

    /// <summary>Creates the client.</summary>
    /// <param name="handler">The HTTP handler, or null for the shared default.</param>
    /// <param name="apiUrl">The store's address, or null for DeckThemes.</param>
    public ThemeStoreClient(HttpMessageHandler? handler = null, string? apiUrl = null)
    {
        ApiUrl = (apiUrl ?? DefaultApiUrl).TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler, false);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.MaxResponseContentBufferSize = MaximumBlobBytes;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>The store's address.</summary>
    public string ApiUrl { get; }

    /// <summary>What WSGM sends as its user agent: its name, and the manifest version it reads.</summary>
    public static string UserAgent { get; } =
        $"WSGM/{typeof(ThemeStoreClient).Assembly.GetName().Version?.ToString(3) ?? "0"} "
        + $"CSSLoader-compatible/{ThemeManifest.SupportedVersion}";

    /// <summary>The address of a blob: a screenshot or a package.</summary>
    /// <param name="blobId">The blob id.</param>
    /// <returns>The address.</returns>
    public string BlobUrl(string blobId)
    {
        return $"{ApiUrl}/blobs/{Uri.EscapeDataString(blobId)}";
    }

    /// <summary>The query string CSS Loader sends for a listing.</summary>
    /// <param name="query">The page asked for.</param>
    /// <returns>The query string, with its leading <c>?</c>.</returns>
    /// <remarks>
    ///     The filter is prefixed the way <c>getThemes</c> prefixes it: Big Picture CSS themes without
    ///     profiles by default, profiles when asked for, and desktop themes only when a desktop target
    ///     is chosen.
    /// </remarks>
    public static string QueryString(ThemeStoreQuery query)
    {
        var all = query.Filter == ThemeStoreQuery.AllFilter || query.Filter.Length == 0;
        var prefix = (query.Filter.Contains("Desktop", StringComparison.Ordinal)
                         ? "-Preset"
                         : query.Filter == "Preset"
                             ? "BPM-CSS"
                             : "BPM-CSS.-Preset")
                     + (all ? string.Empty : ".");
        var filters = prefix + (all ? string.Empty : query.Filter);
        StringBuilder text = new("?");

        void Add(string key, string value)
        {
            if (value.Length == 0)
            {
                return;
            }

            if (text.Length > 1)
            {
                text.Append('&');
            }

            text.Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }

        Add("page", query.Page > 0 ? query.Page.ToString(CultureInfo.InvariantCulture) : string.Empty);
        Add("perPage", query.PerPage > 0 ? query.PerPage.ToString(CultureInfo.InvariantCulture) : string.Empty);
        Add("filters", filters);
        Add("order", query.Order);
        Add("search", query.Search);
        return text.ToString();
    }

    /// <summary>Lists one page of themes.</summary>
    /// <param name="query">The page asked for.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The page.</returns>
    /// <exception cref="ThemeStoreException">The store could not be asked or answered something else.</exception>
    public async Task<ThemePage> QueryAsync(ThemeStoreQuery query, CancellationToken cancellationToken)
    {
        var json = await GetJsonAsync("/themes" + QueryString(query), cancellationToken).ConfigureAwait(false);
        return ParsePage(json);
    }

    /// <summary>Lists Audio Loader sound packs using the same bounded DeckThemes client as CSS themes.</summary>
    /// <param name="page">The one-based page.</param>
    /// <param name="search">Optional search text.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The sound-pack listings.</returns>
    public async Task<ThemePage> QuerySoundsAsync(int page, string search, CancellationToken cancellationToken)
    {
        var json = await GetJsonAsync(
            $"/themes?page={page}&perPage=24&filters=AUDIO.&order=Last%20Updated&search={Uri.EscapeDataString(search)}",
            cancellationToken).ConfigureAwait(false);
        return ParsePage(json);
    }

    /// <summary>The targets and orders the store offers.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The filters.</returns>
    /// <exception cref="ThemeStoreException">The store could not be asked or answered something else.</exception>
    public async Task<ThemeStoreFilters> FiltersAsync(CancellationToken cancellationToken)
    {
        var json = await GetJsonAsync("/themes/filters?type=CSS", cancellationToken).ConfigureAwait(false);
        return ParseFilters(json);
    }

    /// <summary>Everything the store says about one theme.</summary>
    /// <param name="id">The store id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The details.</returns>
    /// <exception cref="ThemeStoreException">The store could not be asked or answered something else.</exception>
    public async Task<ThemeStoreDetails> GetAsync(string id, CancellationToken cancellationToken)
    {
        var json = await GetJsonAsync("/themes/" + Uri.EscapeDataString(id), cancellationToken).ConfigureAwait(false);
        return ParseDetails(json);
    }

    /// <summary>What the store currently lists for some ids, for finding updates.</summary>
    /// <param name="ids">The installed themes' ids.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The listings the store still has; an id the store does not know is absent.</returns>
    /// <exception cref="ThemeStoreException">The store could not be asked or answered something else.</exception>
    public async Task<IReadOnlyList<ThemeStoreSummary>> LookUpAsync(
        IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var json = await GetJsonAsync(
            "/themes/ids?ids=" + Uri.EscapeDataString(string.Join(".", ids)), cancellationToken).ConfigureAwait(false);
        return ParseLookUp(json);
    }

    /// <summary>Reads the answer to a look-up by ids.</summary>
    /// <param name="json">The store's answer.</param>
    /// <returns>The listings in it; anything that is not a theme is skipped.</returns>
    /// <exception cref="ThemeStoreException">The answer was not JSON.</exception>
    public static IReadOnlyList<ThemeStoreSummary> ParseLookUp(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? [.. Objects(document.RootElement).Select(ParseSummary)]
                : [];
        }
        catch (JsonException ex)
        {
            throw new ThemeStoreException($"The theme store's answer could not be read: {ex.Message}");
        }
    }

    /// <summary>Downloads a blob: a package or an image.</summary>
    /// <param name="blobId">The blob id.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="ThemeStoreException">The store could not be asked, refused, or sent more than allowed.</exception>
    public async Task<MemoryStream> DownloadBlobAsync(string blobId, CancellationToken cancellationToken)
    {
        var url = BlobUrl(blobId);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ThemeStoreException($"Got {(int)response.StatusCode} code from '{url}'");
            }

            return await BoundedHttp.ReadAsync(response.Content, MaximumBlobBytes,
                () => new ThemeStoreException("The download is larger than the 64 MB safety limit."),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ThemeStoreException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            throw new ThemeStoreException($"The theme store could not be reached: {ex.Message}");
        }
    }

    /// <summary>The class translations for the client's branch.</summary>
    /// <param name="beta">Whether Steam is on a beta branch.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The document text, never empty.</returns>
    /// <exception cref="ThemeStoreException">The store could not be asked or answered nothing.</exception>
    public async Task<string> TranslationsAsync(bool beta, CancellationToken cancellationToken)
    {
        var text = await GetTextAsync(beta ? "/beta.json" : "/stable.json", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ThemeStoreException("Empty response");
        }

        return text;
    }

    /// <summary>Reads a listing page.</summary>
    /// <param name="json">The store's answer.</param>
    /// <returns>The page.</returns>
    /// <exception cref="ThemeStoreException">The answer was not a page.</exception>
    public static ThemePage ParsePage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ThemeStoreException("The theme store answered something other than a page.");
            }

            var total = JsonRead.Count(root, "total");
            var items = root.TryGetProperty("items", out var itemsProperty) &&
                        itemsProperty.ValueKind == JsonValueKind.Array
                ? Objects(itemsProperty).Select(ParseSummary).ToList()
                : [];
            return new ThemePage(total, items);
        }
        catch (JsonException ex)
        {
            throw new ThemeStoreException($"The theme store's answer could not be read: {ex.Message}");
        }
    }

    /// <summary>Reads the filters answer.</summary>
    /// <param name="json">The store's answer.</param>
    /// <returns>The filters.</returns>
    /// <exception cref="ThemeStoreException">The answer was not the filters.</exception>
    public static ThemeStoreFilters ParseFilters(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Dictionary<string, int> filters = new(StringComparer.Ordinal);
            List<string> orders = [];
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("filters", out var filtersProperty)
                    && filtersProperty.ValueKind == JsonValueKind.Object)
                {
                    foreach (var filter in filtersProperty.EnumerateObject())
                    {
                        filters[filter.Name] = filter.Value.ValueKind == JsonValueKind.Number
                                               && filter.Value.TryGetInt32(out var count)
                            ? count
                            : 0;
                    }
                }

                if (root.TryGetProperty("order", out var orderProperty) &&
                    orderProperty.ValueKind == JsonValueKind.Array)
                {
                    orders.AddRange(orderProperty.EnumerateArray()
                        .Where(order => order.ValueKind == JsonValueKind.String)
                        .Select(order => order.GetString()!));
                }
            }

            return new ThemeStoreFilters(filters, orders);
        }
        catch (JsonException ex)
        {
            throw new ThemeStoreException($"The theme store's answer could not be read: {ex.Message}");
        }
    }

    /// <summary>Reads one theme's details.</summary>
    /// <param name="json">The store's answer.</param>
    /// <returns>The details.</returns>
    /// <exception cref="ThemeStoreException">The answer was not a theme.</exception>
    public static ThemeStoreDetails ParseDetails(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ThemeStoreException("The theme store answered something other than a theme.");
            }

            var summary = ParseSummary(root);
            List<ThemeStoreDependency> dependencies = [];
            if (root.TryGetProperty("dependencies", out var dependenciesProperty)
                && dependenciesProperty.ValueKind == JsonValueKind.Array)
            {
                dependencies.AddRange(dependenciesProperty.EnumerateArray()
                    .Where(dependency => dependency.ValueKind == JsonValueKind.Object)
                    .Select(dependency => new ThemeStoreDependency(
                        Text(dependency, "id"),
                        Text(dependency, "name"),
                        Text(dependency, "displayName", Text(dependency, "name")),
                        Text(dependency, "version"))));
            }

            return new ThemeStoreDetails(
                summary, Text(root, "description"), dependencies, JsonRead.OptionalString(root, "source"));
        }
        catch (JsonException ex)
        {
            throw new ThemeStoreException($"The theme store's answer could not be read: {ex.Message}");
        }
    }

    private static ThemeStoreSummary ParseSummary(JsonElement item)
    {
        List<string> targets = [];
        if (item.TryGetProperty("targets", out var targetsProperty) && targetsProperty.ValueKind == JsonValueKind.Array)
        {
            targets.AddRange(targetsProperty.EnumerateArray()
                .Where(target => target.ValueKind == JsonValueKind.String)
                .Select(target => target.GetString()!));
        }

        List<string> images = [];
        if (item.TryGetProperty("images", out var imagesProperty) && imagesProperty.ValueKind == JsonValueKind.Array)
        {
            images.AddRange(imagesProperty.EnumerateArray()
                .Where(image => image.ValueKind == JsonValueKind.Object)
                .Select(image => Text(image, "id"))
                .Where(id => id.Length > 0));
        }

        string? downloadId = null;
        var downloadCount = 0;
        if (item.TryGetProperty("download", out var download) && download.ValueKind == JsonValueKind.Object)
        {
            downloadId = JsonRead.OptionalString(download, "id");
            downloadCount = JsonRead.Count(download, "downloadCount");
        }

        var authorName = item.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object
            ? Text(author, "username")
            : string.Empty;
        DateTimeOffset? updated = item.TryGetProperty("updated", out var updatedProperty)
                                  && updatedProperty.ValueKind == JsonValueKind.String
                                  && DateTimeOffset.TryParse(
                                      updatedProperty.GetString(), CultureInfo.InvariantCulture,
                                      DateTimeStyles.AssumeUniversal, out var stamp)
            ? stamp
            : null;
        var manifestVersion = JsonRead.Int(item, "manifestVersion", 1);
        var starCount = JsonRead.Count(item, "starCount");
        var name = Text(item, "name");
        return new ThemeStoreSummary(
            Text(item, "id"),
            name,
            Text(item, "displayName", name),
            Text(item, "version"),
            Text(item, "target"),
            targets,
            manifestVersion,
            Text(item, "specifiedAuthor"),
            authorName,
            images,
            downloadId,
            downloadCount,
            starCount,
            updated);
    }

    /// <summary>The objects of an array, skipping whatever else a malformed answer holds.</summary>
    private static IEnumerable<JsonElement> Objects(JsonElement array)
    {
        return array.EnumerateArray().Where(element => element.ValueKind == JsonValueKind.Object);
    }

    private static string Text(JsonElement element, string property, string fallback = "")
    {
        return JsonRead.OptionalString(element, property) ?? fallback;
    }

    private async Task<string> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        var text = await GetTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ThemeStoreException("No json returned!");
        }

        return text;
    }

    private async Task<string> GetTextAsync(string path, CancellationToken cancellationToken)
    {
        var url = ApiUrl + path;
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ThemeStoreException($"Res not OK!, code {(int)response.StatusCode}");
            }

            using var output = await BoundedHttp.ReadAsync(response.Content, MaximumJsonBytes,
                () => new ThemeStoreException("The theme store's answer is larger than expected."),
                cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        catch (ThemeStoreException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            throw new ThemeStoreException($"The theme store could not be reached: {ex.Message}");
        }
    }
}

/// <summary>One page of listings and how many there are in all.</summary>
/// <param name="Total">How many themes match, across every page.</param>
/// <param name="Items">This page's listings.</param>
public sealed record ThemePage(int Total, IReadOnlyList<ThemeStoreSummary> Items);
