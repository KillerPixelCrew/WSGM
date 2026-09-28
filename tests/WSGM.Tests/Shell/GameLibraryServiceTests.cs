using WSGM.Core;
using WSGM.Device.Tests;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>
///     The Game Library's backend. The acknowledgement and selection rules live here rather than in
///     the page, because a page defect must not be able to put a multiplayer title on the route that
///     injects into it, and every write to Steam goes through here.
/// </summary>
public sealed class GameLibraryServiceTests
{
    private const string Launcher = @"C:\WSGM\WSGM.PackagedLaunch.exe";
    private const string Aumid = "Publisher.Game_abc!App";

    private static DiscoveredGame Game(
        string key = Aumid,
        bool routable = true,
        MultiplayerVerdict multiplayer = MultiplayerVerdict.SinglePlayer,
        string name = "Moonlit",
        bool isGame = true)
    {
        return new DiscoveredGame("xbox", key, name, @"C:\WindowsApps\Game",
            new GameLaunch("UWP", routable, "Evidence."),
            multiplayer, "Evidence.", isGame, [], []);
    }

    private static DiscoveredGame EpicGame(string name = "Hades")
    {
        return DiscoveredGame.Command("epic", name, name, @"D:\Games\" + name,
        [
            new ShortcutRoute("direct", "Game executable", $@"D:\Games\{name}\{name}.exe", $@"D:\Games\{name}", "",
                "Evidence.")
        ]);
    }

    /// <summary>What WSGM writes for the packaged title in a mode.</summary>
    private static ShortcutFields Written(ImportMode mode, bool multiplayer = false, bool acknowledged = false)
    {
        return PackagedLauncherShortcut.Compose(Launcher, Aumid, mode, multiplayer, acknowledged);
    }

    private static ImportedEntry Recorded(
        ImportMode mode, uint appId = 77, bool acknowledged = false, bool multiplayer = false, bool ownsProfile = false)
    {
        var fields = Written(mode, multiplayer, acknowledged);
        return new ImportedEntry
        {
            Source = "xbox", Key = Aumid, Name = "Moonlit", AppId = appId, Target = fields.Target,
            LaunchOptions = fields.LaunchOptions, Mode = mode.ToString(), Acknowledged = acknowledged,
            ConfirmedUtc = "2026-09-24T00:00:00.0000000+00:00", OwnsProfile = ownsProfile
        };
    }

    private static async Task<GameLibraryState> ScannedAsync(GameLibraryService source)
    {
        await source.ScanAsync(CancellationToken.None);
        for (var attempt = 0; attempt < 300 && source.ReadState().Phase != "review"; attempt++)
        {
            await Task.Delay(10);
        }

        return source.ReadState();
    }

