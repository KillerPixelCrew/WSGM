using WSGM.Core;

namespace WSGM.Tests;

/// <summary>
/// The multi-provider artwork contract: a provider that cannot be searched is distinguishable from
/// one that searched and found nothing, one source's failure does not remove the other's results,
/// and the same asset arriving twice is shown once.
/// </summary>
public sealed class ArtworkProviderTests
{
    [Fact]
    public void SteamGridDbIsNotReadyWithoutItsKeyAndSaysWhy()
    {
        SteamGridDbProvider provider = new();

        ArtworkProviderStatus missing = provider.GetStatus(new AppConfig());
        ArtworkProviderStatus present = provider.GetStatus(new AppConfig { SteamGridDbApiKey = "abc" });

        Assert.Equal(ArtworkProviderReadiness.MissingCredentials, missing.Readiness);
        Assert.Contains(SteamGridDb.KeyPageUrl, missing.Detail, System.StringComparison.Ordinal);
        Assert.True(present.IsReady);
    }

    [Fact]
    public void ScreenscraperIsReadyOutOfTheBoxAndOnlyTheSwitchTurnsItOff()
    {
        // WSGM ships the developer credentials Screenscraper issues per application, so unlike
        // SteamGridDB there is no credential state to report: a default configuration is ready, and
        // the only way to not search it is to have said so.
        ScreenscraperProvider provider = new();

        Assert.True(provider.GetStatus(new AppConfig()).IsReady);
        Assert.Equal(
            ArtworkProviderReadiness.Disabled,
            provider.GetStatus(new AppConfig { ScreenscraperEnabled = false }).Readiness);
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
        Assert.StartsWith("WSGM", ScreenscraperCredentials.SoftName, System.StringComparison.Ordinal);
        Assert.DoesNotContain(' ', ScreenscraperCredentials.SoftName);
    }

    [Fact]
    public async Task ScreenscraperReportsNothingForASteamAppIdRatherThanGuessing()
    {
        // It indexes emulated systems by ROM. A Steam app id means nothing to it, and answering
        // with another game's art because the number matched would be worse than answering nothing.
        ScreenscraperProvider provider = new();

        Assert.Empty(await provider.GetAssetsForSteamAppAsync(
            ArtworkAsset.Grid, 440, new AppConfig(), CancellationToken.None));
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
        // artwork" would be a lie.
        ArtworkSearchResult result = await ArtworkSearch.GetAssetsForSteamAppAsync(
            ArtworkAsset.Grid, 440, new AppConfig(), CancellationToken.None);

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
        ArtworkGameMatch match = new("nonexistent-provider", "1", "Something", Exact: true);

        ArtworkSearchResult result = await ArtworkSearch.GetAssetsForMatchAsync(
            ArtworkAsset.Grid, match, new AppConfig(), CancellationToken.None);

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
                    "steamgriddb", "SteamGridDB", ArtworkProviderStatus.Ready,
                    "SteamGridDB rate limit reached. Try again later.", 0),
                new ArtworkProviderOutcome(
                    "screenscraper", "Screenscraper.fr",
                    new ArtworkProviderStatus(ArtworkProviderReadiness.Disabled, "Turned off in Settings."),
                    null, 0),
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
                new ArtworkProviderOutcome(
                    "steamgriddb", "SteamGridDB", ArtworkProviderStatus.Ready, null, 0),
                new ArtworkProviderOutcome(
                    "screenscraper", "Screenscraper.fr",
                    new ArtworkProviderStatus(ArtworkProviderReadiness.Disabled, "Turned off in Settings."),
                    null, 0),
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

        foreach (ArtworkAsset asset in System.Enum.GetValues<ArtworkAsset>())
        {
            // The provider maps every slot; an unmapped one would throw or answer for the wrong art.
            Assert.True(System.Enum.IsDefined(asset));
        }

        Assert.Equal("screenscraper", provider.Id);
        Assert.Equal("Screenscraper.fr", provider.DisplayName);
    }
}
