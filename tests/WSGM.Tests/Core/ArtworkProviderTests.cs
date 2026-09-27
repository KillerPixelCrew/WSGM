using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     The multi-provider artwork contract: a provider that cannot be searched is distinguishable from
///     one that searched and found nothing, one source's failure does not remove the other's results,
///     and the same asset arriving twice is shown once.
/// </summary>
public sealed class ArtworkProviderTests
{
    [Fact]
    public void SteamGridDbIsNotReadyWithoutItsKeyAndSaysWhy()
    {
        SteamGridDbProvider provider = new();

        var missing = provider.GetStatus(new ArtworkConfig());
        var present = provider.GetStatus(new ArtworkConfig { SteamGridDbApiKey = "abc" });

        Assert.Equal(ArtworkProviderReadiness.MissingCredentials, missing.Readiness);
        Assert.Contains(SteamGridDb.KeyPageUrl, missing.Detail, StringComparison.Ordinal);
        Assert.True(present.IsReady);
    }

    [Fact]
    public void ScreenscraperIsReadyOutOfTheBoxAndOnlyTheSwitchTurnsItOff()
    {
        // WSGM ships the developer credentials Screenscraper issues per application, so unlike
        // SteamGridDB there is no credential state to report: a default configuration is ready, and
        // the only way to not search it is to have said so.
        ScreenscraperProvider provider = new();

        Assert.True(provider.GetStatus(new ArtworkConfig()).IsReady);
        Assert.Equal(
            ArtworkProviderReadiness.Disabled,
            provider.GetStatus(new ArtworkConfig { ScreenscraperEnabled = false }).Readiness);
    }

    [Fact]
    public void ScreenscraperUnfoldsItsShippedCredentialsIntactly()
    {
        // The pair is stored XOR-folded so it does not sit in the binary as plain text. That is a
        // reversible transform, and a wrong key or a truncated array would fail as an authentication
        // rejection at runtime rather than as anything visible at build time.
        Assert.NotEqual("", ScreenscraperCredentials.DevId);
        Assert.NotEqual("", ScreenscraperCredentials.DevPassword);
        Assert.All(
            ScreenscraperCredentials.DevId + ScreenscraperCredentials.DevPassword,
            c => Assert.InRange(c, '!', '~'));
    }

    [Fact]
    public void ScreenscraperSoftNameCarriesNoSpaceForTheUrlsItComesBackIn()
    {
        // Screenscraper echoes softname into the media URLs it returns, so a space in it arrives
        // inside URLs WSGM would then have to repair.
        Assert.StartsWith("WSGM", ScreenscraperCredentials.SoftName, StringComparison.Ordinal);
        Assert.DoesNotContain(' ', ScreenscraperCredentials.SoftName);
    }

    [Fact]
    public async Task ScreenscraperReportsNothingForASteamAppIdRatherThanGuessing()
    {
        // It indexes emulated systems by ROM. A Steam app id means nothing to it, and answering
        // with another game's art because the number matched would be worse than answering nothing.
        ScreenscraperProvider provider = new();

        Assert.Empty(await provider.GetAssetsForSteamAppAsync(
            ArtworkAsset.Grid, 440, new ArtworkConfig(), CancellationToken.None));
    }

    [Fact]
    public void BothProvidersAreDeclaredAndFindableByTheirOwnIds()
    {
        Assert.Equal(2, ArtworkSearch.Providers.Count);
        Assert.Equal("steamgriddb", ArtworkSearch.Providers[0].Id);
        Assert.Equal("screenscraper", ArtworkSearch.Providers[1].Id);
        Assert.Same(ArtworkSearch.Providers[0], ArtworkSearch.Find("steamgriddb"));
        Assert.Null(ArtworkSearch.Find("nope"));
    }

    [Fact]
    public async Task NoConfiguredProviderIsReportedAsSuchRatherThanAsAnEmptyResult()
    {
        // The distinction this whole layer exists for: nothing was asked, so "this game has no
        // artwork" would be a lie. Screenscraper is ready by default, so it has to be switched off.
        var result = await ArtworkSearch.GetAssetsForSteamAppAsync(
            ArtworkAsset.Grid,
            440,
            new ArtworkConfig { ScreenscraperEnabled = false },
            CancellationToken.None);

        Assert.Empty(result.Candidates);
        Assert.True(result.NoProviderAnswered);
        Assert.Equal(2, result.Outcomes.Count);
        Assert.All(result.Outcomes, outcome => Assert.False(outcome.Status.IsReady));
        Assert.Equal(2, result.Skipped.Count);
    }

