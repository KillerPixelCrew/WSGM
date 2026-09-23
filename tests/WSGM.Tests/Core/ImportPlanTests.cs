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

    private static ImportedEntry Record(
        uint appId = 2147483650u,
        string key = Aumid,
        string target = "\"" + Launcher + "\"",
        string? options = null,
        string mode = nameof(ImportMode.ControllerOnly))
    {
        return new ImportedEntry
        {
            Source = "xbox",
            Key = key,
            AppId = appId,
            Name = "Moonlit",
            Target = target,
            LaunchOptions = options ?? $"--aumid {key} --mode controller-only",
            Mode = mode
        };
    }

    private static ExistingShortcut Shortcut(
        uint appId = 2147483650u, string? target = null, string? options = null)
    {
        return new ExistingShortcut(appId, target ?? "\"" + Launcher + "\"",
            options ?? $"--aumid {Aumid} --mode controller-only");
    }

    private static ImportPlanEntry Single(
        IReadOnlyList<DiscoveredGame> discovered,
        IReadOnlyList<ImportedEntry>? recorded = null,
        IReadOnlyList<ExistingShortcut>? existing = null,
        ImportMode mode = ImportMode.SteamIntegration,
        bool includeUnknown = false)
    {
        return Assert.Single(ImportPlan.Build(
            discovered, recorded ?? [], existing ?? [], Launcher, mode, includeUnknown));
    }

    [Fact]
    public void ANewTitleIsAdded()
    {
        var entry = Single([Game()]);

        Assert.Equal(ImportAction.Add, entry.Action);
        Assert.True(entry.Selectable);
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
        var entry = Single([Game()], [Record(options: "--aumid " + Aumid + " --mode steam-overlay")],
            [Shortcut()]);

        Assert.Equal(ImportAction.Conflict, entry.Action);
        Assert.False(entry.Selectable);
        Assert.Equal(2147483650u, entry.AppId);
    }

    [Fact]
    public void AnEntrySteamAlreadyHasIsAdoptedRatherThanDuplicated()
    {
        // Without this, re-running a sync after losing the record adds a second copy of every game.
        var entry = Single([Game()], [], [Shortcut()]);

        Assert.Equal(ImportAction.Adopt, entry.Action);
        Assert.Equal(2147483650u, entry.AppId);
    }

    [Fact]
    public void AnEntryChangedByHandIsLeftAloneAndNotSelectable()
    {
        var entry = Single([Game()], [Record()],
            [Shortcut(target: @"""C:\Somewhere\else.exe""")]);

        Assert.Equal(ImportAction.Conflict, entry.Action);
        Assert.False(entry.Selectable);
    }

    [Fact]
    public void AnUninstalledTitleIsOfferedForRemoval()
    {
        // Selectable, because a removal nobody can tick never happens. Whether it starts ticked is
        // the page backend's decision, and it does not.
        var entry = Single([], [Record()], [Shortcut()]);

        Assert.Equal(ImportAction.Remove, entry.Action);
        Assert.True(entry.Selectable);
    }

    [Fact]
    public void ARemovalNeedsTheRecordTheLiveEntryTheTargetAndTheIdentityToAgree()
    {
        // A user's own non-Steam shortcut must not be reachable by this path.
        var entry = Single([], [Record()],
            [Shortcut(options: "--aumid Someone.Else_xyz!App --mode controller-only")]);

        Assert.Equal(ImportAction.Conflict, entry.Action);
    }

    [Fact]
    public void ATitleWithNoValidatedRouteIsExcludedUnlessAskedFor()
    {
        Assert.Equal(ImportAction.Skip, Single([Game(routable: false)]).Action);
        Assert.Equal(ImportAction.Add,
            Single([Game(routable: false)], includeUnknown: true).Action);
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
    public void SomethingNeitherSourceCallsAGameIsOfferedWithThatSaidPlainly()
    {
        // Not hidden and not refused: nothing in a UWP manifest says "game", so an offline or
        // incomplete Store lookup makes every one of them look like an ordinary application.
        var entry = Single([Game(isGame: false)]);

        Assert.Equal(ImportAction.Add, entry.Action);
        Assert.True(entry.Selectable);
        Assert.Contains("Store", entry.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityIsTheKeyNotTheName()
    {
        // Two titles can share a display name; no two share an AUMID.
        var plan = ImportPlan.Build(
            [Game("A_x!App", name: "Same"), Game("B_y!App", name: "Same")],
            [], [], Launcher, ImportMode.SteamIntegration, false);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, entry => Assert.Equal(ImportAction.Add, entry.Action));
    }


    [Fact]
    public void EveryEntryCarriesAReasonTheUserCanRead()
    {
        var plan = ImportPlan.Build(
            [Game(), Game("B_y!App", false), Game("C_z!App", isGame: false)],
            [Record(key: "gone!App", appId: 99u)],
            [],
            Launcher,
            ImportMode.SteamIntegration,
            false);

        Assert.All(plan, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Reason)));
        Assert.Equal(4, plan.Count);
    }

    [Fact]
    public void ATitleThatIsGoneCanStillBeTickedForRemoval()
    {
        // An entry nobody can select is a removal that never happens, so the shortcut and the
        // record would stay forever while the scan kept reporting them.
        ImportedEntry record = new()
        {
            Source = "xbox", Key = "Publisher.Gone_abc!App", Name = "Gone", AppId = 42,
            Target = Launcher, LaunchOptions = "--aumid Publisher.Gone_abc!App --mode controller-only"
        };

        var entry = Assert.Single(ImportPlan.Build(
            [],
            [record],
            [new ExistingShortcut(42, Launcher, record.LaunchOptions)],
            Launcher,
            ImportMode.SteamIntegration,
            false));

        Assert.Equal(ImportAction.Remove, entry.Action);
        Assert.True(entry.Selectable);
    }

    [Fact]
    public void AdoptingAnEntryKeepsTheRouteItAlreadyLaunchesWith()
    {
        // Taking over a shortcut must not quietly change how the game starts.
        ExistingShortcut orphan = new(
            9, Launcher, "--aumid Publisher.Game_abc!App --mode controller-only");

        var entry = Assert.Single(ImportPlan.Build(
            [Game()], [], [orphan], Launcher, ImportMode.SteamIntegration, false));

        Assert.Equal(ImportAction.Adopt, entry.Action);
        Assert.Equal(ImportMode.ControllerOnly, entry.Mode);
    }

    [Fact]
    public void ARecordWhoseShortcutIsAlreadyGoneCanBeCleanedUp()
    {
        // Listed with no app id and selectable. Left unselectable it announced on every scan that
        // its record was about to be dropped, and nothing ever dropped it.
        var entry = Assert.Single(ImportPlan.Build(
            [], [Record()], [], Launcher, ImportMode.SteamIntegration, false));

        Assert.Equal(ImportAction.Remove, entry.Action);
        Assert.True(entry.Selectable);

        // The app id is kept so the controller override that entry left behind can be found.
        Assert.NotEqual(0u, entry.AppId);
    }

    [Fact]
    public void ARecordFromAnotherSourceIsNotThisTitle()
    {
        // Identity is the pair. Two sources may one day use the same key, and a record one wrote
        // must never make the other's title look imported, or be removed under its name.
        var record = Record();
        record.Source = "other";

        var plan = ImportPlan.Build([Game()], [record], [Shortcut()], Launcher,
            ImportMode.SteamIntegration, false);

        // The Xbox title finds its live shortcut unrecorded and adopts it; the other source's
        // record stays that source's, and is offered back under its own name.
        Assert.Collection(plan,
            xbox => Assert.Equal((ImportAction.Adopt, "xbox"), (xbox.Action, xbox.Source)),
            other => Assert.Equal((ImportAction.Remove, "other"), (other.Action, other.Source)));
    }

    [Fact]
    public void EveryEntryNamesTheSourceItBelongsTo()
    {
        var plan = ImportPlan.Build(
            [Game(), Game("B_y!App", source: "other")],
            [Record(key: "gone!App", appId: 99u)],
            [],
            Launcher,
            ImportMode.SteamIntegration,
            false);

        Assert.Equal(["xbox", "other", "xbox"], plan.Select(entry => entry.Source));
    }
}
