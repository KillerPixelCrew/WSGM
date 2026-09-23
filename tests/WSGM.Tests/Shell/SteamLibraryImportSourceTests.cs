using WSGM.Core;
using WSGM.Device.Tests;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>
///     The import page's backend. The acknowledgement and selection rules live here rather than in
///     the page, because a page defect must not be able to put a multiplayer title on the route that
///     injects into it.
/// </summary>
public sealed class SteamLibraryImportSourceTests
{
    private const string Launcher = @"C:\WSGM\WSGM.PackagedLaunch.exe";

    private static DiscoveredGame Game(
        string key = "Publisher.Game_abc!App",
        bool routable = true,
        MultiplayerVerdict multiplayer = MultiplayerVerdict.SinglePlayer,
        string name = "Moonlit",
        bool isGame = true)
    {
        return new DiscoveredGame("xbox", key, name, @"C:\WindowsApps\Game",
            new GameLaunch("UWP", routable, "Evidence."),
            multiplayer, "Evidence.", isGame, [], []);
    }

    private static SteamLibraryImportSource Source(
        TemporaryDirectory temporary,
        params DiscoveredGame[] games)
    {
        return new SteamLibraryImportSource(
            new FakeSource(games),
            new ImportStateStore(temporary.GetPath("import.json")),
            () => null,
            _ => Task.FromResult<IReadOnlyList<ExistingShortcut>>([]),
            () => ImportMode.SteamIntegration,
            () => false);
    }

    private static async Task<SteamLibraryImportState> ScannedAsync(SteamLibraryImportSource source)
    {
        await source.ScanAsync(CancellationToken.None);
        for (var attempt = 0; attempt < 200 && source.ReadState().Phase != "review"; attempt++)
        {
            await Task.Delay(10);
        }

        return source.ReadState();
    }

    [Fact]
    public async Task AScanPublishesAPreviewAndWritesNothing()
    {
        using TemporaryDirectory temporary = new();
        using var source = Source(temporary, Game());

        var state = await ScannedAsync(source);

        Assert.Equal("review", state.Phase);
        Assert.Single(state.Entries);
        Assert.False(File.Exists(temporary.GetPath("import.json")));
    }

