using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     What a sync would do, worked out without doing any of it. A scan is a dry run by
///     construction, and nothing here writes.
/// </summary>
public sealed class ImportPlanTests
{
    private const string Launcher = @"C:\WSGM\WSGM.PackagedLaunch.exe";
    private const string Aumid = "Publisher.Game_abc!App";
    private const string Epic = @"C:\Program Files (x86)\Epic Games\Launcher\EpicGamesLauncher.exe";
    private const string Uri = "com.epicgames.launcher://apps/ns%3Aid%3AHades?action=launch&silent=true";

    private static readonly ShortcutRoute ThroughLauncher =
        new("launcher", "Epic Games", Epic, @"C:\Program Files (x86)\Epic Games\Launcher", Uri, "Evidence.");

    private static readonly ShortcutRoute Direct =
        new("direct", "Game executable", @"D:\Games\Hades\Hades.exe", @"D:\Games\Hades", "-windowed", "Evidence.");

    private static DiscoveredGame Game(
        string key = Aumid,
        bool routable = true,
        MultiplayerVerdict multiplayer = MultiplayerVerdict.SinglePlayer,
        bool isGame = true,
        string name = "Moonlit",
        string source = "xbox")
    {
        return new DiscoveredGame(source, key, name, @"C:\WindowsApps\Game",
            new GameLaunch("UWP", routable, "Evidence."),
            multiplayer, "Evidence.", isGame, [], []);
    }

    private static DiscoveredGame CommandGame(params ShortcutRoute[] routes)
    {
        return DiscoveredGame.Command("epic", "Hades", "Hades", @"D:\Games\Hades",
            routes.Length == 0 ? [ThroughLauncher, Direct] : routes);
    }

    /// <summary>What WSGM writes for the packaged title today.</summary>
    private static ShortcutFields Written(string key = Aumid, string launcher = Launcher)
    {
        return PackagedLauncherShortcut.Compose(launcher, key, ImportMode.ControllerOnly, false, false);
    }

    private static ImportedEntry Record(
        uint appId = 2147483650u,
        string key = Aumid,
        string? target = null,
        string? options = null,
        string mode = nameof(ImportMode.ControllerOnly),
        bool confirmed = true)
    {
        var written = Written(key);
        return new ImportedEntry
        {
            Source = "xbox",
            Key = key,
            AppId = appId,
            Name = "Moonlit",
            Target = target ?? written.Target,
            LaunchOptions = options ?? written.LaunchOptions,
            Mode = mode,
            ConfirmedUtc = confirmed && appId > 0 ? "2026-09-24T00:00:00.0000000+00:00" : string.Empty
        };
    }

    private static ImportedEntry CommandRecord(ShortcutRoute route, uint appId = 3000000001u)
    {
        var fields = ShortcutTestFields.Compose(route, Launcher);
        return new ImportedEntry
        {
            Source = "epic",
            Key = "Hades",
            AppId = appId,
            Name = "Hades",
            Target = fields.Target,
            LaunchOptions = fields.LaunchOptions,
            Mode = nameof(ImportMode.SteamIntegration),
            Route = route.Id,
            ConfirmedUtc = "2026-09-27T00:00:00.0000000Z"
        };
    }

    private static ExistingShortcut Shortcut(uint appId = 2147483650u, string? target = null, string? options = null)
    {
        var written = Written();
        return new ExistingShortcut(appId, target ?? written.Target, options ?? written.LaunchOptions);
    }

    private static ExistingShortcut Live(ShortcutRoute route, uint appId = 3000000001u, string? options = null)
    {
        var fields = ShortcutTestFields.Compose(route, Launcher);
        return new ExistingShortcut(appId, fields.Target, options ?? fields.LaunchOptions);
    }

    private static IReadOnlyList<ImportPlanEntry> Plan(
        IReadOnlyList<DiscoveredGame> discovered,
        IReadOnlyList<ImportedEntry>? recorded = null,
        IReadOnlyList<ExistingShortcut>? existing = null,
        ImportMode mode = ImportMode.SteamIntegration,
        bool includeUnknown = false,
        Func<string, ImportSourceState>? sources = null,
        string launcher = Launcher)
    {
        return ImportPlan.Build(discovered, recorded ?? [], existing ?? [], launcher, mode, includeUnknown, sources);
    }

