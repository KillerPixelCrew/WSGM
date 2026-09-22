using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     Reading Microsoft's Store catalog. One lookup answers both questions the importer has: is
///     this a game, and does it have multiplayer. The response is a third party's shape, so the
///     parser is pure and tested against captured fixtures.
/// </summary>
public sealed class StoreCatalogClientTests
{
    private static string Response(string attributes, string productType = "Game", string images = "")
    {
        return $$"""
            {
              "Products": [
                {
                  "ProductId": "9NBLGGH1234",
                  "ProductType": "{{productType}}",
                  "LocalizedProperties": [
                    { "ProductTitle": "Washer", "Images": [ {{images}} ] }
                  ],
                  "Properties": { "Attributes": [ {{attributes}} ] }
                }
              ]
            }
            """;
    }

    [Fact]
    public void AMultiplayerCapabilityIsReportedWithTheCapabilityThatSaidSo()
    {
        var entry = StoreCatalogClient.Parse(Response("""{ "Name": "XboxLiveMultiplayer" }"""));

        Assert.NotNull(entry);
        Assert.Equal(MultiplayerVerdict.Multiplayer, entry.Multiplayer);
        Assert.Contains("XboxLiveMultiplayer", entry.MultiplayerEvidence, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalCoOpCountsAsMultiplayer()
    {
        var entry = StoreCatalogClient.Parse(Response("""{ "Name": "SharedSplitScreen" }"""));

        Assert.Equal(MultiplayerVerdict.Multiplayer, entry?.Multiplayer);
    }

    [Fact]
    public void ANetworkCapabilityIsNotMultiplayer()
    {
        // A single-player game with telemetry or an updater declares these. Treating them as
        // multiplayer would mark almost everything and make the distinction useless.
        var entry = StoreCatalogClient.Parse(Response(
            """{ "Name": "internetClient" }, { "Name": "internetClientServer" }"""));

        Assert.Equal(MultiplayerVerdict.SinglePlayer, entry?.Multiplayer);
    }

    [Fact]
    public void ATitleWithNoListedCapabilitiesIsUnknownRatherThanSinglePlayer()
    {
        // Nothing was said, which is not the same as "no multiplayer".
        var entry = StoreCatalogClient.Parse(Response(""));

        Assert.Equal(MultiplayerVerdict.Unknown, entry?.Multiplayer);
    }

    [Fact]
    public void AGameIsRecognisedByItsProductType()
    {
        Assert.True(StoreCatalogClient.Parse(Response(""))?.IsGame);
        Assert.False(StoreCatalogClient.Parse(Response("", "Application"))?.IsGame);
    }

    [Fact]
    public void ImagesAreReadWithTheirPurposeAndSize()
    {
        var entry = StoreCatalogClient.Parse(Response("",
            images: """{ "ImagePurpose": "Poster", "Uri": "https://cdn/poster.png", "Width": 720, "Height": 1080 }"""));

        var image = Assert.Single(entry!.Images);
        Assert.Equal("Poster", image.Purpose);
        Assert.Equal("https://cdn/poster.png", image.Url);
        Assert.Equal(720, image.Width);
        Assert.Equal(1080, image.Height);
    }

    [Fact]
    public void AProtocolRelativeImageBecomesHttps()
    {
        var entry = StoreCatalogClient.Parse(Response("",
            images: """{ "ImagePurpose": "Logo", "Uri": "//cdn/logo.png" }"""));

        Assert.Equal("https://cdn/logo.png", Assert.Single(entry!.Images).Url);
    }

    [Fact]
    public void APlaintextImageIsRefusedRatherThanDownloaded()
    {
        // An artwork download must not be downgraded to plaintext.
        var entry = StoreCatalogClient.Parse(Response("",
            images: """{ "ImagePurpose": "Logo", "Uri": "http://cdn/logo.png" }"""));

        Assert.Empty(entry!.Images);
    }

    [Theory]
    [InlineData("""{ "Products": [] }""")]
    [InlineData("""{ "Products": null }""")]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData(null)]
    public void AResponseWithNothingUsableReadsAsNothing(string? json)
    {
        Assert.Null(StoreCatalogClient.Parse(json));
    }

    [Fact]
    public void TheLocaleAlwaysNamesAMarketAndLanguage()
    {
        var (market, language) = StoreCatalogClient.Locale;

        Assert.False(string.IsNullOrWhiteSpace(market));
        Assert.False(string.IsNullOrWhiteSpace(language));
    }
}