    private static async Task DoneAsync(GameLibraryService source)
    {
        for (var attempt = 0; attempt < 300 && source.ReadState().Phase is not ("done" or "review"); attempt++)
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task AScanPublishesAPreviewAndWritesNothing()
    {
        using Harness harness = new();
        using var source = harness.Create([Game()]);

        var state = await ScannedAsync(source);

        Assert.Equal("review", state.Phase);
        Assert.Single(state.Entries);
        Assert.Empty(harness.Calls);
        Assert.False(File.Exists(harness.StatePath));
    }

    [Fact]
    public async Task AScanNeverRunsOnTheCallersThread()
    {
        // The overlay calls this from its UI thread; a source reading a slow disk must not freeze it.
        using Harness harness = new();
        TaskCompletionSource<IReadOnlyList<DiscoveredGame>> held = new();
        using var source = harness.Create([], [new BlockingSource(held.Task)]);

        var answer = await source.ScanAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(answer.Succeeded);
        held.SetResult([Game()]);
        Assert.Single((await ScannedAsync(source)).Entries);
    }

    [Fact]
    public async Task AMultiplayerTitleCannotTakeTheOverlayRouteWithoutAnAcknowledgement()
    {
        using Harness harness = new();
        using var source = harness.Create([Game(multiplayer: MultiplayerVerdict.Multiplayer)]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        var refused = await source.SetModeAsync(entry.Id, nameof(ImportMode.SteamIntegration), false,
            CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal(nameof(ImportMode.ControllerOnly), Assert.Single(source.ReadState().Entries).Mode);
    }

    [Fact]
    public async Task AnAcknowledgedMultiplayerTitleMayTakeTheOverlayRoute()
    {
        using Harness harness = new();
        using var source = harness.Create([Game(multiplayer: MultiplayerVerdict.Multiplayer)]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        var accepted = await source.SetModeAsync(entry.Id, nameof(ImportMode.SteamIntegration), true,
            CancellationToken.None);

        Assert.True(accepted.Succeeded);
        var updated = Assert.Single(source.ReadState().Entries);
        Assert.Equal((nameof(ImportMode.SteamIntegration), true, "Steam overlay"),
            (updated.Mode, updated.Acknowledged, updated.LaunchLabel));
    }

    [Fact]
    public async Task CyclingAMultiplayerTitlesLaunchAsksForTheAcknowledgementInsteadOfSwitching()
    {
        using Harness harness = new();
        using var source = harness.Create([Game(multiplayer: MultiplayerVerdict.Multiplayer)]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        var answer = await source.CycleLaunchAsync(entry.Id, CancellationToken.None);

        Assert.True(answer.Succeeded);
        Assert.True(answer.Payload?.GetProperty("acknowledge").GetBoolean());
        Assert.Equal(nameof(ImportMode.ControllerOnly), Assert.Single(source.ReadState().Entries).Mode);
    }

    [Fact]
    public async Task CyclingASinglePlayerTitleSwitchesItsMode()
    {
        using Harness harness = new();
        using var source = harness.Create([Game()]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.True((await source.CycleLaunchAsync(entry.Id, CancellationToken.None)).Succeeded);

        Assert.Equal(nameof(ImportMode.ControllerOnly), Assert.Single(source.ReadState().Entries).Mode);
    }

    [Fact]
    public async Task AnUnclassifiedTitleCannotTakeTheOverlayRouteEvenWithAnAcknowledgement()
    {
        // There is no validated route, so there is nothing an acknowledgement could authorise.
        using Harness harness = new();
        using var source = harness.Create([Game(routable: false)], includeUnroutable: true);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        var refused = await source.SetModeAsync(entry.Id, nameof(ImportMode.SteamIntegration), true,
            CancellationToken.None);

        Assert.False(refused.Succeeded);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("999")]
    public async Task AModeThatNamesNothingIsRefusedRatherThanDefaulted(string mode)
    {
        using Harness harness = new();
        using var source = harness.Create([Game()]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.False((await source.SetModeAsync(entry.Id, mode, true, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task AStaleEntryIdIsRefusedRatherThanIgnored()
    {
        using Harness harness = new();
        using var source = harness.Create([Game()]);
        await ScannedAsync(source);

        Assert.False((await source.ToggleEntryAsync("no-such-entry", CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task ATitleKeepsItsIdAndItsSelectionAcrossRescans()
    {
        // An action a page sends after a rescan reaches the title it showed, not whichever title
        // now sits at the same position, and a title the user unticked stays unticked.
        using Harness harness = new();
        using var source = harness.Create([Game("A_x!App", name: "Alpha"), Game("B_y!App", name: "Beta")]);
        var before = (await ScannedAsync(source)).Entries.Single(entry => entry.Name == "Beta");
        await source.ToggleEntryAsync(before.Id, CancellationToken.None);

        var after = (await ScannedAsync(source)).Entries.Single(entry => entry.Name == "Beta");

        Assert.Equal(before.Id, after.Id);
        Assert.False(after.Selected);
    }

    [Fact]
    public async Task ApplyingWithNothingSelectedIsRefused()
    {
        using Harness harness = new();
        using var source = harness.Create([Game()]);
        await ScannedAsync(source);
        await source.SelectAsync("", "", false, CancellationToken.None);

        Assert.False((await source.ApplyAsync(CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task SelectAllNeverTicksARemovalOrAnAddSteamMayAlreadyHave()
    {
        using Harness harness = new();
        harness.Store.Save(new ImportedEntry
        {
            Source = "xbox", Key = "Gone_x!App", Name = "Gone", AppId = 90, Target = "t", LaunchOptions = "o",
            Mode = nameof(ImportMode.ControllerOnly), ConfirmedUtc = "2026-09-24T00:00:00.0000000+00:00"
        });
        harness.Store.Save(new ImportedEntry
        {
            Source = "xbox", Key = "Maybe_y!App", Name = "Maybe", Target = "t", LaunchOptions = "o",
            Mode = nameof(ImportMode.ControllerOnly)
        });
        using var source = harness.Create([Game(), Game("Maybe_y!App", name: "Maybe")]);
        await ScannedAsync(source);

        await source.SelectAsync("", "", true, CancellationToken.None);

        var selected = source.ReadState().Entries.Where(entry => entry.Selected).Select(entry => entry.Name);
        Assert.Equal(["Moonlit"], selected);
    }

    [Fact]
    public async Task SelectAllTakesOnlyWhatTheTabAndSearchShow()
    {
        using Harness harness = new();
        using var source = harness.Create([Game("A_x!App", name: "Alpha"), Game("B_y!App", name: "Beta")]);
        await ScannedAsync(source);
        await source.SelectAsync("", "", false, CancellationToken.None);

        await source.SelectAsync("new", "ALP", true, CancellationToken.None);

        Assert.Equal(["Alpha"],
            source.ReadState().Entries.Where(entry => entry.Selected).Select(entry => entry.Name));
    }

    [Fact]
    public async Task EveryPublicationCarriesAHigherRevision()
    {
        // The page redraws from this, so a change that does not move it is a change nobody sees.
        using Harness harness = new();
        using var source = harness.Create([Game()]);
        var before = (await ScannedAsync(source)).Revision;
        var entry = Assert.Single(source.ReadState().Entries);

        await source.ToggleEntryAsync(entry.Id, CancellationToken.None);

        Assert.True(source.ReadState().Revision > before);
        Assert.Equal(source.Revision, source.ReadState().Revision);
    }

    [Fact]
    public async Task RereadingAnUnchangedLibraryAnswersTheSameState()
    {
        using Harness harness = new();
        using var source = harness.Create([Game()]);
        await ScannedAsync(source);

        Assert.Same(source.ReadState(), source.ReadState());
    }

    [Fact]
    public async Task ReroutingAnAlreadyImportedTitleMakesItSomethingTheUserCanApply()
    {
        // The plan calls it a Skip, so without this the page shows the new mode, refuses the tick and
        // never rewrites the shortcut.
        using Harness harness = new();
        harness.Import(Recorded(ImportMode.SteamIntegration));
        using var source = harness.Create([Game()]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);
        Assert.Equal("Skip", entry.Action);

        Assert.True((await source.SetModeAsync(entry.Id, nameof(ImportMode.ControllerOnly), false,
            CancellationToken.None)).Succeeded);

        var updated = Assert.Single(source.ReadState().Entries);
        Assert.Equal(("Update", true), (updated.Action, updated.Selectable));
    }

    [Fact]
    public async Task AnUpdateRewritesTheStartDirectoryWithTheCommand()
    {
        using Harness harness = new();
        harness.Import(Recorded(ImportMode.SteamIntegration));
        using var source = harness.Create([Game()]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);
        await source.SetModeAsync(entry.Id, nameof(ImportMode.ControllerOnly), false, CancellationToken.None);
        await source.ToggleEntryAsync(entry.Id, CancellationToken.None);

        await source.ApplyAsync(CancellationToken.None);
        await DoneAsync(source);

        Assert.Equal(Written(ImportMode.ControllerOnly), Assert.Single(harness.Updated));
        Assert.Equal("Skip", Assert.Single(source.ReadState().Entries).Action);
    }

    [Fact]
    public async Task ARefusedUpdateLeavesTheRecordAsSteamStillHasIt()
    {
        // Recording the new command while Steam kept the old one reads as a hand edit on every scan.
        using Harness harness = new() { AcceptUpdates = false };
        harness.Import(Recorded(ImportMode.SteamIntegration));
        using var source = harness.Create([Game()]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);
        await source.SetModeAsync(entry.Id, nameof(ImportMode.ControllerOnly), false, CancellationToken.None);
        await source.ToggleEntryAsync(entry.Id, CancellationToken.None);

        await source.ApplyAsync(CancellationToken.None);
        await DoneAsync(source);

        Assert.NotNull(source.ReadState().Error);
        Assert.Equal(Written(ImportMode.SteamIntegration).LaunchOptions,
            Assert.Single(harness.Store.Entries()).LaunchOptions);
    }

    [Fact]
    public async Task AnAcknowledgementTheUserAlreadyGaveSurvivesAScan()
    {
        // Losing it is not cosmetic: composing an update for an acknowledged multiplayer title without
        // the acknowledgement throws, so the update could never be applied.
        using Harness harness = new();
        harness.Import(Recorded(ImportMode.SteamIntegration, acknowledged: true, multiplayer: true));
        using var source = harness.Create([Game(multiplayer: MultiplayerVerdict.Multiplayer)]);

        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.Equal((nameof(ImportMode.SteamIntegration), true), (entry.Mode, entry.Acknowledged));
    }

    [Fact]
    public async Task SomethingTheSourcesSayIsNotAGameIsNotListed()
    {
        using Harness harness = new();
        using var source = harness.Create([Game(isGame: false)]);

        Assert.Empty((await ScannedAsync(source)).Entries);
    }

    [Fact]
    public async Task AModePickedButNotAppliedSurvivesARescan()
    {
        // Every scan rebuilds the entries. A choice that lived only on the rebuilt object was lost the
        // moment the user scanned again, which is how an acknowledgement used to vanish.
        using Harness harness = new();
        using var source = harness.Create([Game()]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);
        await source.SetModeAsync(entry.Id, nameof(ImportMode.ControllerOnly), false, CancellationToken.None);

        var rescanned = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.Equal(nameof(ImportMode.ControllerOnly), rescanned.Mode);
    }

    [Fact]
    public async Task ATitleLeftOutStaysLeftOutAcrossRescansUntilOfferedAgain()
    {
        using Harness harness = new();
        using var source = harness.Create([Game()]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.True((await source.ExcludeAsync(entry.Id, CancellationToken.None)).Succeeded);
        var excluded = Assert.Single((await ScannedAsync(source)).Entries);
        Assert.Equal((true, false, false, "excluded"),
            (excluded.Excluded, excluded.Selectable, excluded.Selected, excluded.Group));

        Assert.True((await source.IncludeAsync(excluded.Id, CancellationToken.None)).Succeeded);
        var offered = Assert.Single((await ScannedAsync(source)).Entries);
        Assert.Equal((false, true), (offered.Excluded, offered.Selectable));
    }

    [Fact]
    public async Task AnImportedTitleCannotBeLeftOut()
    {
        // Taking an imported title out of Steam is a removal, and deserves to be asked for as one.
        using Harness harness = new();
        harness.Import(Recorded(ImportMode.SteamIntegration));
        using var source = harness.Create([Game()]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.False((await source.ExcludeAsync(entry.Id, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task AStoredChoiceTheFactsNoLongerAllowIsNotHonoured()
    {
        // The choice was made for a single-player title. It became multiplayer since, and nobody
        // accepted that risk, so the stored overlay route is judged again and refused.
        using Harness harness = new();
        harness.Store.SaveChoice(new ImportChoice
        {
            Source = "xbox", Key = Aumid, Mode = nameof(ImportMode.SteamIntegration)
        });
        using var source = harness.Create([Game(multiplayer: MultiplayerVerdict.Multiplayer)]);

        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.Equal(nameof(ImportMode.ControllerOnly), entry.Mode);
    }

    [Fact]
    public async Task ReroutingAnAdoptedEntryRewritesItRatherThanRecordingAModeItDoesNotHave()
    {
        // Adopting writes nothing. Recording a different mode than the shortcut launches with would
        // leave the record describing a game that starts some other way.
        using Harness harness = new();
        harness.Library.Add(new ExistingShortcut(77, Written(ImportMode.SteamIntegration).Target,
            Written(ImportMode.SteamIntegration).LaunchOptions));
        using var source = harness.Create([Game()]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);
        Assert.Equal("Adopt", entry.Action);

        await source.SetModeAsync(entry.Id, nameof(ImportMode.ControllerOnly), false, CancellationToken.None);

        Assert.Equal("Update", Assert.Single(source.ReadState().Entries).Action);
    }

    [Fact]
    public async Task AdoptingWritesNothingAndLeavesTheProfileAlone()
    {
        // The shortcut and any profile it has may be the user's own.
        using Harness harness = new();
        harness.Library.Add(new ExistingShortcut(77, Written(ImportMode.SteamIntegration).Target,
            Written(ImportMode.SteamIntegration).LaunchOptions));
        var overrides = 0;
        using var source = harness.Create([Game()], setControllerTarget: (_, _, _, _, _) =>
        {
            overrides++;
            return Task.FromResult(true);
        });
        var entry = Assert.Single((await ScannedAsync(source)).Entries);
        await source.ToggleEntryAsync(entry.Id, CancellationToken.None);

        await source.ApplyAsync(CancellationToken.None);
        await DoneAsync(source);

        Assert.Empty(harness.Calls);
        Assert.Equal(0, overrides);
        Assert.Equal(77u, Assert.Single(harness.Store.Entries()).AppId);
    }

    [Fact]
    public async Task ATitleNotInSteamYetCannotOpenTheArtworkPage()
    {
        using Harness harness = new();
        var opened = 0;
        using var source = harness.Create([Game()], openArtwork: (_, _, _) =>
        {
            opened++;
            return Task.FromResult(SteamUiCommandResult.Applied);
        });
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.False((await source.OpenArtworkAsync(entry.Id, CancellationToken.None)).Succeeded);
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task AnImportedTitleOpensTheArtworkPageUnderItsOwnName()
    {
        // The title travels with the request, because a shortcut created moments ago is not in Steam's
        // list yet and would otherwise be searched for as "App 77".
        using Harness harness = new();
        harness.Import(Recorded(ImportMode.SteamIntegration));
        (uint AppId, string Title)? asked = null;
        using var source = harness.Create([Game()], openArtwork: (appId, title, _) =>
        {
            asked = (appId, title);
            return Task.FromResult(SteamUiCommandResult.Applied);
        });
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        var answer = await source.OpenArtworkAsync(entry.Id, CancellationToken.None);

        Assert.True(answer.Succeeded);
        Assert.Equal((77u, "Moonlit"), asked);
        Assert.Equal("/wsgm/artwork/77", answer.Payload?.GetProperty("route").GetString());
    }

    [Fact]
    public async Task TheDetailsCarryTheEvidenceTheCardLeavesOut()
    {
        using Harness harness = new();
        using var source = harness.Create([Game()]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        var answer = await source.DetailsAsync(entry.Id, CancellationToken.None);

        Assert.True(answer.Succeeded);
        Assert.Equal(@"C:\WindowsApps\Game", answer.Payload?.GetProperty("installPath").GetString());
        Assert.Contains(Aumid, answer.Payload?.GetProperty("identity").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAddedTitleLearnsItsAppIdAndBecomesImported()
    {
        // Choosing artwork happens after the write, when Steam has given the entry an id. And a title
        // just imported must not keep offering itself as new.
        using Harness harness = new();
        var game = Game() with
        {
            Artwork =
            [
                new DiscoveredArtwork(ArtworkAsset.Grid, "https://store/a.png"),
                new DiscoveredArtwork(ArtworkAsset.Hero, "https://store/b.png")
            ]
        };
        using var source = harness.Create([game], applyArtwork: (_, images, _) =>
            Task.FromResult<IReadOnlyList<ArtworkResult>>([.. images.Select(_ => new ArtworkResult("ok", true))]));
        await ScannedAsync(source);

        Assert.True((await source.ApplyAsync(CancellationToken.None)).Succeeded);
        await DoneAsync(source);

        var entry = Assert.Single(source.ReadState().Entries);
        Assert.Equal("done", source.ReadState().Phase);
        Assert.Equal((harness.FirstAppId, (int?)2, "Skip", false),
            (entry.AppId, entry.ArtworkApplied, entry.Action, entry.Selected));
    }

    [Fact]
    public async Task RemovingANonXboxTitleDeletesItsShortcut()
    {
        // A removal's stand-in has no routes; it used to be taken for a packaged title and composed
        // with its plain source key, which threw and aborted every removal outside Xbox.
        using Harness harness = new();
        var fields = CommandShortcut.Compose(EpicGame().CommandRoutes[0], Launcher);
        harness.Import(new ImportedEntry
        {
            Source = "epic", Key = "Hades", Name = "Hades", AppId = 88, Target = fields.Target,
            LaunchOptions = fields.LaunchOptions, Mode = nameof(ImportMode.SteamIntegration), Route = "direct",
            ConfirmedUtc = "2026-09-24T00:00:00.0000000+00:00"
        });
        using var source = harness.Create([], [new FakeSource("epic", [EpicGame("Other")])]);
        var entry = (await ScannedAsync(source)).Entries.Single(candidate => candidate.Name == "Hades");
        Assert.Equal(("Remove", false), (entry.Action, entry.Packaged));
        await source.SelectAsync("", "", false, CancellationToken.None);
        await source.ToggleEntryAsync(entry.Id, CancellationToken.None);

        await source.ApplyAsync(CancellationToken.None);
        await DoneAsync(source);

        Assert.Null(source.ReadState().Error);
        Assert.Equal(["remove 88"], harness.Calls);
        Assert.Empty(harness.Store.Entries());
    }

    [Fact]
    public async Task ASourceThatFailedOffersNoneOfItsTitlesForRemoval()
    {
        using Harness harness = new();
        harness.Import(Recorded(ImportMode.SteamIntegration));
        using var source = harness.Create([], [new FakeSource("xbox", null)]);

        var state = await ScannedAsync(source);

        Assert.Empty(state.Entries);
        Assert.Contains("could not be read", state.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASourceThatReportsNothingOffersNoneOfItsImportsForRemoval()
    {
        // Every game uninstalled at once is far less likely than a launcher that was not read properly.
        using Harness harness = new();
        harness.Import(Recorded(ImportMode.SteamIntegration));
        using var source = harness.Create([]);

        var state = await ScannedAsync(source);

        Assert.Empty(state.Entries);
        Assert.Contains("reported no titles", state.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFixedMatchSurvivesSavingTheTitle()
    {
        // The next scan's artwork would otherwise go back to the wrong automatic match.
        using Harness harness = new();
        using var source = harness.Create([Game()]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);
        await source.SetMatchAsync(entry.Id, "steamgriddb", "7", "Moonlit", CancellationToken.None);

        await source.ApplyAsync(CancellationToken.None);
        await DoneAsync(source);

        Assert.Equal("7", Assert.Single(harness.Store.Choices()).MatchId);
        Assert.True(Assert.Single((await ScannedAsync(source)).Entries).MatchFixed);
    }

    [Fact]
    public async Task ATitleWhoseShortcutTheUserDeletedIsReAddedOnlyWhenAskedAndReleasesTheOldProfile()
    {
        using Harness harness = new();
        harness.Store.Save(Recorded(ImportMode.ControllerOnly, ownsProfile: true));
        List<(string Identity, ManagedControllerTarget? Target)> overrides = [];
        using var source = harness.Create([Game()], setControllerTarget: (identity, _, target, _, _) =>
        {
            overrides.Add((identity, target));
            return Task.FromResult(true);
        });
        var entry = Assert.Single((await ScannedAsync(source)).Entries);
        Assert.Equal(("Add", false), (entry.Action, entry.Selected));
        await source.ToggleEntryAsync(entry.Id, CancellationToken.None);

        await source.ApplyAsync(CancellationToken.None);
        await DoneAsync(source);

        Assert.Contains(("steam:77", (ManagedControllerTarget?)null), overrides);
        Assert.Equal(harness.FirstAppId, Assert.Single(harness.Store.Entries()).AppId);
    }

    [Fact]
    public async Task FillingEmptySlotsLeavesTheImagesATitleAlreadyHas()
    {
        using Harness harness = new();
        harness.Import(Recorded(ImportMode.SteamIntegration));
        var game = Game() with { Artwork = [new DiscoveredArtwork(ArtworkAsset.Grid, "https://store/a.png")] };
        using var source = harness.Create([game]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);
        await source.SetModeAsync(entry.Id, nameof(ImportMode.ControllerOnly), false, CancellationToken.None);
        await source.ToggleEntryAsync(entry.Id, CancellationToken.None);

        await source.FillArtworkAsync("Catalog", true, "", CancellationToken.None);
        Assert.All(Assert.Single(source.ReadState().Entries).Artwork, slot => Assert.Equal("keep", slot.Kind));

        await source.FillArtworkAsync("Catalog", false, "grid", CancellationToken.None);
        Assert.Equal("pick",
            Assert.Single(source.ReadState().Entries).Artwork.Single(slot => slot.Asset == "grid").Kind);
    }

    [Fact]
    public async Task TheListCannotChangeWhileAnApplyIsWorkingThroughIt()
    {
        // A mode changed between composing a shortcut and recording it would be recorded and pinned
        // while the shortcut still launched the old way.
        using Harness harness = new();
        TaskCompletionSource<IReadOnlyList<ExistingShortcut>> held = new();
        var reads = 0;
        harness.ReadLibrary = _ => ++reads == 1 ? Task.FromResult<IReadOnlyList<ExistingShortcut>>([]) : held.Task;
        using var source = harness.Create([Game()]);
        var entry = Assert.Single((await ScannedAsync(source)).Entries);
        Assert.True((await source.ApplyAsync(CancellationToken.None)).Succeeded);

        Assert.False((await source.SetModeAsync(entry.Id, nameof(ImportMode.ControllerOnly), false,
            CancellationToken.None)).Succeeded);
        Assert.False((await source.ToggleEntryAsync(entry.Id, CancellationToken.None)).Succeeded);
        Assert.False((await source.SelectAsync("", "", false, CancellationToken.None)).Succeeded);
        Assert.False((await source.ExcludeAsync(entry.Id, CancellationToken.None)).Succeeded);

        held.SetResult([]);
        await DoneAsync(source);
    }

    [Fact]
    public async Task AControllerOverrideThatFailedIsStillReportedWhenTheRunEnds()
    {
        // The record and the shortcut both say controller-only, so a rescan would show nothing wrong.
        // The completion message must not be what hides it.
        using Harness harness = new();
        using var source = harness.Create([Game(multiplayer: MultiplayerVerdict.Multiplayer)],
            setControllerTarget: (_, _, _, _, _) => throw new IOException("config is locked"));
        await ScannedAsync(source);

        await source.ApplyAsync(CancellationToken.None);
        await DoneAsync(source);

        Assert.Contains("Moonlit", source.ReadState().Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReroutingAnImportedTitleKeepsItsArtwork()
    {
        // Store art is for a shortcut the run created. An update may be a title whose capsule the user
        // already chose, and a launch-mode change must not replace it.
        using Harness harness = new();
        var record = Recorded(ImportMode.SteamIntegration);
        record.ArtworkApplied = 2;
        harness.Import(record);
        var applied = 0;
        using var source = harness.Create(
            [Game() with { Artwork = [new DiscoveredArtwork(ArtworkAsset.Grid, "https://s/a.png")] }],
            applyArtwork: (_, images, _) =>
            {
                applied++;
                return Task.FromResult<IReadOnlyList<ArtworkResult>>([
                    .. images.Select(_ => new ArtworkResult("ok", true))
                ]);
            });
        var entry = Assert.Single((await ScannedAsync(source)).Entries);
        await source.SetModeAsync(entry.Id, nameof(ImportMode.ControllerOnly), false, CancellationToken.None);
        await source.ToggleEntryAsync(entry.Id, CancellationToken.None);

        await source.ApplyAsync(CancellationToken.None);
        await DoneAsync(source);

        Assert.Equal(0, applied);
        Assert.Equal(2, Assert.Single(source.ReadState().Entries).ArtworkApplied);
    }

    [Fact]
    public async Task AControllerOnlyImportWithNothingManagingTheControllerIsReported()
    {
        // The override is written, but nothing will switch the controller while management is off.
        using Harness harness = new();
        using var source = harness.Create([Game(multiplayer: MultiplayerVerdict.Multiplayer)],
            setControllerTarget: (_, _, _, _, _) => Task.FromResult(false), controllerManaged: () => false);
        await ScannedAsync(source);

        await source.ApplyAsync(CancellationToken.None);
        await DoneAsync(source);

        Assert.Contains("controller management is off", source.ReadState().Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAddSteamNeverConfirmedIsNotTickedForTheUser()
    {
        using Harness harness = new();
        harness.Store.Save(new ImportedEntry
        {
            Source = "xbox", Key = Aumid, Name = "Moonlit", Target = Launcher,
            LaunchOptions = Written(ImportMode.SteamIntegration).LaunchOptions,
            Mode = nameof(ImportMode.SteamIntegration)
        });
        using var source = harness.Create([Game()]);

        var entry = Assert.Single((await ScannedAsync(source)).Entries);

        Assert.Equal((true, false), (entry.Selectable, entry.Selected));
    }

    [Fact]
    public async Task RemovingAnImportKeepsAProfileTheImportDidNotCreate()
    {
        // Adopted over a profile the user already had: clearing the override must not delete it.
        using Harness harness = new();
        harness.Import(Recorded(ImportMode.ControllerOnly));
        List<bool> removeEmpty = [];
        using var source = harness.Create([], [new FakeSource("xbox", [Game("Other_z!App", name: "Other")])],
            setControllerTarget: (_, _, target, remove, _) =>
            {
                Assert.Null(target);
                removeEmpty.Add(remove);
                return Task.FromResult(false);
            });
        var entry = (await ScannedAsync(source)).Entries.Single(candidate => candidate.Action == "Remove");
        await source.SelectAsync("", "", false, CancellationToken.None);
        await source.ToggleEntryAsync(entry.Id, CancellationToken.None);

        await source.ApplyAsync(CancellationToken.None);
        await DoneAsync(source);

        Assert.Equal([false], removeEmpty);
    }

    [Fact]
    public async Task AProfileTheImportCreatedIsRememberedSoItCanGoLater()
    {
        using Harness harness = new();
        using var source = harness.Create([Game(multiplayer: MultiplayerVerdict.Multiplayer)],
            setControllerTarget: (_, _, _, _, _) => Task.FromResult(true));
        await ScannedAsync(source);

        await source.ApplyAsync(CancellationToken.None);
        await DoneAsync(source);

        Assert.True(Assert.Single(harness.Store.Entries()).OwnsProfile);
    }

    [Fact]
    public async Task UntickingASourceTakesItsTitlesOutAtOnce()
    {
        using Harness harness = new();
        var settings = new GameLibraryConfig();
        using var source = harness.Create([Game()], settings: settings);
        Assert.Single((await ScannedAsync(source)).Entries);

        Assert.True((await source.SetSourceEnabledAsync("xbox", false, CancellationToken.None)).Succeeded);

        Assert.Empty(source.ReadState().Entries);
        Assert.Equal(["xbox"], settings.DisabledSources);
    }

    [Fact]
    public async Task AnImportFillsItsSourcesCollectionWhenTheSwitchIsOn()
    {
        using Harness harness = new();
        GameLibraryConfig settings = new() { CreateCollections = true };
        using var source = harness.Create([Game()], settings: settings);
        Assert.True(Assert.Single((await ScannedAsync(source)).Entries).Selected);

        await source.ApplyAsync(CancellationToken.None);
        await DoneAsync(source);

        var synced = Assert.Single(harness.Synced);
        Assert.Equal((null, "Xbox"), (synced.Id, synced.Name));
        Assert.Equal([harness.FirstAppId], synced.Add);
        Assert.Empty(synced.Remove);
        var collection = Assert.Single(harness.Store.Collections());
        Assert.Equal(("xbox", "uc-Xbox"), (collection.Group, collection.Id));
        Assert.True(source.ReadState().CreateCollections);
    }

    [Fact]
    public async Task TheSwitchOffMakesNoCollection()
    {
        using Harness harness = new();
        using var source = harness.Create([Game()], settings: new GameLibraryConfig());
        await ScannedAsync(source);

        await source.ApplyAsync(CancellationToken.None);
        await DoneAsync(source);

        Assert.Contains(harness.Calls, call => call.StartsWith("add ", StringComparison.Ordinal));
        Assert.Empty(harness.Synced);
    }

    [Fact]
    public async Task TurningTheSwitchOnBringsTitlesImportedBeforeIntoTheirCollection()
    {
        using Harness harness = new();
        harness.Import(Recorded(ImportMode.SteamIntegration, 77));
        GameLibraryConfig settings = new();
        using var source = harness.Create([Game()], settings: settings);

        Assert.True((await source.SetCollectionsAsync(true, CancellationToken.None)).Succeeded);
        for (var attempt = 0; attempt < 300 && harness.Store.Collections().Count == 0; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(settings.CreateCollections);
        Assert.Equal([77u], Assert.Single(harness.Synced).Add);
        Assert.Equal("uc-Xbox", Assert.Single(harness.Store.Collections()).Id);
    }

    [Fact]
    public async Task ATitleNoLongerImportedIsTakenBackAndAnEmptiedCollectionForgotten()
    {
        using Harness harness = new();
        harness.Store.SaveCollection(new ImportedCollection
            { Group = "xbox", Id = "uc-7", Name = "Xbox", AppIds = [77] });
        GameLibraryConfig settings = new();
        using var source = harness.Create([Game()], settings: settings);

        await source.SetCollectionsAsync(true, CancellationToken.None);
        for (var attempt = 0; attempt < 300 && harness.Store.Collections().Count > 0; attempt++)
        {
            await Task.Delay(10);
        }

        var synced = Assert.Single(harness.Synced);
        Assert.Equal("uc-7", synced.Id);
        Assert.Equal([77u], synced.Remove);
        Assert.Empty(synced.Add);
        Assert.Empty(harness.Store.Collections());
    }

    [Fact]
    public async Task AnUntickedSourcesCollectionIsLeftAlone()
    {
        using Harness harness = new();
        harness.Import(Recorded(ImportMode.SteamIntegration, 77));
        GameLibraryConfig settings = new() { DisabledSources = ["xbox"] };
        using var source = harness.Create([Game()], settings: settings);

        await source.SetCollectionsAsync(true, CancellationToken.None);
        await Task.Delay(100);

        Assert.Empty(harness.Synced);
    }

    /// <summary>A Steam library in memory, and a writer over it that records what it was asked.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly TemporaryDirectory _temporary = new();
        private uint _next = 2147483651u;

        internal Harness()
        {
            Store = new ImportStateStore(StatePath);
        }

        internal string StatePath => _temporary.GetPath("import.json");
        internal ImportStateStore Store { get; }
        internal List<ExistingShortcut> Library { get; } = [];
        internal List<string> Calls { get; } = [];
        internal List<ShortcutFields> Updated { get; } = [];
        internal bool AcceptUpdates { get; init; } = true;
        internal uint FirstAppId => 2147483651u;
        internal Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>>? ReadLibrary { get; set; }

        /// <summary>Every collection sync asked for: the recorded id, the name, the apps in and out.</summary>
        internal List<(string? Id, string Name, uint[] Add, uint[] Remove)> Synced { get; } = [];

        public void Dispose()
        {
            _temporary.Dispose();
        }

        private Task<SteamCollectionSyncResult> SyncCollection(
            string? id, string name, IReadOnlyCollection<uint> add, IReadOnlyCollection<uint> remove,
            CancellationToken cancellationToken)
        {
            lock (Synced)
            {
                Synced.Add((id, name, [.. add], [.. remove]));
            }

            return Task.FromResult(new SteamCollectionSyncResult(true, true,
                add.Count == 0 ? null : id ?? "uc-" + name, add.Count, null));
        }

        /// <summary>A title WSGM imported earlier: recorded, and in Steam as recorded.</summary>
        internal void Import(ImportedEntry record)
        {
            Store.Save(record);
            Library.Add(new ExistingShortcut(record.AppId, record.Target, record.LaunchOptions));
        }

        internal GameLibraryService Create(
            IReadOnlyList<DiscoveredGame> games,
            IReadOnlyList<ILibrarySource>? sources = null,
            bool includeUnroutable = false,
            Func<string, string, ManagedControllerTarget?, bool, CancellationToken, Task<bool>>? setControllerTarget =
                null,
            Func<bool>? controllerManaged = null,
            Func<uint, IReadOnlyList<(ArtworkAsset Asset, string Url)>, CancellationToken,
                Task<IReadOnlyList<ArtworkResult>>>? applyArtwork = null,
            Func<uint, string, CancellationToken, Task<SteamUiCommandResult>>? openArtwork = null,
            GameLibraryConfig? settings = null)
        {
            SteamShortcutWriter writer = new(
                (_, fields, _) =>
                {
                    var appId = _next++;
                    Calls.Add($"add {appId}");
                    Library.Add(new ExistingShortcut(appId, fields.Target, fields.LaunchOptions));
                    return Task.FromResult(new ShortcutWriteResult(appId, true, null));
                },
                (appId, fields, _) =>
                {
                    Calls.Add($"update {appId}");
                    if (!AcceptUpdates)
                    {
                        return Task.FromResult(false);
                    }

                    Updated.Add(fields);
                    Library.RemoveAll(shortcut => shortcut.AppId == appId);
                    Library.Add(new ExistingShortcut(appId, fields.Target, fields.LaunchOptions));
                    return Task.FromResult(true);
                },
                (appId, _) =>
                {
                    Calls.Add($"remove {appId}");
                    Library.RemoveAll(shortcut => shortcut.AppId == appId);
                    return Task.FromResult(true);
                });
            return new GameLibraryService(
                sources ?? [new FakeSource("xbox", games)],
                Store,
                () => writer,
                token => ReadLibrary?.Invoke(token)
                         ?? Task.FromResult<IReadOnlyList<ExistingShortcut>>([.. Library]),
                (appId, _) => Task.FromResult(Library.FirstOrDefault(shortcut => shortcut.AppId == appId)),
                () => ImportMode.SteamIntegration,
                () => includeUnroutable,
                applyArtwork,
                setControllerTarget,
                () => Launcher,
                openArtwork,
                controllerManaged,
                settings is null ? null : () => settings,
                settings is null ? null : change => change(settings),
                syncCollection: SyncCollection);
        }
    }

    private sealed class FakeSource(string id, IReadOnlyList<DiscoveredGame>? games) : ILibrarySource
    {
        public string Id => id;

        public string DisplayName => id == "xbox" ? "Xbox" : "Epic Games";

        public SourceAvailability Detect()
        {
            return new SourceAvailability(true, "Installed");
        }

        public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken)
        {
            return games is null
                ? Task.FromException<IReadOnlyList<DiscoveredGame>>(new IOException("the database is locked"))
                : Task.FromResult(games);
        }
    }

    private sealed class BlockingSource(Task<IReadOnlyList<DiscoveredGame>> games) : ILibrarySource
    {
        public string Id => "xbox";

        public string DisplayName => "Xbox";

        public SourceAvailability Detect()
        {
            return new SourceAvailability(true, "Installed");
        }

        public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken)
        {
            return games;
        }
    }
}
