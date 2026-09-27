using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>
///     The Game Library's artwork stage: the automatic match asks the providers in preference order,
///     a provider that could not be asked is a failure rather than "no artwork", and a title's
///     candidates arrive once, merged with its source's own images.
/// </summary>
public sealed class GameLibraryArtworkTests
{
    private static readonly ArtworkGameMatch SgdbGame = new("steamgriddb", "1", "Moonlit", true);
    private static readonly ArtworkGameMatch RomGame = new("screenscraper", "9", "Moonlit", true);

    private static GameLibraryArtworkRequest Request(
        string id = "t1", ArtworkGameMatch? match = null, IReadOnlyList<DiscoveredArtwork>? catalog = null)
    {
        return new GameLibraryArtworkRequest(id, "Moonlit", catalog ?? [], "Microsoft Store", match);
    }

    private static async Task<GameLibraryArtworkProgress> SettledAsync(GameLibraryArtwork stage, string id = "t1")
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var progress = stage.StatusOf(id);
            if (progress.Status is not (GameLibraryArtworkStatus.Pending or GameLibraryArtworkStatus.Loading))
            {
                return progress;
            }

            await Task.Delay(10);
        }

        return stage.StatusOf(id);
    }

    [Fact]
    public async Task TheFirstProviderThatKnowsTheTitleDecidesTheMatch()
    {
        FakeProviders providers = new() { Matches = { ["steamgriddb"] = SgdbGame, ["screenscraper"] = RomGame } };
        providers.Images[SgdbGame] = ["https://sgdb/a.png"];
        using GameLibraryArtwork stage = new(providers);

        stage.Reset([Request()]);
        var progress = await SettledAsync(stage);

        Assert.Equal((GameLibraryArtworkStatus.Ready, "Moonlit"), (progress.Status, progress.MatchName));
        Assert.Equal(["steamgriddb"], providers.Asked);
        Assert.Equal("https://sgdb/a.png", Assert.Single(stage.Options("t1", ArtworkAsset.Grid)).Url);
    }

    [Fact]
    public async Task AGameWithNoImagesGivesWayToTheNextProvider()
    {
        FakeProviders providers = new() { Matches = { ["steamgriddb"] = SgdbGame, ["screenscraper"] = RomGame } };
        providers.Images[RomGame] = ["https://screenscraper.fr/media/a.png"];
        using GameLibraryArtwork stage = new(providers);

        stage.Reset([Request()]);
        var progress = await SettledAsync(stage);

        Assert.Equal(GameLibraryArtworkStatus.Ready, progress.Status);
        Assert.Equal(["steamgriddb", "screenscraper"], providers.Asked);
    }

    [Fact]
    public async Task AProviderThatCouldNotBeAskedIsAFailureNotAFallThrough()
    {
        // A rejected key read as "no results" sent the title to Screenscraper, which pinned a ROM
        // database's guess as its match.
        FakeProviders providers = new() { Failure = "SteamGridDB rejected the API key." };
        using GameLibraryArtwork stage = new(providers);

        stage.Reset([Request()]);
        var progress = await SettledAsync(stage);

        Assert.Equal((GameLibraryArtworkStatus.Failed, "SteamGridDB rejected the API key."),
            (progress.Status, progress.Detail));
    }

    [Fact]
    public async Task NoProviderSetUpSaysSoRatherThanNotFound()
    {
        FakeProviders providers = new() { UnavailableReason = "No API key." };
        using GameLibraryArtwork stage = new(providers);

        stage.Reset([Request()]);

        Assert.Equal((GameLibraryArtworkStatus.Unavailable, "No API key."),
            ((await SettledAsync(stage)).Status, stage.StatusOf("t1").Detail));
    }

    [Fact]
    public async Task AMatchTheUserFixedIsKeptEvenWithoutImages()
    {
        FakeProviders providers = new();
        using GameLibraryArtwork stage = new(providers);

        stage.Reset([Request(match: RomGame)]);
        var progress = await SettledAsync(stage);

        Assert.Equal(GameLibraryArtworkStatus.NotFound, progress.Status);
        Assert.Empty(providers.Asked);
        Assert.Equal([RomGame], providers.Fetched.Distinct());
    }

    [Fact]
    public async Task ATitlesImagesArriveInOneChangeNotOnePerType()
    {
        FakeProviders providers = new() { Matches = { ["steamgriddb"] = SgdbGame } };
        providers.Images[SgdbGame] = ["https://sgdb/a.png"];
        using GameLibraryArtwork stage = new(providers);
        var changes = 0;
        stage.Changed += () => Interlocked.Increment(ref changes);

        stage.Reset([Request()]);
        await SettledAsync(stage);
        await Task.Delay(50);

        // One for the reset, one for the title.
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task TheSourcesOwnImagesComeFirstAndEachImageOnce()
    {
        FakeProviders providers = new() { Matches = { ["steamgriddb"] = SgdbGame } };
        providers.Images[SgdbGame] = ["https://store/a.png", "https://sgdb/b.png"];
        using GameLibraryArtwork stage = new(providers);

        stage.Reset([Request(catalog: [new DiscoveredArtwork(ArtworkAsset.Grid, "https://store/a.png")])]);
        await SettledAsync(stage);

        Assert.Equal([("https://store/a.png", true), ("https://sgdb/b.png", false)],
            stage.Options("t1", ArtworkAsset.Grid).Select(option => (option.Url, option.Catalog)));
    }

    [Fact]
    public async Task ASubscriberThatThrowsDoesNotStopTheWorkers()
    {
        FakeProviders providers = new() { Matches = { ["steamgriddb"] = SgdbGame } };
        providers.Images[SgdbGame] = ["https://sgdb/a.png"];
        using GameLibraryArtwork stage = new(providers);
        stage.Changed += () => throw new InvalidOperationException("subscriber defect");

        stage.Reset([Request(), Request("t2")]);

        Assert.Equal(GameLibraryArtworkStatus.Ready, (await SettledAsync(stage, "t2")).Status);
    }

    [Fact]
    public async Task ChangedCredentialsRetryWhatFailedButNothingElse()
    {
        // A miss costs Screenscraper's daily allowance, so a save that changed nothing asks nothing.
        FakeProviders providers = new() { Failure = "SteamGridDB rejected the API key." };
        using GameLibraryArtwork stage = new(providers);
        stage.Reset([Request()]);
        await SettledAsync(stage);
        var asked = providers.Searches;

        stage.ConfigurationChanged();
        await Task.Delay(50);
        Assert.Equal(asked, providers.Searches);

        providers.Failure = null;
        providers.Matches["steamgriddb"] = SgdbGame;
        providers.Images[SgdbGame] = ["https://sgdb/a.png"];
        providers.Key = "new key";
        stage.ConfigurationChanged();

        Assert.Equal(GameLibraryArtworkStatus.Ready, (await SettledAsync(stage)).Status);
    }

    [Fact]
    public async Task FixingAMatchSearchesEveryProviderAtOnce()
    {
        FakeProviders providers = new() { Merged = [SgdbGame, RomGame] };
        using GameLibraryArtwork stage = new(providers);

        var matches = await stage.SearchAsync("Moonlit", CancellationToken.None);

        Assert.Equal([SgdbGame, RomGame], matches);
    }

    private sealed class FakeProviders : IGameLibraryArtworkProviders
    {
        private static readonly string[] Order = ["steamgriddb", "screenscraper"];
        private int _searches;

        internal Dictionary<string, ArtworkGameMatch> Matches { get; } = [];
        internal Dictionary<ArtworkGameMatch, string[]> Images { get; } = [];
        internal List<string> Asked { get; } = [];
        internal List<ArtworkGameMatch> Fetched { get; } = [];
        internal IReadOnlyList<ArtworkGameMatch> Merged { get; init; } = [];
        internal string? Failure { get; set; }
        internal string? UnavailableReason { get; init; }
        internal string Key { get; set; } = "key";
        internal int Searches => Volatile.Read(ref _searches);

        public string? Unavailable()
        {
            return UnavailableReason;
        }

        public string Signature()
        {
            return Key;
        }

        public Task<ArtworkGameMatch?> FindMatchAsync(
            string name, IReadOnlyCollection<string> skip, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _searches);
            if (UnavailableReason is not null)
            {
                return Task.FromResult<ArtworkGameMatch?>(null);
            }

            if (Failure is { } failure)
            {
                throw new ArtworkProviderException(failure);
            }

            foreach (var provider in Order.Where(provider => !skip.Contains(provider)))
            {
                lock (Asked)
                {
                    Asked.Add(provider);
                }

                if (Matches.TryGetValue(provider, out var match))
                {
                    return Task.FromResult<ArtworkGameMatch?>(match);
                }
            }

            return Task.FromResult<ArtworkGameMatch?>(null);
        }

        public Task<IReadOnlyList<ArtworkGameMatch>> SearchAsync(string term, CancellationToken cancellationToken)
        {
            return Task.FromResult(Merged);
        }

        public Task<ArtworkSearchResult> FetchAsync(
            ArtworkAsset asset, ArtworkGameMatch match, CancellationToken cancellationToken)
        {
            lock (Fetched)
            {
                Fetched.Add(match);
            }

            IReadOnlyList<ArtworkCandidate> candidates =
                asset is ArtworkAsset.Grid && Images.TryGetValue(match, out var urls)
                    ?
                    [
                        .. urls.Select(url =>
                            new ArtworkCandidate(url, url, 600, 900, "png", match.ProviderId, match.ProviderId))
                    ]
                    : [];
            return Task.FromResult(new ArtworkSearchResult(candidates, []));
        }
    }
}
