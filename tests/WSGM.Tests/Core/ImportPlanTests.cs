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
        XboxRuntime runtime = XboxRuntime.NativeUwp,
        MultiplayerVerdict multiplayer = MultiplayerVerdict.SinglePlayer,
        bool isGame = true,
        string name = "Moonlit")
    {
        return new DiscoveredGame("xbox", key, name, @"C:\WindowsApps\Game", runtime,
            "Evidence.", multiplayer, "Evidence.", isGame, []);
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
    public void AChangedCommandLineIsAnUpdateRatherThanASecondEntry()
    {
        // Re-adding would lose the appid and every piece of artwork attached to it.
        var entry = Single([Game()], [Record(options: "--aumid " + Aumid + " --mode steam-overlay")],
            [Shortcut()]);

        Assert.Equal(ImportAction.Update, entry.Action);
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
    public void AnUninstalledTitleIsOfferedForRemovalButNeverPreselected()
    {
        var entry = Single([], [Record()], [Shortcut()]);

        Assert.Equal(ImportAction.Remove, entry.Action);
        Assert.False(entry.Selectable);
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
    public void AnUnknownRuntimeIsExcludedUnlessAskedFor()
    {
        Assert.Equal(ImportAction.Skip, Single([Game(runtime: XboxRuntime.Unknown)]).Action);
        Assert.Equal(ImportAction.Add,
            Single([Game(runtime: XboxRuntime.Unknown)], includeUnknown: true).Action);
    }

    [Fact]
    public void AnUnknownRuntimeCannotTakeTheOverlayRoute()
    {
        // There is no validated route, so there is nothing for the user to accept a risk about.
        var entry = Single([Game(runtime: XboxRuntime.Unknown)], includeUnknown: true);

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
    public void SomethingNeitherSourceCallsAGameIsNotOffered()
    {
        Assert.Equal(ImportAction.Skip, Single([Game(isGame: false)]).Action);
    }

    [Fact]
    public void IdentityIsTheKeyNotTheName()
    {
        // Two titles can share a display name; no two share an AUMID.
        var plan = ImportPlan.Build(
            [Game(key: "A_x!App", name: "Same"), Game(key: "B_y!App", name: "Same")],
            [], [], Launcher, ImportMode.SteamIntegration, false);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, entry => Assert.Equal(ImportAction.Add, entry.Action));
    }

    [Fact]
    public void ARecordWhoseEntryIsGoneFromSteamIsDroppedRatherThanRemoved()
    {
        var entry = Single([], [Record()], []);

        Assert.Equal(ImportAction.Skip, entry.Action);
    }

    [Fact]
    public void OwnershipNeedsBothTheTargetAndTheIdentity()
    {
        Assert.True(ImportPlan.Ours(Shortcut(), Launcher, Aumid));

        // Our launcher, somebody else's game.
        Assert.False(ImportPlan.Ours(
            Shortcut(options: "--aumid Other_z!App --mode controller-only"), Launcher, Aumid));

        // Our game named in a shortcut that runs something else.
        Assert.False(ImportPlan.Ours(Shortcut(target: @"""C:\other.exe"""), Launcher, Aumid));
    }

    [Fact]
    public void QuotingDoesNotChangeWhetherAnEntryIsOurs()
    {
        Assert.True(ImportPlan.Ours(Shortcut(target: Launcher), Launcher, Aumid));
        Assert.True(ImportPlan.Ours(Shortcut(target: "\"" + Launcher + "\""), Launcher, Aumid));
    }

    [Fact]
    public void EveryEntryCarriesAReasonTheUserCanRead()
    {
        var plan = ImportPlan.Build(
            [Game(), Game(key: "B_y!App", runtime: XboxRuntime.Unknown), Game(key: "C_z!App", isGame: false)],
            [Record(key: "gone!App", appId: 99u)],
            [],
            Launcher,
            ImportMode.SteamIntegration,
            false);

        Assert.All(plan, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Reason)));
        Assert.Equal(4, plan.Count);
    }
}
