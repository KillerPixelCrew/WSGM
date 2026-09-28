using System.Net;
using WSGM.Core;

namespace WSGM.Tests.Core.Themes;

/// <summary>The DeckThemes client asks what CSS Loader asks and reads what the store answers.</summary>
public sealed class ThemeStoreClientTests
{
    private const string Listing = """
                                   { "id": "abc", "name": "Dark Deck", "displayName": "Dark Deck", "version": "v2.1", "target": "Steam Deck",
                                     "targets": ["Steam Deck", "Library"], "manifestVersion": 9, "specifiedAuthor": "Squishy", "type": "Css",
                                     "images": [{ "id": "img1", "blobType": "Jpg" }, { "id": "img2", "blobType": "Jpg" }],
                                     "download": { "id": "zip1", "blobType": "Zip", "downloadCount": 12480 },
                                     "author": { "id": "u1", "username": "squishy", "avatar": "", "premiumTier": "None" },
                                     "submitted": "2026-01-01T00:00:00Z", "updated": "2026-09-12T10:00:00Z", "starCount": 318 }
                                   """;

    [Theory]
    [InlineData("All", "Last Updated", "", "?page=1&perPage=50&filters=BPM-CSS.-Preset&order=Last%20Updated")]
    [InlineData("Library", "Most Downloaded", "dark",
        "?page=1&perPage=50&filters=BPM-CSS.-Preset.Library&order=Most%20Downloaded&search=dark")]
    [InlineData("Preset", "Last Updated", "", "?page=1&perPage=50&filters=BPM-CSS.Preset&order=Last%20Updated")]
    [InlineData("Desktop", "Last Updated", "", "?page=1&perPage=50&filters=-Preset.Desktop&order=Last%20Updated")]
    public void TheQueryCarriesCssLoadersFilterPrefix(string filter, string order, string search, string expected)
    {
        Assert.Equal(expected, ThemeStoreClient.QueryString(new ThemeStoreQuery(1, 50, filter, order, search)));
    }

    [Fact]
    public void ReadsAListingPageWithItsImagesPackageAndCounts()
    {
        var page = ThemeStoreClient.ParsePage($$"""{ "total": 312, "items": [{{Listing}}] }""");

        Assert.Equal(312, page.Total);
        var item = Assert.Single(page.Items);
        Assert.Equal("abc", item.Id);
        Assert.Equal("Dark Deck", item.Name);
        Assert.Equal("v2.1", item.Version);
        Assert.Equal(["Steam Deck", "Library"], item.Targets);
        Assert.Equal(["img1", "img2"], item.ImageIds);
        Assert.Equal("zip1", item.DownloadId);
        Assert.Equal(12480, item.DownloadCount);
        Assert.Equal(318, item.StarCount);
        Assert.Equal("Squishy", item.SpecifiedAuthor);
        Assert.Equal("squishy", item.AuthorName);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero), item.Updated);
    }

    [Fact]
    public void ReadsDetailsWithDependenciesAndTheFiltersWithTheirCounts()
    {
        var details = ThemeStoreClient.ParseDetails(Listing.TrimEnd().TrimEnd('}')
                                                    + """, "description": "Near black.", "source": "https://x", "dependencies": [{ "id": "dep", "name": "Rounded", "displayName": "Rounded Corners", "version": "v1.4" }] }""");
        Assert.Equal("Near black.", details.Description);
        Assert.Equal("https://x", details.Source);
        var dependency = Assert.Single(details.Dependencies);
        Assert.Equal(("dep", "Rounded", "Rounded Corners", "v1.4"),
            (dependency.Id, dependency.Name, dependency.DisplayName, dependency.Version));

        var filters = ThemeStoreClient.ParseFilters(
            """{ "filters": { "Steam Deck": 200, "Library": 12 }, "order": ["Last Updated", "Most Downloaded"] }""");
        Assert.Equal(200, filters.Filters["Steam Deck"]);
        Assert.Equal(["Last Updated", "Most Downloaded"], filters.Orders);
    }

    [Fact]
    public void AnAnswerThatIsNotJsonIsARefusalWithAReason()
    {
        var refused = Assert.Throws<ThemeStoreException>(() => ThemeStoreClient.ParsePage("<html>"));

        Assert.Contains("could not be read", refused.Message);
    }

    [Fact]
    public async Task TheClientAsksTheStoresEndpointsAndBoundsWhatItReads()
    {
        List<string> asked = [];
        var handler = new StubHandler(request =>
        {
            asked.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath switch
            {
                "/themes/filters" => Text("""{ "filters": { "Steam Deck": 1 }, "order": ["Last Updated"] }"""),
                "/themes/abc" => Text(Listing),
                "/themes/ids" => Text($"[{Listing}]"),
                "/blobs/zip1" => Bytes([1, 2, 3]),
                "/stable.json" => Text("{}"),
                "/beta.json" => Text(" "),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var client = new ThemeStoreClient(handler, "https://store.example/");

        Assert.Equal("https://store.example/blobs/img%201", client.BlobUrl("img 1"));
        Assert.Equal(1, (await client.FiltersAsync(CancellationToken.None)).Filters["Steam Deck"]);
        Assert.Equal("abc", (await client.GetAsync("abc", CancellationToken.None)).Summary.Id);
        Assert.Single(await client.LookUpAsync(["abc", "def"], CancellationToken.None));
        Assert.Empty(await client.LookUpAsync([], CancellationToken.None));
        Assert.Equal([1, 2, 3], (await client.DownloadBlobAsync("zip1", CancellationToken.None)).ToArray());
        Assert.Equal("{}", await client.TranslationsAsync(false, CancellationToken.None));
        var empty = await Assert.ThrowsAsync<ThemeStoreException>(() =>
            client.TranslationsAsync(true, CancellationToken.None));
        Assert.Equal("Empty response", empty.Message);
        var missing =
            await Assert.ThrowsAsync<ThemeStoreException>(() => client.GetAsync("nope", CancellationToken.None));
        Assert.Contains("404", missing.Message);

        Assert.Equal(
            [
                "/themes/filters?type=CSS", "/themes/abc", "/themes/ids?ids=abc.def", "/blobs/zip1", "/stable.json",
                "/beta.json", "/themes/nope"
            ],
            asked);
        Assert.StartsWith("WSGM/", handler.LastUserAgent);
    }

    private static HttpResponseMessage Text(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private static HttpResponseMessage Bytes(byte[] body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    }

    [Fact]
    public void NumbersOfTheWrongKindAndItemsThatAreNotThemesReadAsDefaults()
    {
        var page = ThemeStoreClient.ParsePage(
            """{ "total": "many", "items": [1, "x", { "id": "t", "name": "T", "manifestVersion": null, "starCount": "5", "download": { "downloadCount": true } }] }""");

        Assert.Equal(0, page.Total);
        var item = Assert.Single(page.Items);
        Assert.Equal((1, 0, 0), (item.ManifestVersion, item.StarCount, item.DownloadCount));
        var filters = ThemeStoreClient.ParseFilters("""{ "filters": { "Deck": "lots" } }""");
        Assert.Equal(0, filters.Filters["Deck"]);
        Assert.Throws<ThemeStoreException>(() => ThemeStoreClient.ParseLookUp("<html>"));
        Assert.Empty(ThemeStoreClient.ParseLookUp("""[2, null]"""));
    }

    /// <summary>A handler that answers from a function, recording the last user agent sent.</summary>
    internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        internal string? LastUserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(answer(request));
        }
    }
}