    [Fact]
    public async Task AMultiplayerTitleCannotTakeTheOverlayRouteWithoutAnAcknowledgement()
    {
        using TemporaryDirectory temporary = new();
        using var source = Source(temporary, Game(multiplayer: MultiplayerVerdict.Multiplayer));
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        var refused = await source.SetModeAsync(
            entry.Id, nameof(ImportMode.SteamIntegration), false, CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal(nameof(ImportMode.ControllerOnly),
            Assert.Single(source.ReadState().Entries).Mode);
    }

    [Fact]
    public async Task AnAcknowledgedMultiplayerTitleMayTakeTheOverlayRoute()
    {
        using TemporaryDirectory temporary = new();
        using var source = Source(temporary, Game(multiplayer: MultiplayerVerdict.Multiplayer));
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        var accepted = await source.SetModeAsync(
            entry.Id, nameof(ImportMode.SteamIntegration), true, CancellationToken.None);

        Assert.True(accepted.Succeeded);
        var updated = Assert.Single(source.ReadState().Entries);
        Assert.Equal(nameof(ImportMode.SteamIntegration), updated.Mode);
        Assert.True(updated.Acknowledged);
    }

    [Fact]
    public async Task AnUnclassifiedTitleCannotTakeTheOverlayRouteEvenWithAnAcknowledgement()
    {
        // There is no validated route, so there is nothing an acknowledgement could authorise.
        using TemporaryDirectory temporary = new();
        using var source = new SteamLibraryImportSource(
            new FakeSource([Game(routable: false)]),
            new ImportStateStore(temporary.GetPath("import.json")),
            () => null,
            _ => Task.FromResult<IReadOnlyList<ExistingShortcut>>([]),
            () => ImportMode.SteamIntegration,
            () => true);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        var refused = await source.SetModeAsync(
            entry.Id, nameof(ImportMode.SteamIntegration), true, CancellationToken.None);

        Assert.False(refused.Succeeded);
    }

    [Fact]
    public async Task AnUnrecognisedModeIsRefusedRatherThanDefaulted()
    {
        using TemporaryDirectory temporary = new();
        using var source = Source(temporary, Game());
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.False((await source.SetModeAsync(entry.Id, "nonsense", true, CancellationToken.None))
            .Succeeded);
    }

    [Fact]
    public async Task AStaleEntryIdIsRefusedRatherThanIgnored()
    {
        using TemporaryDirectory temporary = new();
        using var source = Source(temporary, Game());
        await ScannedAsync(source);

        Assert.False((await source.ToggleEntryAsync("no-such-entry", CancellationToken.None))
            .Succeeded);
    }

    [Fact]
    public async Task ApplyingWithNothingSelectedIsRefused()
    {
        using TemporaryDirectory temporary = new();
        using var source = Source(temporary, Game());
        await ScannedAsync(source);
        await source.SelectAllAsync(false, CancellationToken.None);

        Assert.False((await source.ApplyAsync(CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task SelectAllNeverSelectsSomethingThatCannotBeActedOn()
    {
        using TemporaryDirectory temporary = new();
        using var source = new SteamLibraryImportSource(
            new FakeSource([Game(routable: false)]),
            new ImportStateStore(temporary.GetPath("import.json")),
            () => null,
            _ => Task.FromResult<IReadOnlyList<ExistingShortcut>>([]),
            () => ImportMode.SteamIntegration,
            () => false);
        await ScannedAsync(source);

        await source.SelectAllAsync(true, CancellationToken.None);

        Assert.Equal(0, source.ReadState().SelectedCount);
    }

    [Fact]
    public async Task EveryPublicationCarriesAHigherRevision()
    {
        // The page redraws from this, so a change that does not move it is a change nobody sees.
        using TemporaryDirectory temporary = new();
        using var source = Source(temporary, Game());
        var before = (await ScannedAsync(source)).Revision;
        var entry = Assert.Single(source.ReadState().Entries);

        await source.ToggleEntryAsync(entry.Id, CancellationToken.None);

        Assert.True(source.ReadState().Revision > before);
    }

    [Fact]
    public async Task RerouteingAnAlreadyImportedTitleMakesItSomethingTheUserCanApply()
    {
        // The plan calls it a Skip, so without this the page shows the new mode, refuses the tick
        // and never rewrites the shortcut.
        using TemporaryDirectory temporary = new();
        var game = Game();
        const string options = "--aumid Publisher.Game_abc!App --mode steam-overlay";
        ImportStateStore store = new(temporary.GetPath("import.json"));
        store.Save(new ImportedEntry
        {
            Source = "xbox", Key = game.Key, Name = game.Name, AppId = 77,
            Target = Launcher, LaunchOptions = options, Mode = nameof(ImportMode.SteamIntegration)
        });

        using SteamLibraryImportSource source = new(
            new FakeSource([game]),
            store,
            () => null,
            _ => Task.FromResult<IReadOnlyList<ExistingShortcut>>(
                [new ExistingShortcut(77, Launcher, options)]),
            () => ImportMode.SteamIntegration,
            () => false,
            resolveLauncher: () => Launcher);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);
        Assert.Equal("Skip", entry.Action);

        Assert.True((await source.SetModeAsync(
            entry.Id, nameof(ImportMode.ControllerOnly), false, CancellationToken.None)).Succeeded);

        var updated = Assert.Single(source.ReadState().Entries);
        Assert.Equal("Update", updated.Action);
        Assert.True(updated.Selectable);
    }

    [Fact]
    public async Task AnAcknowledgementTheUserAlreadyGaveSurvivesAScan()
    {
        // Losing it is not cosmetic: composing an update for an acknowledged multiplayer title
        // without the acknowledgement throws, so the update could never be applied.
        using TemporaryDirectory temporary = new();
        var game = Game(multiplayer: MultiplayerVerdict.Multiplayer);
        ImportStateStore store = new(temporary.GetPath("import.json"));
        store.Save(new ImportedEntry
        {
            Source = "xbox", Key = game.Key, Name = game.Name, AppId = 77,
            Target = Launcher, LaunchOptions = "--aumid Publisher.Game_abc!App --mode steam-overlay",
            Mode = nameof(ImportMode.SteamIntegration), Acknowledged = true
        });

        using SteamLibraryImportSource source = new(
            new FakeSource([game]),
            store,
            () => null,
            _ => Task.FromResult<IReadOnlyList<ExistingShortcut>>(
                [new ExistingShortcut(77, Launcher, "--aumid Publisher.Game_abc!App --mode controller-only")]),
            () => ImportMode.SteamIntegration,
            () => false,
            resolveLauncher: () => Launcher);

        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.Equal(nameof(ImportMode.SteamIntegration), entry.Mode);
        Assert.True(entry.Acknowledged);
    }

    [Fact]
    public async Task NothingTheSourcesCouldNotVouchForIsTickedForTheUser()
    {
        using TemporaryDirectory temporary = new();
        using var source = Source(temporary, Game(isGame: false));

        var state = await ScannedAsync(source);

        var entry = Assert.Single(state.Entries);
        Assert.True(entry.Selectable);
        Assert.False(entry.Selected);
        Assert.Equal(0, state.SelectedCount);
    }

    [Fact]
    public async Task AModePickedButNotAppliedSurvivesARescan()
    {
        // Every scan rebuilds the entries. A choice that lived only on the rebuilt object was lost
        // the moment the user scanned again, which is how an acknowledgement used to vanish.
        using TemporaryDirectory temporary = new();
        using var source = Source(temporary, Game());
        var entry = Assert.Single((await ScannedAsync(source)).Entries);
        await source.SetModeAsync(entry.Id, nameof(ImportMode.ControllerOnly), false, CancellationToken.None);

        var rescanned = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.Equal(nameof(ImportMode.ControllerOnly), rescanned.Mode);
    }

    [Fact]
    public async Task ATitleLeftOutStaysLeftOutAcrossRescansUntilOfferedAgain()
    {
        using TemporaryDirectory temporary = new();
        using var source = Source(temporary, Game());
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.True((await source.ExcludeAsync(entry.Id, CancellationToken.None)).Succeeded);
        var excluded = Assert.Single((await ScannedAsync(source)).Entries);
        Assert.True(excluded.Excluded);
        Assert.False(excluded.Selectable);
        Assert.False(excluded.Selected);

        Assert.True((await source.IncludeAsync(excluded.Id, CancellationToken.None)).Succeeded);
        var offered = Assert.Single((await ScannedAsync(source)).Entries);
        Assert.False(offered.Excluded);
        Assert.True(offered.Selectable);
    }

    [Fact]
    public async Task AnImportedTitleCannotBeLeftOut()
    {
        // Taking an imported title out of Steam is a removal, and deserves to be asked for as one.
        using TemporaryDirectory temporary = new();
        const string options = "--aumid Publisher.Game_abc!App --mode steam-overlay";
        ImportStateStore store = new(temporary.GetPath("import.json"));
        store.Save(new ImportedEntry
        {
            Source = "xbox", Key = "Publisher.Game_abc!App", Name = "Moonlit", AppId = 77,
            Target = Launcher, LaunchOptions = options, Mode = nameof(ImportMode.SteamIntegration)
        });
        using SteamLibraryImportSource source = new(
            new FakeSource([Game()]), store, () => null,
            _ => Task.FromResult<IReadOnlyList<ExistingShortcut>>([new ExistingShortcut(77, Launcher, options)]),
            () => ImportMode.SteamIntegration, () => false, resolveLauncher: () => Launcher);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.False((await source.ExcludeAsync(entry.Id, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task AStoredChoiceTheFactsNoLongerAllowIsNotHonoured()
    {
        // The choice was made for a single-player title. It became multiplayer since, and nobody
        // accepted that risk, so the stored overlay route is judged again and refused.
        using TemporaryDirectory temporary = new();
        ImportStateStore store = new(temporary.GetPath("import.json"));
        store.SaveChoice(new ImportChoice
        {
            Source = "xbox", Key = "Publisher.Game_abc!App", Mode = nameof(ImportMode.SteamIntegration)
        });
        using SteamLibraryImportSource source = new(
            new FakeSource([Game(multiplayer: MultiplayerVerdict.Multiplayer)]), store, () => null,
            _ => Task.FromResult<IReadOnlyList<ExistingShortcut>>([]),
            () => ImportMode.SteamIntegration, () => false);

        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.Equal(nameof(ImportMode.ControllerOnly), entry.Mode);
    }

    [Fact]
    public async Task ReroutingAnAdoptedEntryRewritesItRatherThanRecordingAModeItDoesNotHave()
    {
        // Adopting writes nothing. Recording a different mode than the shortcut launches with
        // would leave the record describing a game that starts some other way.
        using TemporaryDirectory temporary = new();
        const string options = "--aumid Publisher.Game_abc!App --mode steam-overlay";
        using SteamLibraryImportSource source = new(
            new FakeSource([Game()]), new ImportStateStore(temporary.GetPath("import.json")), () => null,
            _ => Task.FromResult<IReadOnlyList<ExistingShortcut>>([new ExistingShortcut(77, Launcher, options)]),
            () => ImportMode.SteamIntegration, () => false, resolveLauncher: () => Launcher);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);
        Assert.Equal("Adopt", entry.Action);

        await source.SetModeAsync(entry.Id, nameof(ImportMode.ControllerOnly), false, CancellationToken.None);

        Assert.Equal("Update", Assert.Single(source.ReadState().Entries).Action);
    }

    private sealed class FakeSource(IReadOnlyList<DiscoveredGame> games) : ILibrarySource
    {
        public string Id => "xbox";

        public string DisplayName => "Xbox";

        public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(games);
        }
    }
}
