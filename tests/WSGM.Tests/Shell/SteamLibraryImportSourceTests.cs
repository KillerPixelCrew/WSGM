using System.Threading;
using System.Threading.Tasks;
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
    private static DiscoveredGame Game(
        string key = "Publisher.Game_abc!App",
        XboxRuntime runtime = XboxRuntime.NativeUwp,
        MultiplayerVerdict multiplayer = MultiplayerVerdict.SinglePlayer,
        string name = "Moonlit")
    {
        return new DiscoveredGame("xbox", key, name, @"C:\WindowsApps\Game", runtime,
            "Evidence.", multiplayer, "Evidence.", true, []);
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
            new FakeSource([Game(runtime: XboxRuntime.Unknown)]),
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
            new FakeSource([Game(runtime: XboxRuntime.Unknown)]),
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