    [Fact]
    public async Task AMatchIsOnlyEverAskedOfTheProviderThatIssuedIt()
    {
        // A game id belongs to the source that issued it. Asking the other provider would either
        // return nothing or, worse, return a different game that happened to share the number.
        ArtworkGameMatch match = new("nonexistent-provider", "1", "Something", true);

        var result = await ArtworkSearch.GetAssetsForMatchAsync(
            ArtworkAsset.Grid, match, new ArtworkConfig(), CancellationToken.None);

        Assert.Empty(result.Candidates);
        Assert.Empty(result.Outcomes);
    }

    [Fact]
    public void TheResultSeparatesAFailureFromASkip()
    {
        ArtworkSearchResult result = new(
            [],
            [
                new ArtworkProviderOutcome(
                    "SteamGridDB", ArtworkProviderStatus.Ready,
                    "SteamGridDB rate limit reached. Try again later."),
                new ArtworkProviderOutcome(
                    "Screenscraper.fr",
                    new ArtworkProviderStatus(ArtworkProviderReadiness.Disabled, "Turned off in Settings."),
                    null)
            ]);

        Assert.Equal(["SteamGridDB: SteamGridDB rate limit reached. Try again later."], result.Failures);
        Assert.Equal(["Screenscraper.fr: Turned off in Settings."], result.Skipped);
        Assert.True(result.NoProviderAnswered);
    }

    [Fact]
    public void OneProviderAnsweringMeansTheGridIsHonestlyEmptyRatherThanUnanswered()
    {
        ArtworkSearchResult result = new(
            [],
            [
                new ArtworkProviderOutcome("SteamGridDB", ArtworkProviderStatus.Ready, null),
                new ArtworkProviderOutcome(
                    "Screenscraper.fr",
                    new ArtworkProviderStatus(ArtworkProviderReadiness.Disabled, "Turned off in Settings."),
                    null)
            ]);

        Assert.False(result.NoProviderAnswered);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void EveryArtworkSlotHasAScreenscraperMediaMapping()
    {
        // Screenscraper's vocabulary is its own and does not line up with Steam's slots, so the
        // mapping has to be total: an unmapped slot would silently return nothing.
        ScreenscraperProvider provider = new();

        Assert.All(Enum.GetValues<ArtworkAsset>(), asset =>
            Assert.NotEmpty(ScreenscraperProvider.MediaTypes[asset]));
        Assert.Equal("screenscraper", provider.Id);
        Assert.Equal("Screenscraper.fr", provider.DisplayName);
    }

    [Theory]
    [InlineData("https://neoclone.screenscraper.fr/api2/mediaJeu.php?media=wheel", true)]
    [InlineData("https://screenscraper.fr/image.php", true)]
    [InlineData("https://cdn2.steamgriddb.com/grid/a.png", false)]
    [InlineData("https://notscreenscraper.fr/a.png", false)]
    public void ScreenscraperDownloadsOnlyItsOwnMedia(string url, bool served)
    {
        // Its media endpoint counts against the account's one thread, so those downloads wait with its
        // searches; every other address is fetched directly.
        Assert.Equal(served, new ScreenscraperProvider().Serves(new Uri(url)));
        Assert.False(((IArtworkProvider)new SteamGridDbProvider()).Serves(new Uri(url)));
    }

    [Fact]
    public void AUrlLosesTheUsersAccountButKeepsEverythingElse()
    {
        var stripped = ArtworkUrls.WithoutAccount(
            "https://neoclone.screenscraper.fr/api2/mediaJeu.php?devid=d&ssid=me&sspassword=secret&media=wheel#top");

        Assert.Equal("https://neoclone.screenscraper.fr/api2/mediaJeu.php?devid=d&media=wheel#top", stripped);
        Assert.Equal("https://a/b.png", ArtworkUrls.WithoutAccount("https://a/b.png?ssid=me&sspassword=secret"));
    }

    [Fact]
    public void ALoggedUrlShowsNoCredential()
    {
        var logged = ArtworkUrls.Redact("https://a/b?devid=d&devpassword=p&ssid=me&SSPASSWORD=s&media=wheel");

        Assert.Equal("https://a/b?devid=***&devpassword=***&ssid=***&SSPASSWORD=***&media=wheel", logged);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpg")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, "webp")]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x00, 1 }, "ico")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38 }, null)]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x41, 0x56, 0x45 }, null)]
    public void AnImagesFormatComesFromItsOwnBytes(byte[] bytes, string? format)
    {
        // A provider's media endpoint answers every format under one address, so the URL cannot say.
        Assert.Equal(format, SteamArtwork.ImageFormat(bytes));
    }
}
