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

        var game = Assert.Single(await source.DiscoverAsync([], CancellationToken.None));

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

        Assert.Empty(Assert.Single(await source.DiscoverAsync([], CancellationToken.None)).Artwork);
    }

    [Fact]
    public async Task ThePickedImageIsTheLargestTheStoreOffersForThatCapsuleAndTheRestAreAlternatives()
    {
        var source = Source(Catalog(
            new StoreCatalogImage("Poster", "https://store/small.png", 300, 450),
            new StoreCatalogImage("BoxArt", "https://store/large.png", 720, 1080)));

        var artwork = Assert.Single(await source.DiscoverAsync([], CancellationToken.None)).Artwork;

        Assert.Equal(["https://store/large.png", "https://store/small.png"], artwork.Select(image => image.Url));
        Assert.All(artwork, image => Assert.Equal(ArtworkAsset.Grid, image.Asset));
    }

    [Fact]
    public async Task APackageNeitherGdkEvidenceNorTheStoreCallsAGameIsReturnedAsNotAGame()
    {
        // Every installed Store application carries the same package identity a game does. With no
        // manifest to read and nothing from the Store, this one is an application until proven
        // otherwise. It is still returned, so an imported title whose lookup failed offline is not
        // taken for uninstalled; the plan decides what a non-game shows.
        var game = Assert.Single(await Source(null).DiscoverAsync([], CancellationToken.None));

        Assert.False(game.IsGame);
        Assert.Equal(Package.Aumid, game.Key);
    }

    [Fact]
    public async Task AnApplicationTheStoreKnowsIsNotAGameIsReturnedAsNotAGame()
    {
        var source = Source(new StoreCatalogEntry(
            "9WZDNCRFHVQM", "Paint", false, MultiplayerVerdict.Unknown, "Evidence.", []));

        Assert.False(Assert.Single(await source.DiscoverAsync([], CancellationToken.None)).IsGame);
    }

    [Fact]
    public async Task TheGamingPlumbingWindowsInstallsIsNeverOffered()
    {
        // These sit beside every Xbox title and would otherwise be listed as games themselves,
        // because they carry the same GDK evidence the titles do. The Store is made to call them games
        // here, so only the package filter can keep them out.
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
            _ => null,
            (_, _) => Task.FromResult<StoreCatalogEntry?>(Catalog()));

        Assert.Empty(await source.DiscoverAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task APackageWithNoAumidIsSkippedRatherThanListedUnlaunchable()
    {
        // The Store calls it a game, so only the missing AUMID keeps it out.
        XboxLibrarySource source = new(
            _ => [Package with { Aumid = string.Empty }],
            _ => null,
            (_, _) => Task.FromResult<StoreCatalogEntry?>(Catalog()));

        Assert.Empty(await source.DiscoverAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task APackageWithSeveralApplicationsIsReadAndLookedUpOnce()
    {
        var reads = 0;
        var lookUps = 0;
        XboxLibrarySource source = new(
            _ =>
            [
                Package,
                Package with { Aumid = "Publisher.Game_abc!Launcher", ApplicationId = "Launcher" }
            ],
            _ =>
            {
                reads++;
                return null;
            },
            (_, _) =>
            {
                lookUps++;
                return Task.FromResult<StoreCatalogEntry?>(Catalog());
            });

        var games = await source.DiscoverAsync([], CancellationToken.None);

        Assert.Equal(["Publisher.Game_abc!App", "Publisher.Game_abc!Launcher"], games.Select(game => game.Key));
        Assert.Equal(1, lookUps);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void TheStoreNamesTheXboxSourcesImages()
    {
        ILibrarySource source = Source(null);

        Assert.Equal("Microsoft Store", source.CatalogName);
    }

    [Theory]
    [InlineData(XboxRuntime.PackagedWin32Gdk, true)]
    [InlineData(XboxRuntime.NativeUwp, true)]
    [InlineData(XboxRuntime.Unknown, false)]
    public void OnlyTheTwoRuntimesTheLauncherHasARouteForAreValidated(XboxRuntime runtime, bool validated)
    {
        // Everything after discovery reads this, not the Xbox runtime, so it has to carry both the
        // verdict and the reason the review shows.
        var launch = XboxLibrarySource.Launch(new XboxRuntimeClassification(runtime, "Because."));

        Assert.Equal(validated, launch.Validated);
        Assert.Equal("Because.", launch.Evidence);
        Assert.False(string.IsNullOrWhiteSpace(launch.Label));
    }

    [Theory]
    [InlineData("x86")]
    [InlineData("arm64")]
    public void APackageBuiltForAnotherArchitectureIsNotOfferedTheOverlayRoute(string architecture)
    {
        var launch =
            XboxLibrarySource.Launch(new XboxRuntimeClassification(XboxRuntime.NativeUwp, "UWP."), architecture);

        Assert.False(launch.Validated);
        Assert.Contains(architecture, launch.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("neutral")]
    [InlineData("")]
    public void AnX64OrNeutralPackageKeepsItsRoute(string architecture)
    {
        Assert.True(XboxLibrarySource.Launch(
            new XboxRuntimeClassification(XboxRuntime.NativeUwp, "UWP."), architecture).Validated);
    }
}
