using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     Discovery's use of the one catalog response. The runtime and multiplayer rules are covered by
///     the classifier and catalog parser; what is left here is what discovery itself decides.
/// </summary>
public sealed class XboxLibrarySourceTests
{
    private static readonly InstalledPackage Package =
        new("Publisher.Game_abc!App", "Publisher.Game_abc", "Moonlit", @"C:\WindowsApps\Game", "App");

    private static XboxLibrarySource Source(StoreCatalogEntry? catalog)
    {
        return new XboxLibrarySource(
            _ => [Package], _ => null, (_, _) => Task.FromResult(catalog));
    }

    private static StoreCatalogEntry Catalog(params StoreCatalogImage[] images)
    {
        return new StoreCatalogEntry(
            "9NBLGGH4R0R7", "Moonlit", true, MultiplayerVerdict.SinglePlayer, "Evidence.", images);
    }

    [Fact]
    public async Task EachStoreImagePurposeFillsTheCapsuleWhoseShapeItActuallyHas()
    {
        var source = Source(Catalog(
            new StoreCatalogImage("Poster", "https://store/poster.png", 720, 1080),
            new StoreCatalogImage("SuperHeroArt", "https://store/hero.jpg", 1920, 1080),
            new StoreCatalogImage("Logo", "https://store/logo.png", 300, 300),
            new StoreCatalogImage("TitledHeroArt", "https://store/wide.png", 1280, 720),
            new StoreCatalogImage("Tile", "https://store/tile.png", 150, 150)));

        var game = Assert.Single(await source.DiscoverAsync(CancellationToken.None));

        Assert.Equal(
            [ArtworkAsset.Grid, ArtworkAsset.Hero, ArtworkAsset.Logo, ArtworkAsset.Wide, ArtworkAsset.Icon],
            game.Artwork.Select(image => image.Asset));
        Assert.Equal("https://store/poster.png", game.Artwork[0].Url);
    }

    [Fact]
    public async Task AnUnmappedPurposeIsDroppedRatherThanGuessedAt()
    {
        // A wrongly shaped capsule is worse than none: Steam shows the stretched result instead of
        // falling back to its own.
        var source = Source(Catalog(
            new StoreCatalogImage("ScreenshotWide", "https://store/shot.png", 1920, 1080)));

        Assert.Empty(Assert.Single(await source.DiscoverAsync(CancellationToken.None)).Artwork);
    }

    [Fact]
    public async Task ThePickedImageIsTheLargestTheStoreOffersForThatCapsule()
    {
        var source = Source(Catalog(
            new StoreCatalogImage("Poster", "https://store/small.png", 300, 450),
            new StoreCatalogImage("BoxArt", "https://store/large.png", 720, 1080)));

        var image = Assert.Single(Assert.Single(
            await source.DiscoverAsync(CancellationToken.None)).Artwork);

        Assert.Equal("https://store/large.png", image.Url);
        Assert.Equal(ArtworkAsset.Grid, image.Asset);
    }

    [Fact]
    public async Task ATitleTheStoreHasNothingForCarriesNoArtwork()
    {
        Assert.Empty(Assert.Single(
            await Source(null).DiscoverAsync(CancellationToken.None)).Artwork);
    }

    [Fact]
    public async Task TheGamingPlumbingWindowsInstallsIsNeverOfferedAsAGame()
    {
        // These sit beside every Xbox title and would otherwise be listed as games themselves,
        // because they carry the same GDK evidence the titles do.
        XboxLibrarySource source = new(
            _ =>
            [
                Package with
                {
                    FamilyName = "Microsoft.GamingServices_8wek", Aumid = "Microsoft.GamingServices_8wek!App"
                },
                Package with
                {
                    FamilyName = "Microsoft.XboxGamingOverlay_8wek",
                    Aumid = "Microsoft.XboxGamingOverlay_8wek!App"
                }
            ],
            _ => null);

        Assert.Empty(await source.DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task APackageWithNoAumidIsSkippedRatherThanListedUnlaunchable()
    {
        XboxLibrarySource source = new(_ => [Package with { Aumid = string.Empty }], _ => null);

        Assert.Empty(await source.DiscoverAsync(CancellationToken.None));
    }
}