    private static ImportPlanEntry Single(
        IReadOnlyList<DiscoveredGame> discovered,
        IReadOnlyList<ImportedEntry>? recorded = null,
        IReadOnlyList<ExistingShortcut>? existing = null,
        ImportMode mode = ImportMode.SteamIntegration,
        bool includeUnknown = false)
    {
        return Assert.Single(Plan(discovered, recorded, existing, mode, includeUnknown));
    }

    [Fact]
    public void ANewTitleIsAddedAndStartsTicked()
    {
        var entry = Single([Game()]);

        Assert.Equal(ImportAction.Add, entry.Action);
        Assert.True(entry.Selectable);
        Assert.True(entry.Preselect);
    }

    [Fact]
    public void AnAlreadyImportedTitleIsSkipped()
    {
        var entry = Single([Game()], [Record()], [Shortcut()]);

        Assert.Equal(ImportAction.Skip, entry.Action);
        Assert.False(entry.Selectable);
    }

    [Fact]
    public void ACommandEditedInSteamIsLeftAloneRatherThanRestored()
    {
        // Our Target and key are still there, but the mode was switched by hand. Restoring the
        // recorded command would silently undo that, and re-adding would lose the id and its art.
        var entry = Single([Game()], [Record()],
            [Shortcut(options: Written().LaunchOptions.Replace("controller-only", "steam-overlay"))]);

        Assert.Equal(ImportAction.Conflict, entry.Action);
        Assert.False(entry.Selectable);
        Assert.Equal(2147483650u, entry.AppId);
    }

    [Fact]
    public void AShortcutWrittenBeforeWsgmMovedIsRewrittenRatherThanCalledAHandEdit()
    {
        // The record and the live entry agree; only the launcher's folder changed with WSGM.
        var old = Written(launcher: @"D:\Old\WSGM.PackagedLaunch.exe");
        var entry = Single([Game()], [Record(target: old.Target, options: old.LaunchOptions)],
            [Shortcut(target: old.Target, options: old.LaunchOptions)]);

        Assert.Equal(ImportAction.Update, entry.Action);
        Assert.True(entry.Selectable);
        Assert.True(entry.Preselect);
    }

    [Fact]
    public void AnEntrySteamAlreadyHasIsAdoptedRatherThanDuplicated()
    {
        // Without this, re-running a sync after losing the record adds a second copy of every game.
        var entry = Single([Game()], [], [Shortcut()]);

        Assert.Equal(ImportAction.Adopt, entry.Action);
        Assert.Equal(2147483650u, entry.AppId);
        Assert.False(entry.Preselect);
    }

    [Fact]
    public void AnEntryChangedByHandIsLeftAloneAndNotSelectable()
    {
        var entry = Single([Game()], [Record()], [Shortcut(target: @"""C:\Somewhere\else.exe""")]);

        Assert.Equal(ImportAction.Conflict, entry.Action);
        Assert.False(entry.Selectable);
    }

    [Fact]
    public void AnUninstalledTitleIsOfferedForRemovalButNeverTicked()
    {
        var entry = Single([], [Record()], [Shortcut()]);

        Assert.Equal(ImportAction.Remove, entry.Action);
        Assert.True(entry.Selectable);
        Assert.False(entry.Preselect);
    }

    [Fact]
    public void ARemovalNeedsTheLiveEntryToHoldExactlyWhatWasRecorded()
    {
        // A user's own non-Steam shortcut, or one they edited, must not be reachable by this path.
        var entry = Single([], [Record()],
            [Shortcut(options: Written().LaunchOptions + " --diagnostics")]);

        Assert.Equal(ImportAction.Conflict, entry.Action);
    }

    [Fact]
    public void ATitleWithNoValidatedRouteIsNotOfferedUnlessAskedFor()
    {
        Assert.Empty(Plan([Game(routable: false)]));
        Assert.Equal(ImportAction.Add, Single([Game(routable: false)], includeUnknown: true).Action);
    }

    [Fact]
    public void AnImportedTitleThatLostItsValidatedRouteIsStillHandled()
    {
        var entry = Single([Game(routable: false)], [Record()], [Shortcut()]);

        Assert.Equal(ImportAction.Skip, entry.Action);
    }

    [Fact]
    public void ATitleWithNoValidatedRouteCannotTakeTheOverlayRoute()
    {
        // There is no validated route, so there is nothing for the user to accept a risk about.
        var entry = Single([Game(routable: false)], includeUnknown: true);

        Assert.False(entry.CanUseSteamIntegration);
        Assert.False(entry.RequiresAcknowledgement);
        Assert.Equal(ImportMode.ControllerOnly, entry.Mode);
    }

    [Fact]
    public void AMultiplayerTitleDefaultsToControllerOnlyAndNeedsAnAcknowledgement()
    {
        var entry = Single([Game(multiplayer: MultiplayerVerdict.Multiplayer)]);

        Assert.Equal(ImportMode.ControllerOnly, entry.Mode);
        Assert.True(entry.CanUseSteamIntegration);
        Assert.True(entry.RequiresAcknowledgement);
    }

    [Fact]
    public void AnUnknownMultiplayerTagTakesTheDefaultRoute()
    {
        var entry = Single([Game(multiplayer: MultiplayerVerdict.Unknown)]);

        Assert.Equal(ImportMode.SteamIntegration, entry.Mode);
        Assert.False(entry.RequiresAcknowledgement);
    }

    [Fact]
    public void SomethingThatIsNotAGameIsLeftOutUnlessItWasImported()
    {
        // A failed Store lookup must not turn an imported title into a removal.
        Assert.Empty(Plan([Game(isGame: false)]));
        Assert.Equal(ImportAction.Skip, Single([Game(isGame: false)], [Record()], [Shortcut()]).Action);
    }

    [Fact]
    public void IdentityIsTheKeyNotTheName()
    {
        // Two titles can share a display name; no two share an AUMID.
        var plan = Plan([Game("A_x!App", name: "Same"), Game("B_y!App", name: "Same")]);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, entry => Assert.Equal(ImportAction.Add, entry.Action));
    }

    [Fact]
    public void EveryEntryCarriesAReasonTheUserCanRead()
    {
        var plan = Plan(
            [Game(), Game("B_y!App", false), Game("C_z!App", isGame: false)],
            [Record(key: "gone!App", appId: 99u)]);

        Assert.All(plan, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Reason)));
        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public void AdoptingAnEntryKeepsTheRouteItAlreadyLaunchesWith()
    {
        // Taking over a shortcut must not quietly change how the game starts.
        var entry = Single([Game()], [], [Shortcut(9)]);

        Assert.Equal(ImportAction.Adopt, entry.Action);
        Assert.Equal(ImportMode.ControllerOnly, entry.Mode);
    }

    [Fact]
    public void ARecordWhoseShortcutIsAlreadyGoneCanBeCleanedUp()
    {
        // Selectable. Left unselectable it announced on every scan that its record was about to be
        // dropped, and nothing ever dropped it.
        var entry = Single([], [Record()]);

        Assert.Equal(ImportAction.Remove, entry.Action);
        Assert.True(entry.Selectable);

        // The app id is kept so the controller override that entry left behind can be found.
        Assert.NotEqual(0u, entry.AppId);
    }

    [Fact]
    public void AShortcutAnotherTitlesRecordNamesIsNeitherAdoptedNorDoubleClaimed()
    {
        // Identity is the pair, and a recorded shortcut belongs to its record's title. Adopting it
        // under this title would let one run adopt it and then delete it as the other's removal.
        var record = Record();
        record.Source = "other";

        var plan = Plan([Game()], [record], [Shortcut()]);

        Assert.Collection(plan,
            xbox => Assert.Equal((ImportAction.Add, "xbox"), (xbox.Action, xbox.Source)),
            other => Assert.Equal((ImportAction.Remove, "other"), (other.Action, other.Source)));
    }

    [Fact]
    public void EveryEntryNamesTheSourceItBelongsTo()
    {
        var plan = Plan([Game(), Game("B_y!App", source: "other")], [Record(key: "gone!App", appId: 99u)]);

        Assert.Equal(["xbox", "other", "xbox"], plan.Select(entry => entry.Source));
    }

    [Fact]
    public void AnAddSteamNeverConfirmedIsOfferedButNeverTicked()
    {
        // The first add may still have gone through. Offered, because only the user can look, and
        // marked, so nothing ticks it for them and a second copy is not made by default.
        var entry = Single([Game()], [Record(0, confirmed: false)]);

        Assert.Equal(ImportAction.Add, entry.Action);
        Assert.True(entry.Unconfirmed);
        Assert.False(entry.Preselect);
        Assert.Contains("never confirmed", entry.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ATitleWhoseShortcutTheUserDeletedIsOfferedBackUntickedAndReplacesTheOldId()
    {
        // The user removed it in Steam. Adding it again is theirs to ask for, and the old id's
        // controller override has to go with the old shortcut.
        var entry = Single([Game()], [Record()]);

        Assert.Equal(ImportAction.Add, entry.Action);
        Assert.False(entry.Unconfirmed);
        Assert.False(entry.Preselect);
        Assert.Equal(2147483650u, entry.ReplacedAppId);
    }

    [Fact]
    public void ADuplicateAppIdInTheLibraryDoesNotFailTheScan()
    {
        var plan = Plan([Game()], [Record()], [Shortcut(), Shortcut()]);

        Assert.Equal(ImportAction.Skip, Assert.Single(plan).Action);
    }

    [Fact]
    public void AnUntickedOrFailedSourceIsNeitherReadNorOfferedForRemoval()
    {
        var plan = Plan([], [Record()], [Shortcut()], sources: _ => ImportSourceState.Unread);

        Assert.Empty(plan);
    }

    [Fact]
    public void TheTitlesOfASourceThatIsGoneCanBeRemoved()
    {
        // An uninstalled launcher or a removed folder: nothing else would ever offer its shortcuts.
        var entry = Assert.Single(Plan([], [Record()], [Shortcut()], sources: _ => ImportSourceState.Gone));

        Assert.Equal(ImportAction.Remove, entry.Action);
        Assert.Contains("no longer on this machine", entry.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewLauncherTitleIsAddedOnItsFirstRoute()
    {
        var entry = Single([CommandGame()]);

        Assert.Equal(ImportAction.Add, entry.Action);
        Assert.Equal("launcher", entry.Route);
        Assert.False(entry.RequiresAcknowledgement);
    }

    [Fact]
    public void AnUnrecordedShortcutRunningOneOfItsRoutesIsAdoptedOnThatRoute()
    {
        var entry = Single([CommandGame()], [], [Live(Direct)]);

        Assert.Equal(ImportAction.Adopt, entry.Action);
        Assert.Equal("direct", entry.Route);
    }

    [Fact]
    public void ARecordedShortcutStillRunningWhatWasWrittenIsSkipped()
    {
        var entry = Single([CommandGame()], [CommandRecord(Direct)], [Live(Direct)]);

        Assert.Equal(ImportAction.Skip, entry.Action);
        Assert.Equal("direct", entry.Route);
    }

    [Fact]
    public void ARecordedShortcutWhoseArgumentsWereEditedIsAConflict()
    {
        var entry = Single([CommandGame()], [CommandRecord(Direct)], [Live(Direct, options: "-windowed -dx11")]);

        Assert.Equal(ImportAction.Conflict, entry.Action);
    }

    [Fact]
    public void ALauncherTitleWhoseCommandMovedIsRewritten()
    {
        // The install moved; the recorded shortcut is untouched, and now runs a program that is gone.
        var moved = Direct with { Target = @"E:\Games\Hades\Hades.exe", StartDirectory = @"E:\Games\Hades" };

        var entry = Single([CommandGame(ThroughLauncher, moved)], [CommandRecord(Direct)], [Live(Direct)]);

        Assert.Equal(ImportAction.Update, entry.Action);
        Assert.True(entry.Preselect);
        Assert.Equal("direct", entry.Route);
    }

    [Fact]
    public void ATitleWhoseImportedRouteIsGoneIsOfferedTheFirstRouteUnticked()
    {
        var entry = Single([CommandGame(Direct)], [CommandRecord(ThroughLauncher)], [Live(ThroughLauncher)]);

        Assert.Equal(ImportAction.Update, entry.Action);
        Assert.Equal("direct", entry.Route);
        Assert.False(entry.Preselect);
    }

    [Fact]
    public void AGoneLauncherTitleWhoseShortcutIsUnchangedIsOfferedForRemoval()
    {
        var entry = Single([], [CommandRecord(Direct)], [Live(Direct)]);

        Assert.Equal(ImportAction.Remove, entry.Action);
        Assert.True(entry.Selectable);
    }

    [Fact]
    public void AFollowRouteThatCannotBeComposedDoesNotFailTheScan()
    {
        // Without the launcher the follow route refuses itself; the other titles are still planned.
        var followed = ThroughLauncher with { FollowDirectory = @"D:\Games\Hades" };

        var plan = Plan([CommandGame(followed), Game()], existing: [Shortcut()], launcher: string.Empty);

        Assert.Equal(2, plan.Count);
    }
}
